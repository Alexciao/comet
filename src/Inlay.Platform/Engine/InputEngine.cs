using System.Collections.Concurrent;
using System.Diagnostics;
using Inlay.Core.Shorthands;
using Inlay.Core.Input;
using Inlay.Core.Search;
using Inlay.Core.Settings;
using Inlay.Platform.Context;
using Inlay.Platform.Hook;
using Inlay.Platform.Input;
using Inlay.Platform.Native;

namespace Inlay.Platform.Engine;

/// <summary>
/// The brain, running on the hook thread: translates keys, drives the picker session and shorthands,
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
    private const uint WmShortcut = HookThread.WmClientBase + 6;
    private const uint WmInsertTemplate = HookThread.WmClientBase + 7;

    /// <summary>
    /// Give up holding keys if an insertion hasn't completed by then (they are then replayed, in order). Longer than
    /// the injector's own worst case (the clipboard path waits up to ~1.5 s for a slow app), so held keys can't
    /// overtake an insertion that is still running.
    /// </summary>
    private const long MaxInjectionMs = 4000;

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
    private readonly QuickInsertSession _picker = new();
    private readonly ShorthandEngine _shorthands = new();
    private readonly PopupFrame _frame = new();
    private readonly SearchResult[] _results = new SearchResult[PopupFrame.MaxResults];
    private readonly bool[] _swallowedDown = new bool[256];
    private readonly List<(KBDLLHOOKSTRUCT Key, bool Up)> _held = new(16);
    private readonly ConcurrentQueue<ContextResult> _contextResults = new();
    private readonly ConcurrentQueue<InsertionJob> _completedJobs = new();
    private readonly Queue<InsertionJob> _queuedJobs = new(); // hook thread only
    private readonly ConcurrentQueue<string> _templatesToInsert = new();
    private readonly List<RecentItem> _recents = new(32); // the data's recents plus this session's picks
    private InsertionJob? _currentJob;

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
    private bool _hasCreateRow;
    private bool _showingRecents;
    private bool _anchorIsWindow;
    private nuint _recentsTimer;
    private int _selected;
    private int _pick; // "Let me pick": the chosen image of the selected row
    private bool _popupVisible;
    private bool _swallowNextMouseUp;
    private long _injectingSince;
    private long _keyTimestamp;
    private bool _mouseHookWanted;
    private long _lastWatchdogKick;

    public InputEngine(IPopupSink popup, bool acceptForeignInjectedInput = false)
    {
        _popup = popup;
        _picker.ExactMatcher = this;
        _hook = new HookThread(this) { AcceptForeignInjectedInput = acceptForeignInjectedInput };
        _hook.CallbackWasSlow += () =>
        {
            Log.Warn("a keyboard callback was slow; checking that Windows kept the hook");
            KickWatchdog(force: true);
        };
        _worker = new ContextWorker(r =>
        {
            _contextResults.Enqueue(r);
            _hook.Post(WmContextResult);
        });
        _injector = new TextInjector((job, accepted) =>
        {
            _completedJobs.Enqueue(job);
            _hook.Post(WmInjected, accepted);
        });
    }

    /// <summary>Raised on the hook thread when the user picks something (frecency key, e.g. the emoji or "snippet:id").</summary>
    public event Action<string>? Picked;

    /// <summary>
    /// Raised on the hook thread when the user chose "Create snippet :name" (the typed token is already being
    /// removed): open the New snippet dialog, then give the window focus back.
    /// </summary>
    public event Action<string, nint>? CreateSnippetRequested;

    /// <summary>
    /// Raised on the hook thread when an app didn't read a pasted image: (process, method to use next time). The app
    /// stores it in <c>ImagePasteMethods</c>.
    /// </summary>
    public event Action<string, string>? ImagePasteMethodLearned;

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
        _recents.AddRange(data.Recents);
        ApplyConfig(config);
        _shorthands.SetRules(data.Rules);
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

    /// <summary>The keyboard shortcut was pressed (any thread): open Quick Insert at the caret without typing anything.</summary>
    public void OpenFromShortcut() => _hook.Post(WmShortcut);

    /// <summary>Inserts a snippet's text at the caret of the focused window (any thread), e.g. one just created.</summary>
    public void InsertSnippet(string template)
    {
        _templatesToInsert.Enqueue(template);
        _hook.Post(WmInsertTemplate);
    }

    /// <summary>The data currently in use (for tests).</summary>
    internal EngineData CurrentData => Volatile.Read(ref _data);

    // ------------------------------------------------------------------ keyboard

    public bool OnKey(in KBDLLHOOKSTRUCT k, bool isKeyDown, bool isReplay)
    {
        var vk = k.vkCode & 0xFF;
        if (!isKeyDown)
        {
            _translator.KeyUp(vk);
            if (_swallowedDown[vk])
            {
                _swallowedDown[vk] = false;
                return true;
            }

            if (MustHold(isReplay))
            {
                _held.Add((k, true));
                return true;
            }

            return false;
        }

        if (MustHold(isReplay))
        {
            // Hold keys typed during an insertion so they land after it, in order.
            _held.Add((k, false));
            return true;
        }

        // A new press of a key whose earlier press was swallowed: that press's key-up was lost (or the key is
        // auto-repeating through), so this press's key-up must reach the app, or the key stays down there.
        _swallowedDown[vk] = false;

        var start = Stopwatch.GetTimestamp();
        _keyTimestamp = start;
        var wasActive = _picker.IsActive;
        DisarmRecents(); // the pause after the trigger is over

        Span<KeyInput> inputs = stackalloc KeyInput[2];
        // Focus-change events arrive asynchronously, after hook callbacks for keys already typed in the new window:
        // check the foreground window per key (a cheap call) so no key is judged against the previous window.
        if (User32.GetForegroundWindow() != _context.Foreground)
            ResetForNewFocus();

        var layout = User32.GetKeyboardLayout(_context.ThreadId);
        // The key that completes a dead key must reach the app, or the accent it holds lands on whatever comes next.
        var deadKeyPending = _translator.DeadKeyPending;
        var n = _translator.Translate(vk, k.scanCode, ModifierState.Read(k.flags), layout, inputs);
        var swallow = false;
        if (deadKeyPending && _picker.IsActive)
        {
            _picker.Reset();
            EndSession();
        }

        for (var i = 0; i < n; i++)
            swallow |= Process(inputs[i]);
        if (deadKeyPending)
            swallow = false;

        if (swallow)
            _swallowedDown[vk] = true;

        UpdateMouseHook();
        var micros = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        (wasActive || _picker.IsActive ? ActiveKeyLatency : IdleKeyLatency).Add(micros);
        return swallow;
    }

    /// <summary>
    /// Keys typed while an insertion is in flight are held and replayed after it, so they land in order. Keys
    /// being replayed pass straight through, unless one of them started a newer insertion: the replayed keys
    /// after it must then wait for that one too.
    /// </summary>
    private bool MustHold(bool isReplay)
    {
        if (_currentJob is not { } job || (isReplay && job.Replay is not null))
            return false;
        if (Environment.TickCount64 - _injectingSince <= MaxInjectionMs)
            return true;

        // Stuck: replay what was held. If there was anything, this key waits behind that replay, so the order holds.
        Log.Warn("insertion did not complete in time; releasing held keys");
        FinishInjection();
        return _currentJob is not null;
    }

    private bool Process(KeyInput key)
    {
        var config = _config;

        // ---- picker
        if (_picker.IsActive && _popupVisible && key.Kind is (KeyKind.Left or KeyKind.Right) && SelectedIsPick)
        {
            MovePick(key.Kind == KeyKind.Right ? +1 : -1);
            return true; // ←/→ choose in the strip; the caret stays where it is
        }

        if (config.QuickInsertEnabled && (_picker.FromShortcut || QuickInsertAllowedHere(config))
            && !(key.Kind == KeyKind.Char && key.Char == config.TriggerChar && !_picker.IsActive && ImeOpen()))
        {
            var d = _picker.OnKey(key, _popupVisible);
            switch (d.Action)
            {
                case QuickInsertAction.QueryChanged:
                    OnQueryChanged();
                    break;
                case QuickInsertAction.Cancel:
                    EndSession();
                    break;
                case QuickInsertAction.SelectNext:
                    MoveSelection(+1);
                    break;
                case QuickInsertAction.SelectPrevious:
                    MoveSelection(-1);
                    break;
                case QuickInsertAction.CommitSelected:
                    if (_popupVisible && _selected < RowCount)
                        Choose(_selected, d.DeleteCount);
                    else
                        EndSession();
                    break;
                case QuickInsertAction.CommitExact:
                case QuickInsertAction.CommitExactThenSpace:
                    if (FindExactHere(_picker.CommittedToken) is { } exact)
                        Insert(exact, d.DeleteCount, d.Action == QuickInsertAction.CommitExactThenSpace ? new Boundary(BoundaryKind.Char, ' ') : null);
                    else
                        EndSession();
                    break;
            }

            if (d.Action is QuickInsertAction.CommitSelected or QuickInsertAction.CommitExact or QuickInsertAction.CommitExactThenSpace)
            {
                _shorthands.NotifyInserted();
                return d.Swallow;
            }

            if (d.Swallow)
                return true; // navigation inside the popup: the caret didn't move, shorthands keeps its word
        }
        else if (_picker.IsActive)
        {
            _picker.Reset();
            EndSession();
        }

        // ---- shorthands
        if (!config.ShorthandsActive)
            return false;

        var allowed = _context.IsSafe && !config.ShorthandsExcluded.Contains(_context.ProcessName);
        // A boundary typed into an IME composition (space converts, Enter commits) is the IME's, not a word end.
        if (allowed && _shorthands.IsCandidate && EndsWord(key) && ImeOpen())
            allowed = false;
        var a = _shorthands.OnKey(key, allowed);
        switch (a.Action)
        {
            case ShorthandAction.Expand:
            case ShorthandAction.Undo:
                StartInjection(new InsertionJob(a.DeleteCount, a.Text, null, a.Boundary, MethodFor(_context.ProcessName), _keyTimestamp, _context.PacedEditor) { DeleteUnits = a.Deleted?.Length });
                return true;
        }

        if (_shorthands.IsCandidate)
            EnsurePasswordKnown();
        return false;
    }

    /// <summary>
    /// True while the focused window's IME is on (Japanese, Chinese, Korean input): the low-level hook sees raw
    /// keys before the IME composes them, so Inlay must stay out. Asked only when a trigger is typed or a word
    /// could expand, never per key; one bounded cross-process message.
    /// </summary>
    private bool ImeOpen()
    {
        var window = _context.Focus != 0 ? _context.Focus : _context.Foreground;
        var ime = window == 0 ? 0 : Imm32.ImmGetDefaultIMEWnd(window);
        if (ime == 0)
            return false;
        return User32.SendMessageTimeoutW(ime, WmImeControl, ImcGetOpenStatus, 0, User32.SMTO_ABORTIFHUNG, 50, out var open) != 0 && open != 0;
    }

    private static bool EndsWord(KeyInput key) => key.Kind is KeyKind.Enter or KeyKind.Tab
        || (key.Kind == KeyKind.Char && (TextClass.IsWhitespace(key.Char) || TextClass.IsClosingPunctuation(key.Char)));

    private const uint WmImeControl = 0x0283;
    private const nuint ImcGetOpenStatus = 0x0005;

    private bool QuickInsertAllowedHere(EngineConfig config) =>
        !_context.Elevated && !_context.IsSelf && config.ModeFor(_context.ProcessName) != QuickInsertMode.Off;

    /// <summary>
    /// "Hide emoji" in this app: only the user's own snippets and images are offered. Not when the user asked for
    /// Quick Insert with the shortcut: then everything is.
    /// </summary>
    private bool HideEmojiHere => !_picker.FromShortcut && _config.ModeFor(_context.ProcessName) == QuickInsertMode.HideEmoji;

    /// <summary>Drops emoji results in "Hide emoji" apps; Quick Insert then appears only when something of yours matches.</summary>
    private int FilterForApp(int count)
    {
        if (!HideEmojiHere)
            return count;
        var kept = 0;
        for (var i = 0; i < count; i++)
        {
            if (_results[i].Kind != ResultKind.Emoji)
                _results[kept++] = _results[i];
        }

        return kept;
    }

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

        if (_picker.AwaitingQuery)
            ArmRecents();

        if (!_picker.HasQuery)
        {
            // From the shortcut, an empty query shows recents (or just the search line).
            if (_picker.FromShortcut)
            {
                ShowRecents();
                return;
            }

            _resultCount = 0;
            _hasCreateRow = false;
            _showingRecents = false;
            Publish();
            return;
        }

        _showingRecents = false;
        _resultCount = FilterForApp(_data.Search.Search(_picker.Token, _results));
        _hasCreateRow = OffersCreate();
        _selected = 0;
        _pick = 0;
        Publish();
    }

    /// <summary>Rows on screen: the results, plus "Create snippet" when it is offered.</summary>
    private int RowCount => _resultCount + (_hasCreateRow ? 1 : 0);

    /// <summary>
    /// "Create snippet :name" when Snippets is on, the query is a valid name of 2+ characters, and none of the user's
    /// snippets or images has exactly that name.
    /// </summary>
    private bool OffersCreate()
    {
        var config = _config;
        var token = _picker.Token;
        return config.SnippetsEnabled && config.OfferCreateSnippet && token.Length >= 2
            && QuickInsertSession.IsValidName(token)
            && _data.Search.FindExact(token) is not { Kind: ResultKind.Snippet };
    }

    // ------------------------------------------------------------------ recents

    /// <summary>One-shot timer, armed only right after a trigger: a pause opens recents. Nothing runs otherwise.</summary>
    private void ArmRecents()
    {
        if (!_config.ShowRecents || _recentsTimer != 0 || _recents.Count == 0)
            return;
        _recentsTimer = User32.SetTimer(0, 0, (uint)_config.RecentsDelayMs, 0);
    }

    private void DisarmRecents()
    {
        if (_recentsTimer == 0)
            return;
        User32.KillTimer(0, _recentsTimer);
        _recentsTimer = 0;
    }

    private void OnRecentsTimer()
    {
        DisarmRecents();
        if (!_picker.AwaitingQuery)
            return;
        _picker.OpenedEarly();
        ShowRecents();
    }

    /// <summary>The most used items across every enabled kind, most used first.</summary>
    private void ShowRecents()
    {
        var hideEmoji = HideEmojiHere;
        var count = 0;
        foreach (var r in _recents)
        {
            if (count == RecentsShown)
                break;
            if (hideEmoji && r.Result.Kind == ResultKind.Emoji)
                continue;
            _results[count++] = r.Result;
        }

        _resultCount = count;
        _hasCreateRow = false;
        _showingRecents = true;
        _selected = 0;
        _pick = 0;
        Publish();
    }

    private const int RecentsShown = 8;

    /// <summary>A pick counts right away: it moves up in recents for as long as this data is in use.</summary>
    private void BumpRecent(SearchResult picked)
    {
        var i = _recents.FindIndex(r => r.Result.Kind == picked.Kind && r.Result.Index == picked.Index);
        var item = i >= 0 ? _recents[i] with { Score = _recents[i].Score + 1 } : new RecentItem(picked with { Tier = MatchTier.Exact, Score = 0 }, 1);
        if (i >= 0)
            _recents.RemoveAt(i);
        var at = 0;
        while (at < _recents.Count && _recents[at].Score >= item.Score)
            at++;
        _recents.Insert(at, item);
    }

    private void MoveSelection(int delta)
    {
        var rows = RowCount;
        if (rows == 0)
            return;
        _selected = (_selected + delta + rows) % rows;
        _pick = 0;
        Publish();
    }

    /// <summary>The selected row is a "Let me pick" folder.</summary>
    private bool SelectedIsPick =>
        _selected < _resultCount && _results[_selected] is { Kind: ResultKind.Snippet } r && r.Index < _data.UserItems.Length && _data.UserItems[r.Index].IsPick;

    private void MovePick(int delta)
    {
        var item = _data.UserItems[_results[_selected].Index].Image!;
        var count = _data.Images?.PickCandidates(item).Count ?? 0;
        if (count == 0)
            return;
        _pick = Math.Clamp(_pick + delta, 0, count - 1);
        Publish();
    }

    /// <summary>Shows, updates or hides the popup to match the current state.</summary>
    private void Publish()
    {
        var shown = _picker.HasQuery || _showingRecents || _picker.FromShortcut;
        var visible = shown && (RowCount > 0 || _picker.FromShortcut) && _anchorKnown && _context.IsSafe;
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
            _frame.Count = RowCount;
            _frame.CreateRow = _hasCreateRow;
            _frame.Recents = _showingRecents;
            _frame.FromShortcut = _picker.FromShortcut;
            _frame.AnchorIsWindow = _anchorIsWindow;
            Array.Copy(_results, _frame.Results, _resultCount);
            _frame.Selected = _selected;
            _frame.Pick = _pick;
            var token = _picker.Token;
            token.CopyTo(_frame.Query);
            _frame.QueryLength = token.Length;
            _frame.Anchor = _anchor;
            _frame.SkinTone = _config.SkinTone;
            _frame.Trigger = _config.TriggerString;
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
        _hasCreateRow = false;
        _showingRecents = false;
        DisarmRecents();
        _caretRequested = false;
        _anchorKnown = false;
        _anchorIsWindow = false;
        lock (_frame.Gate)
            _frame.Data = EngineData.Empty; // a closed Quick Insert keeps no dataset alive (Emoji may have been turned off)
        ApplyPendingData();
    }

    /// <summary>Acts on row <paramref name="row"/>: a result, or "Create snippet".</summary>
    private void Choose(int row, int deleteCount)
    {
        if (row < _resultCount)
            Insert(_results[row], deleteCount, after: null, pick: row == _selected ? _pick : 0);
        else if (_hasCreateRow && row == _resultCount)
            CreateSnippet(deleteCount);
        else
            EndSession();
    }

    /// <summary>Removes the typed token and hands the name to the app, which opens the New snippet dialog.</summary>
    private void CreateSnippet(int deleteCount)
    {
        var name = _picker.CommittedToken.Length > 0 ? _picker.CommittedToken.ToString() : _picker.Token.ToString();
        var window = _context.Foreground;
        EndSession();
        _picker.NotifyInserted();
        if (deleteCount > 0)
            StartInjection(new InsertionJob(deleteCount, "", null, null, MethodFor(_context.ProcessName), _keyTimestamp, _context.PacedEditor));
        CreateSnippetRequested?.Invoke(name, window);
    }

    private void Insert(SearchResult result, int deleteCount, Boundary? after, int pick = 0)
    {
        var data = _data;
        string? text = null, template = null, key;
        ImageInsertion? image = null;
        if (result.Kind == ResultKind.Emoji && data.Emoji is { } emoji)
        {
            text = emoji.Emoji(result.Index, _config.SkinTone).ToString();
            key = emoji.IsSymbol(result.Index) ? Frecency.SymbolKey(emoji.Emoji(result.Index)) : Frecency.EmojiKey(emoji.Emoji(result.Index));
        }
        else if (result.Kind == ResultKind.Snippet && result.Index < data.UserItems.Length && data.UserItems[result.Index] is { Snippet: { } snippet } item)
        {
            template = snippet.Text;
            key = item.FrecencyKey;
        }
        else if (result.Kind == ResultKind.Snippet && result.Index < data.UserItems.Length && data.UserItems[result.Index] is { Image: { } imageItem } entry
            && data.Images?.ToInsertion(imageItem, _context.ProcessName, _config.ImagePasteMethods, entry.IsPick ? pick : -1) is { } resolved)
        {
            image = resolved; // resolved now from cached lists: a folder entry gives its newest, a random or the picked file
            key = entry.FrecencyKey;
        }
        else
        {
            EndSession();
            return;
        }

        // Hide first, with no animation: the text lands in the same frame the popup disappears.
        EndSession();
        _picker.NotifyInserted();
        StartInjection(new InsertionJob(deleteCount, text, template, after, MethodFor(_context.ProcessName), _keyTimestamp, _context.PacedEditor) { Image = image });
        data.Search.BumpFrecency(result.Kind, result.Index);
        BumpRecent(result);
        Picked?.Invoke(key);
    }

    private InsertionMethod MethodFor(string process) =>
        s_clipboardApps.Contains(process) || (_config.InsertionMethods.TryGetValue(process, out var m) && m == "clipboard")
            ? InsertionMethod.Clipboard
            : InsertionMethod.Unicode;

    private void StartInjection(InsertionJob job)
    {
        Log.Debug($"inject: delete {job.DeleteCount}, text {job.Text?.Length ?? -1}, snippet {job.SnippetTemplate is not null}, replay {job.Replay?.Length ?? 0}, method {job.Method}");
        _currentJob = job;
        _injectingSince = Environment.TickCount64;
        // Hand the job over only after the current hook callback has returned, so SendInput never runs while
        // the system is still waiting on our callback for the key that triggered it.
        _queuedJobs.Enqueue(job);
        _hook.Post(WmDispatchInjection);
    }

    private void FinishInjection()
    {
        Log.Debug($"inject done after {Environment.TickCount64 - _injectingSince} ms, {_held.Count} keys held");
        _currentJob = null;
        if (_held.Count == 0)
            return;
        var keys = new KBDLLHOOKSTRUCT[_held.Count];
        var ups = new bool[_held.Count];
        for (var i = 0; i < _held.Count; i++)
            (keys[i], ups[i]) = _held[i];
        _held.Clear();
        // Replayed keys come back through the hook tagged as real typing, in their original order.
        StartInjection(new InsertionJob(0, null, null, null, InsertionMethod.Unicode, 0, _context.PacedEditor) { Replay = keys, ReplayKeyUp = ups });
    }

    // ------------------------------------------------------------------ IExactMatcher

    bool IExactMatcher.HasExact(ReadOnlySpan<char> token) =>
        _context.IsSafe && FindExactHere(token) is not null;

    private SearchResult? FindExactHere(ReadOnlySpan<char> token) =>
        _data.Search.FindExact(token) is { } exact && !(exact.Kind == ResultKind.Emoji && HideEmojiHere) ? exact : null;

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
                // Handle the click here and never let it reach the popup, so the popup can't take focus. A click on
                // a list the popup hasn't drawn yet (a key typed in the same frame) picks nothing: the user saw a
                // different row there.
                var row = g.RowAt(point.Y - g.Window.Top - g.ListTop);
                if (g.Version == _frame.Version && row >= 0 && row < RowCount)
                {
                    var delete = _picker.FromShortcut ? 0 : 1 + _picker.Token.Length;
                    _shorthands.NotifyInserted();
                    Choose(row, delete);
                    _picker.Reset();
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

        _shorthands.Reset();
        UpdateMouseHook();
        return false;
    }

    private static bool Contains(RECT r, POINT p) => p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;

    /// <summary>
    /// The mouse hook costs a context switch per mouse event, so it is installed only while a click must reset
    /// state: a picker session, or shorthands knowing the text before the caret (a click moves the caret away
    /// from it, and a Backspace after an expansion would otherwise undo it wherever the caret now is).
    /// </summary>
    private void UpdateMouseHook()
    {
        var want = _picker.IsActive || _shorthands.HasState;
        if (want != _mouseHookWanted)
        {
            _mouseHookWanted = want;
            _hook.Post(HookThread.WmSetMouseHook, want ? 1u : 0u);
        }
    }

    // ------------------------------------------------------------------ focus

    public void OnWinEvent(uint winEvent, nint hwnd, int idObject)
    {
        // Chromium and Electron raise focus events from accessibility child windows while keyboard focus stays put
        // (e.g. VS Code on every edit): what counts is the window that actually holds keyboard focus.
        if (winEvent == User32.EVENT_SYSTEM_FOREGROUND || (hwnd != _context.Focus && FocusContext.KeyboardFocus() != (_context.Foreground, _context.Focus)))
        {
            ResetForNewFocus();
            KickWatchdog();
            return;
        }

        // Focus moved between elements of the same window (e.g. fields in a web page). Clicks and Tab already
        // reset the session; here we only re-check whether the new element is a password field.
        _generation++;
        _context = _context with { Generation = _generation };
        if (_picker.IsActive || _shorthands.CurrentWord.Length > 0)
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
        _shorthands.Reset();
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
        Log.Debug($"context {r.Kind}: {Environment.TickCount64 - r.Started} ms ({(r.Kind == ContextRequestKind.Caret ? r.Method.ToString() : r.Password.ToString())})");
        if (r.Generation != _context.Generation)
            return; // answer for a focus that no longer exists

        if (r.Kind == ContextRequestKind.Password)
        {
            _context = _context with { Password = r.Password };
            if (r.Password == Tristate.Yes)
            {
                _picker.Reset();
                _shorthands.Reset();
                EndSession();
                return;
            }
        }
        else if (_picker.IsActive)
        {
            _anchor = r.Caret;
            _anchorKnown = r.Method != CaretMethod.None;
            _anchorIsWindow = false;
            // From the shortcut without a caret (no text field focused): the bottom center of the window.
            if (!_anchorKnown && _picker.FromShortcut && User32.GetWindowRect(_context.Foreground, out var window))
            {
                _anchor = window;
                _anchorKnown = _anchorIsWindow = true;
            }
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
                // Only the completion of the job we are waiting for releases held keys (an earlier replay job
                // may finish after a key it replayed started a newer insertion).
                while (_completedJobs.TryDequeue(out var done))
                {
                    if (done.Image is { InsertAs: ImageInsertAs.Image, Result.Read: false } unread)
                        ImagePasteMethodLearned?.Invoke(unread.Process, ImagePasteMethod.AfterUnread(unread.Method));
                    if (ReferenceEquals(done, _currentJob))
                        FinishInjection();
                }

                break;
            case WmDispatchInjection:
                while (_queuedJobs.TryDequeue(out var job))
                    _injector.Enqueue(job);
                break;
            case WmCancel:
                ResetForNewFocus();
                break;
            case WmShortcut:
                OpenFromShortcutNow();
                break;
            case WmInsertTemplate:
                while (_templatesToInsert.TryDequeue(out var template))
                    InsertTemplateNow(template);
                break;
            case User32.WM_TIMER when wParam == _recentsTimer && _recentsTimer != 0:
                OnRecentsTimer();
                break;
        }
    }

    private void OpenFromShortcutNow()
    {
        if (!_config.QuickInsertEnabled || !_hook.KeyboardHookInstalled)
            return;
        if (User32.GetForegroundWindow() != _context.Foreground)
            ResetForNewFocus();
        if (_context.Elevated || _context.IsSelf || _context.Password == Tristate.Yes)
            return;
        if (_picker.IsActive)
            EndSession();
        _picker.Reset();
        _picker.StartFromShortcut();
        OnQueryChanged();
        UpdateMouseHook();
    }

    /// <summary>Inserts a template at the caret with nothing to erase (a snippet created from Quick Insert).</summary>
    private void InsertTemplateNow(string template)
    {
        if (User32.GetForegroundWindow() != _context.Foreground)
            ResetForNewFocus();
        if (!_context.IsSafe || _context.Elevated)
            return;
        _picker.NotifyInserted();
        _shorthands.NotifyInserted();
        StartInjection(new InsertionJob(0, null, template, null, MethodFor(_context.ProcessName), Stopwatch.GetTimestamp(), _context.PacedEditor));
    }

    private void ApplyConfig(EngineConfig config)
    {
        _config = config;
        _picker.TriggerChar = config.TriggerChar;
        _picker.MinChars = config.MinChars;
        if (!config.QuickInsertEnabled && _picker.IsActive)
        {
            _picker.Reset();
            EndSession();
        }

        if (!config.ShorthandsActive)
            _shorthands.Reset();
        _hook.SetKeyboardHook(config.NeedsHook);
    }

    private void ApplyPendingData()
    {
        if (_popupVisible || Interlocked.Exchange(ref _pendingData, null) is not { } data)
            return;
        Volatile.Write(ref _data, data);
        _shorthands.SetRules(data.Rules);
        _recents.Clear();
        _recents.AddRange(data.Recents);
        Log.Info($"search data updated: emoji {(data.Emoji is null ? "off" : "on")}, {data.UserItems.Length} snippets and images, {data.Rules.Length} replacements");
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
