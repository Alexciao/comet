using System.Collections.Concurrent;
using System.Diagnostics;
using Comet.Core.AutoReplace;
using Comet.Core.Input;
using Comet.Core.Search;
using Comet.Core.Settings;
using Comet.Platform.Context;
using Comet.Platform.Hook;
using Comet.Platform.Input;
using Comet.Platform.Native;

namespace Comet.Platform.Engine;

/// <summary>
/// The brain, running on the hook thread: translates keys, drives the picker session and auto-replace,
/// searches, decides synchronously what to swallow, and hands insertions to the injector. Everything it
/// owns is touched only on the hook thread; other threads talk to it through posted messages.
/// </summary>
internal sealed class InputEngine : IHookClient, IExactMatcher, IDisposable
{
    private const uint WmConfig = HookThread.WmClientBase;
    private const uint WmData = HookThread.WmClientBase + 1;
    private const uint WmContextResult = HookThread.WmClientBase + 2;
    private const uint WmInjected = HookThread.WmClientBase + 3;
    private const uint WmCancel = HookThread.WmClientBase + 4;
    private const uint WmDispatchInjection = HookThread.WmClientBase + 5;

    /// <summary>Give up holding keys if an insertion hasn't completed by then (keys are then replayed).</summary>
    private const long MaxInjectionMs = 1000;

    // Apps known to mishandle KEYEVENTF_UNICODE input: remote desktops and VMs forward scan codes, Java
    // Swing drops surrogate pairs.
    private static readonly HashSet<string> s_clipboardApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "mstsc.exe", "msrdc.exe", "vmconnect.exe", "virtualboxvm.exe", "vmware-vmx.exe", "vmware.exe", "javaw.exe", "java.exe",
    };

    private readonly IPopupSink _popup;
    private readonly HookThread _hook;
    private readonly ContextWorker _worker;
    private readonly TextInjector _injector;
    private readonly KeyTranslator _translator = new();
    private readonly PickerSession _picker = new();
    private readonly AutoReplaceEngine _autoReplace = new();
    private readonly PopupFrame _frame = new();
    private readonly SearchResult[] _results = new SearchResult[PopupFrame.MaxResults];
    private readonly bool[] _swallowedDown = new bool[256];
    private readonly List<(KBDLLHOOKSTRUCT Key, bool Up)> _held = new(16);
    private readonly ConcurrentQueue<ContextResult> _contextResults = new();
    private InsertionJob? _queuedJob;

    private EngineConfig _config = new();
    private EngineConfig? _pendingConfig;
    private EngineData _data = EngineData.Empty;
    private EngineData? _pendingData;

    private FocusContext _context = FocusContext.None;
    private int _generation;
    private bool _passwordRequested;
    private bool _caretRequested;
    private RECT _anchor;
    private bool _anchorKnown;
    private int _resultCount;
    private int _selected;
    private bool _popupVisible;
    private bool _swallowNextMouseUp;
    private bool _injecting;
    private long _injectingSince;
    private long _keyTimestamp;
    private bool _mouseHookWanted;
    private long _lastWatchdogKick;

    public InputEngine(IPopupSink popup, bool acceptForeignInjectedInput = false)
    {
        _popup = popup;
        _picker.ExactMatcher = this;
        _hook = new HookThread(this) { AcceptForeignInjectedInput = acceptForeignInjectedInput };
        _worker = new ContextWorker(r =>
        {
            _contextResults.Enqueue(r);
            _hook.Post(WmContextResult);
        });
        _injector = new TextInjector((job, accepted) => _hook.Post(WmInjected, accepted));
    }

    /// <summary>Raised on the hook thread when the user picks something (frecency key, e.g. the emoji or "snippet:id").</summary>
    public event Action<string>? Picked;

    /// <summary>Hook overhead per keystroke when nothing is triggered.</summary>
    public LatencyStats IdleKeyLatency { get; } = new("hook idle keystroke");

    /// <summary>Hook time per keystroke while a picker session is active (includes the search).</summary>
    public LatencyStats ActiveKeyLatency { get; } = new("hook active keystroke");

    public HookThread Hook => _hook;

    public TextInjector Injector => _injector;

    public bool HookInstalled => _hook.KeyboardHookInstalled;

    public void Start(EngineConfig config, EngineData data)
    {
        _config = config;
        _data = data;
        ApplyConfig(config);
        _autoReplace.SetRules(data.Rules);
        _hook.Start(config.NeedsHook);
        _hook.Post(WmCancel); // capture the initial focus context on the hook thread
    }

    /// <summary>Applies new settings (any thread). Installs or removes the hook as needed.</summary>
    public void SetConfig(EngineConfig config)
    {
        Volatile.Write(ref _pendingConfig, config);
        _hook.Post(WmConfig);
    }

    /// <summary>Hands over new search data (any thread). Swapped in when no popup is showing.</summary>
    public void SetData(EngineData data)
    {
        Volatile.Write(ref _pendingData, data);
        _hook.Post(WmData);
    }

    /// <summary>Closes the popup and forgets any session (any thread).</summary>
    public void Cancel() => _hook.Post(WmCancel);

    /// <summary>The data currently in use (for tests).</summary>
    internal EngineData CurrentData => Volatile.Read(ref _data);

    // ------------------------------------------------------------------ keyboard

    public bool OnKey(in KBDLLHOOKSTRUCT k, bool isKeyDown, bool isReplay)
    {
        var vk = k.vkCode & 0xFF;
        if (!isKeyDown)
        {
            if (_swallowedDown[vk])
            {
                _swallowedDown[vk] = false;
                return true;
            }

            if (!isReplay && _injecting && !InjectionTimedOut())
            {
                _held.Add((k, true));
                return true;
            }

            return false;
        }

        // Replayed keys are the held ones being delivered in order: never hold them again.
        if (!isReplay && _injecting && !InjectionTimedOut())
        {
            // Hold keys typed during an insertion so they land after it, in order.
            _held.Add((k, false));
            return true;
        }

        var start = Stopwatch.GetTimestamp();
        _keyTimestamp = start;
        var wasActive = _picker.IsActive;

        Span<KeyInput> inputs = stackalloc KeyInput[2];
        var layout = User32.GetKeyboardLayout(_context.ThreadId);
        var n = _translator.Translate(vk, k.scanCode, ModifierState.Read(k.flags), layout, inputs);
        var swallow = false;
        for (var i = 0; i < n; i++)
            swallow |= Process(inputs[i]);

        if (swallow)
            _swallowedDown[vk] = true;

        UpdateMouseHook();
        var micros = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        (wasActive || _picker.IsActive ? ActiveKeyLatency : IdleKeyLatency).Add(micros);
        return swallow;
    }

    private bool InjectionTimedOut()
    {
        if (Environment.TickCount64 - _injectingSince <= MaxInjectionMs)
            return false;
        Log.Warn("insertion did not complete in time; releasing held keys");
        FinishInjection();
        return true;
    }

    private bool Process(KeyInput key)
    {
        var config = _config;

        // ---- picker
        if (config.PickerEnabled && PickerAllowedHere(config))
        {
            var d = _picker.OnKey(key, _popupVisible);
            switch (d.Action)
            {
                case PickerAction.QueryChanged:
                    OnQueryChanged();
                    break;
                case PickerAction.Cancel:
                    EndSession();
                    break;
                case PickerAction.SelectNext:
                    MoveSelection(+1);
                    break;
                case PickerAction.SelectPrevious:
                    MoveSelection(-1);
                    break;
                case PickerAction.CommitSelected:
                    if (_popupVisible && _resultCount > 0)
                        Insert(_results[_selected], d.DeleteCount, after: null);
                    else
                        EndSession();
                    break;
                case PickerAction.CommitExact:
                case PickerAction.CommitExactThenSpace:
                    if (_data.Search.FindExact(_picker.CommittedToken) is { } exact)
                        Insert(exact, d.DeleteCount, d.Action == PickerAction.CommitExactThenSpace ? new Boundary(BoundaryKind.Char, ' ') : null);
                    else
                        EndSession();
                    break;
            }

            if (d.Action is PickerAction.CommitSelected or PickerAction.CommitExact or PickerAction.CommitExactThenSpace)
            {
                _autoReplace.NotifyInserted();
                return d.Swallow;
            }

            if (d.Swallow)
                return true; // navigation inside the popup: the caret didn't move, auto-replace keeps its word
        }
        else if (_picker.IsActive)
        {
            _picker.Reset();
            EndSession();
        }

        // ---- auto-replace
        if (!config.AutoReplaceActive)
            return false;

        var allowed = _context.IsSafe && !config.AutoReplaceExcluded.Contains(_context.ProcessName);
        var a = _autoReplace.OnKey(key, allowed);
        switch (a.Action)
        {
            case AutoReplaceAction.Expand:
            case AutoReplaceAction.Undo:
                StartInjection(new InsertionJob(a.DeleteCount, a.Text, null, a.Boundary, MethodFor(_context.ProcessName), _keyTimestamp, _context.IsTerminal));
                return true;
        }

        if (_autoReplace.IsCandidate)
            EnsurePasswordKnown();
        return false;
    }

    private bool PickerAllowedHere(EngineConfig config) =>
        !_context.Elevated && !_context.IsSelf && !config.PickerExcluded.Contains(_context.ProcessName);

    // ------------------------------------------------------------------ picker session

    private void OnQueryChanged()
    {
        if (!_picker.IsActive)
            return;

        if (!_caretRequested)
        {
            // Session just started at the trigger: ask for the caret now, while the user types the next characters.
            _caretRequested = true;
            _anchorKnown = false;
            _worker.Request(ContextRequestKind.Caret, _context);
            EnsurePasswordKnown();
        }

        if (!_picker.HasQuery)
        {
            _resultCount = 0;
            Publish();
            return;
        }

        _resultCount = _data.Search.Search(_picker.Token, _results);
        _selected = 0;
        Publish();
    }

    private void MoveSelection(int delta)
    {
        if (_resultCount == 0)
            return;
        _selected = (_selected + delta + _resultCount) % _resultCount;
        Publish();
    }

    /// <summary>Shows, updates or hides the popup to match the current state.</summary>
    private void Publish()
    {
        var visible = _picker.HasQuery && _resultCount > 0 && _anchorKnown && _context.IsSafe;
        if (!visible)
        {
            if (_popupVisible)
                _popup.Hide();
            _popupVisible = false;
            return;
        }

        lock (_frame.Gate)
        {
            _frame.Version++;
            _frame.Count = _resultCount;
            Array.Copy(_results, _frame.Results, _resultCount);
            _frame.Selected = _selected;
            var token = _picker.Token;
            token.CopyTo(_frame.Query);
            _frame.QueryLength = token.Length;
            _frame.Anchor = _anchor;
            _frame.SkinTone = _config.SkinTone;
            _frame.Data = _data;
            _frame.KeyTimestamp = _keyTimestamp;
        }

        _popupVisible = true;
        _popup.Update(_frame);
    }

    private void EndSession()
    {
        if (_popupVisible)
            _popup.Hide();
        _popupVisible = false;
        _resultCount = 0;
        _caretRequested = false;
        _anchorKnown = false;
        ApplyPendingData();
    }

    private void Insert(SearchResult result, int deleteCount, Boundary? after)
    {
        var data = _data;
        string? text = null, template = null, key;
        if (result.Kind == ResultKind.Emoji && data.Emoji is { } emoji)
        {
            text = emoji.Emoji(result.Index, _config.SkinTone).ToString();
            key = Frecency.EmojiKey(emoji.Emoji(result.Index));
        }
        else if (result.Kind == ResultKind.Snippet && result.Index < data.Snippets.Length)
        {
            template = data.Snippets[result.Index].Text;
            key = Frecency.SnippetKey(data.Snippets[result.Index].Id);
        }
        else
        {
            EndSession();
            return;
        }

        // Hide first, with no animation: the text lands in the same frame the popup disappears.
        EndSession();
        _picker.NotifyInserted();
        StartInjection(new InsertionJob(deleteCount, text, template, after, MethodFor(_context.ProcessName), _keyTimestamp, _context.IsTerminal));
        data.Search.BumpFrecency(result.Kind, result.Index);
        Picked?.Invoke(key);
    }

    private InsertionMethod MethodFor(string process) =>
        s_clipboardApps.Contains(process) || (_config.InsertionMethods.TryGetValue(process, out var m) && m == "clipboard")
            ? InsertionMethod.Clipboard
            : InsertionMethod.Unicode;

    private void StartInjection(InsertionJob job)
    {
        Log.Debug($"inject: delete {job.DeleteCount}, text {job.Text?.Length ?? -1}, snippet {job.SnippetTemplate is not null}, replay {job.Replay?.Length ?? 0}, method {job.Method}");
        _injecting = true;
        _injectingSince = Environment.TickCount64;
        // Hand the job over only after the current hook callback has returned: injecting while the system is
        // still waiting on our callback for the key that triggered it corrupts VK_PACKET surrogate pairs.
        _queuedJob = job;
        _hook.Post(WmDispatchInjection);
    }

    private void FinishInjection()
    {
        Log.Debug($"inject done after {Environment.TickCount64 - _injectingSince} ms, {_held.Count} keys held");
        _injecting = false;
        if (_held.Count == 0)
            return;
        var keys = new KBDLLHOOKSTRUCT[_held.Count];
        var ups = new bool[_held.Count];
        for (var i = 0; i < _held.Count; i++)
            (keys[i], ups[i]) = _held[i];
        _held.Clear();
        // Replayed keys come back through the hook tagged as real typing, in their original order.
        StartInjection(new InsertionJob(0, null, null, null, InsertionMethod.Unicode, 0) { Replay = keys, ReplayKeyUp = ups });
    }

    // ------------------------------------------------------------------ IExactMatcher

    bool IExactMatcher.HasExact(ReadOnlySpan<char> token) =>
        _context.IsSafe && _data.Search.FindExact(token) is not null;

    // ------------------------------------------------------------------ mouse

    public bool OnMouseButton(uint message, POINT point)
    {
        if (message == User32.WM_LBUTTONUP && _swallowNextMouseUp)
        {
            _swallowNextMouseUp = false;
            return true;
        }

        if (_popupVisible && _popup.Geometry is { } g && Contains(g.Window, point))
        {
            if (message == User32.WM_LBUTTONDOWN)
            {
                // Handle the click here and never let it reach the popup, so the popup can't take focus.
                var row = g.RowHeight > 0 ? g.FirstVisible + ((point.Y - g.Window.Top - g.ListTop) / g.RowHeight) : -1;
                if (row >= 0 && row < _resultCount && point.Y >= g.Window.Top + g.ListTop)
                {
                    var token = _picker.Token.Length;
                    _picker.Reset();
                    _autoReplace.NotifyInserted();
                    Insert(_results[row], 1 + token, after: null);
                }

                _swallowNextMouseUp = true;
            }

            return true;
        }

        // A click anywhere else moves the caret: forget the token and the word.
        if (_picker.IsActive)
        {
            _picker.Reset();
            EndSession();
        }

        _autoReplace.Reset();
        UpdateMouseHook();
        return false;
    }

    private static bool Contains(RECT r, POINT p) => p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;

    /// <summary>The mouse hook costs a context switch per mouse event, so it is installed only while a click must reset state.</summary>
    private void UpdateMouseHook()
    {
        var want = _picker.IsActive || _autoReplace.CurrentWord.Length > 0;
        if (want != _mouseHookWanted)
        {
            _mouseHookWanted = want;
            _hook.Post(HookThread.WmSetMouseHook, want ? 1u : 0u);
        }
    }

    // ------------------------------------------------------------------ focus

    public void OnWinEvent(uint winEvent, nint hwnd, int idObject)
    {
        if (winEvent == User32.EVENT_SYSTEM_FOREGROUND || hwnd != _context.Focus)
        {
            ResetForNewFocus();
            KickWatchdog();
            return;
        }

        // Focus moved between elements of the same window (e.g. fields in a web page). Clicks and Tab already
        // reset the session; here we only re-check whether the new element is a password field.
        _generation++;
        _context = _context with { Generation = _generation };
        if (_picker.IsActive || _autoReplace.CurrentWord.Length > 0)
        {
            _worker.Request(ContextRequestKind.Password, _context);
        }
        else
        {
            _context = FocusContext.Capture(_generation);
            _passwordRequested = false;
        }
    }

    private void ResetForNewFocus()
    {
        _picker.Reset();
        _autoReplace.Reset();
        EndSession();
        _generation++;
        _context = FocusContext.Capture(_generation);
        _passwordRequested = false;
        _translator.Resync();
        UpdateMouseHook();
    }

    private void EnsurePasswordKnown()
    {
        if (_context.Password != Tristate.Unknown || _passwordRequested || _context.Elevated || _context.IsSelf)
            return;
        _passwordRequested = true;
        _worker.Request(ContextRequestKind.Password, _context);
    }

    private void OnContextResult(ContextResult r)
    {
        if (r.Generation != _context.Generation)
            return; // answer for a focus that no longer exists

        if (r.Kind == ContextRequestKind.Password)
        {
            _context = _context with { Password = r.Password };
            if (r.Password == Tristate.Yes)
            {
                _picker.Reset();
                _autoReplace.Reset();
                EndSession();
                return;
            }
        }
        else if (_picker.IsActive)
        {
            _anchor = r.Caret;
            _anchorKnown = r.Method != CaretMethod.None;
        }

        if (_picker.IsActive)
            Publish();
    }

    // ------------------------------------------------------------------ hook-thread messages

    public void OnThreadMessage(uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case WmConfig:
                if (Interlocked.Exchange(ref _pendingConfig, null) is { } config)
                    ApplyConfig(config);
                break;
            case WmData:
                if (!_popupVisible)
                    ApplyPendingData();
                break;
            case WmContextResult:
                while (_contextResults.TryDequeue(out var r))
                    OnContextResult(r);
                break;
            case WmInjected:
                FinishInjection();
                break;
            case WmDispatchInjection:
                if (_queuedJob is { } job)
                {
                    _queuedJob = null;
                    _injector.Enqueue(job);
                }

                break;
            case WmCancel:
                ResetForNewFocus();
                break;
        }
    }

    private void ApplyConfig(EngineConfig config)
    {
        _config = config;
        _picker.TriggerChar = config.TriggerChar;
        _picker.MinChars = config.MinChars;
        if (!config.PickerEnabled && _picker.IsActive)
        {
            _picker.Reset();
            EndSession();
        }

        if (!config.AutoReplaceActive)
            _autoReplace.Reset();
        _hook.SetKeyboardHook(config.NeedsHook);
    }

    private void ApplyPendingData()
    {
        if (_popupVisible || Interlocked.Exchange(ref _pendingData, null) is not { } data)
            return;
        Volatile.Write(ref _data, data);
        _autoReplace.SetRules(data.Rules);
    }

    // ------------------------------------------------------------------ watchdog

    /// <summary>
    /// Windows silently removes a low-level hook whose callback once took too long. On wake-ups that matter
    /// (focus changes, resume, unlock; at most once a minute) we inject a probe key that a live hook swallows;
    /// if it isn't seen, the hook is re-installed. No timers run while idle.
    /// </summary>
    public void KickWatchdog(bool force = false)
    {
        var now = Environment.TickCount64;
        if (!_hook.KeyboardHookInstalled || (!force && now - Interlocked.Read(ref _lastWatchdogKick) < 60_000))
            return;
        Interlocked.Exchange(ref _lastWatchdogKick, now);
        ThreadPool.UnsafeQueueUserWorkItem(static engine =>
        {
            var sentAt = Environment.TickCount64;
            engine._injector.SendProbe();
            var deadline = sentAt + 500;
            while (Environment.TickCount64 < deadline)
            {
                if (engine._hook.LastProbeTicks >= sentAt)
                    return;
                Thread.Sleep(20); // bounded wait on a rare background check, not on any input path
            }

            Log.Warn("keyboard hook did not see the watchdog probe; re-installing");
            engine._hook.Reinstall();
        }, this, preferLocal: false);
    }

    public void Dispose()
    {
        _hook.Dispose();
        _injector.Dispose();
        _worker.Dispose();
    }
}
