using System.Diagnostics;
using System.Runtime.InteropServices;
using Inlay.Platform.Native;

namespace Inlay.Platform.Hook;

/// <summary>Tags on <c>dwExtraInfo</c> that let the hook recognise Inlay's own synthetic input.</summary>
internal static class InputSignature
{
    /// <summary>Text Inlay inserts: always ignored by the hook.</summary>
    public const nuint Inlay = 0x434F4D45;

    /// <summary>User keystrokes held during an insertion and replayed afterwards: treated as real typing.</summary>
    public const nuint Replay = 0x434F4D52;

    /// <summary>Watchdog probe: swallowed by a live hook, proving it is still installed.</summary>
    public const nuint Probe = 0x434F4D50;
}

/// <summary>Receives hook callbacks on the hook thread. Implementations must return quickly and never throw.</summary>
internal interface IHookClient
{
    /// <returns>True to swallow the key.</returns>
    /// <param name="isReplay">A key the user typed during an insertion, now re-sent by Inlay in order.</param>
    bool OnKey(in KBDLLHOOKSTRUCT key, bool isKeyDown, bool isReplay);

    /// <returns>True to swallow the click.</returns>
    bool OnMouseButton(uint message, POINT point);

    void OnWinEvent(uint winEvent, nint hwnd, int idObject);

    void OnThreadMessage(uint message, nuint wParam, nint lParam);
}

/// <summary>
/// Owns the <c>WH_KEYBOARD_LL</c> hook on a dedicated thread with its own message loop. LL hook callbacks
/// run on the installing thread and Windows silently removes a hook whose callback is slow, so this thread
/// does nothing else: callbacks decide synchronously (swallow or not) and push any slow work elsewhere.
/// </summary>
internal sealed unsafe class HookThread : IDisposable
{
    public const uint WmReinstall = User32.WM_APP + 1;
    public const uint WmSetKeyboardHook = User32.WM_APP + 2;
    public const uint WmSetMouseHook = User32.WM_APP + 3;

    /// <summary>First message number available to <see cref="IHookClient"/>.</summary>
    public const uint WmClientBase = User32.WM_APP + 16;

    private static HookThread? s_instance;

    private readonly IHookClient _client;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new();
    private uint _threadId;
    private nint _keyboardHook;
    private nint _mouseHook;
    private nint _foregroundEvents;
    private nint _focusEvents;
    private long _lastProbeTicks;
    private bool _mouseWanted;
    private nuint _retryTimer;

    /// <summary>A keyboard callback took long enough that Windows may drop the hook: check it now (raised on the thread pool).</summary>
    public event Action? CallbackWasSlow;

    /// <summary>Windows removes a hook whose callback exceeds LowLevelHooksTimeout (a few hundred ms by default).</summary>
    private static readonly long s_slowCallback = Stopwatch.Frequency / 5; // 200 ms

    private const uint RetryIntervalMs = 5000;

    public HookThread(IHookClient client)
    {
        if (Interlocked.CompareExchange(ref s_instance, this, null) is not null)
            throw new InvalidOperationException("Only one hook thread may exist.");
        _client = client;
        _thread = new Thread(Run) { IsBackground = true, Name = "Inlay hook", Priority = ThreadPriority.Highest };
    }

    /// <summary>Process injected input that isn't Inlay's own (end-to-end tests drive the app with SendInput).</summary>
    public bool AcceptForeignInjectedInput { get; init; }

    public bool KeyboardHookInstalled => Volatile.Read(ref _keyboardHook) != 0;

    public bool MouseHookInstalled => _mouseHook != 0;

    public uint ThreadId => _threadId;

    /// <summary>Tick count of the last watchdog probe the hook saw.</summary>
    public long LastProbeTicks => Interlocked.Read(ref _lastProbeTicks);

    public void Start(bool installKeyboardHook)
    {
        _wantKeyboard = installKeyboardHook;
        _thread.Start();
        _started.Wait();
    }

    private bool _wantKeyboard;

    public bool Post(uint message, nuint wParam = 0, nint lParam = 0) => User32.PostThreadMessageW(_threadId, message, wParam, lParam);

    /// <summary>Installs or removes the keyboard hook (and focus events). Asynchronous; safe from any thread.</summary>
    public void SetKeyboardHook(bool installed) => Post(WmSetKeyboardHook, installed ? 1u : 0u);

    /// <summary>Re-installs the keyboard hook after Windows dropped it.</summary>
    public void Reinstall() => Post(WmReinstall);

    private void Run()
    {
        _threadId = Kernel32.GetCurrentThreadId();
        // Make sure the thread has a message queue before anyone posts to it.
        User32.PostThreadMessageW(_threadId, User32.WM_NULL, 0, 0);
        if (_wantKeyboard)
            InstallKeyboard();
        _started.Set();

        try
        {
            while (User32.GetMessageW(out var msg, 0, 0, 0) > 0)
            {
                if (msg.hwnd != 0)
                {
                    User32.TranslateMessage(msg);
                    User32.DispatchMessageW(msg);
                    continue;
                }

                switch (msg.message)
                {
                    case WmReinstall:
                        if (_wantKeyboard)
                        {
                            RemoveKeyboard();
                            InstallKeyboard();
                        }

                        break;
                    case WmSetKeyboardHook:
                        _wantKeyboard = msg.wParam != 0;
                        if (_wantKeyboard && _keyboardHook == 0)
                            InstallKeyboard();
                        else if (!_wantKeyboard && _keyboardHook != 0)
                            RemoveKeyboard();
                        break;
                    case WmSetMouseHook:
                        _mouseWanted = msg.wParam != 0;
                        SetMouse(_mouseWanted);
                        break;
                    case User32.WM_TIMER when msg.wParam == _retryTimer:
                        // An earlier SetWindowsHookEx failed: try again while the hook is still wanted.
                        if (!_wantKeyboard || _keyboardHook != 0)
                            StopRetrying();
                        else
                            InstallKeyboard();
                        break;
                    case User32.WM_NULL:
                        break;
                    default:
                        _client.OnThreadMessage(msg.message, msg.wParam, msg.lParam);
                        break;
                }
            }
        }
        finally
        {
            SetMouse(false);
            RemoveKeyboard();
        }
    }

    private void InstallKeyboard()
    {
        var module = Kernel32.GetModuleHandleW(null);
        Volatile.Write(ref _keyboardHook, User32.SetWindowsHookExW(User32.WH_KEYBOARD_LL, &KeyboardProc, module, 0));
        if (_keyboardHook == 0)
        {
            Log.Warn($"SetWindowsHookEx(WH_KEYBOARD_LL) failed: {Marshal.GetLastWin32Error()}; retrying every {RetryIntervalMs / 1000} s");
            if (_retryTimer == 0)
                _retryTimer = User32.SetTimer(0, 0, RetryIntervalMs, 0);
            return;
        }

        Log.Info("keyboard hook installed");
        StopRetrying();
        // Focus events come with the keyboard hook; installed once, whatever happened to the hook before.
        var flags = User32.WINEVENT_OUTOFCONTEXT | User32.WINEVENT_SKIPOWNPROCESS;
        if (_foregroundEvents == 0)
            _foregroundEvents = User32.SetWinEventHook(User32.EVENT_SYSTEM_FOREGROUND, User32.EVENT_SYSTEM_FOREGROUND, 0, &WinEventProc, 0, 0, flags);
        if (_focusEvents == 0)
            _focusEvents = User32.SetWinEventHook(User32.EVENT_OBJECT_FOCUS, User32.EVENT_OBJECT_FOCUS, 0, &WinEventProc, 0, 0, flags);
        // A re-install must not lose the mouse hook the engine still wants.
        if (_mouseWanted)
            SetMouse(true);
    }

    private void StopRetrying()
    {
        if (_retryTimer != 0)
            User32.KillTimer(0, _retryTimer);
        _retryTimer = 0;
    }

    private void RemoveKeyboard()
    {
        StopRetrying();
        SetMouse(false);
        if (_keyboardHook != 0)
        {
            User32.UnhookWindowsHookEx(_keyboardHook);
            Log.Info("keyboard hook removed");
        }

        Volatile.Write(ref _keyboardHook, 0);
        if (_foregroundEvents != 0)
            User32.UnhookWinEvent(_foregroundEvents);
        if (_focusEvents != 0)
            User32.UnhookWinEvent(_focusEvents);
        _foregroundEvents = _focusEvents = 0;
    }

    /// <summary>Installs the mouse hook only while there is state a click must reset (no cost otherwise).</summary>
    internal void SetMouse(bool installed)
    {
        if (installed && _mouseHook == 0 && _keyboardHook != 0)
            _mouseHook = User32.SetWindowsHookExW(User32.WH_MOUSE_LL, &MouseProc, Kernel32.GetModuleHandleW(null), 0);
        else if (!installed && _mouseHook != 0)
        {
            User32.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = 0;
        }
    }

    [UnmanagedCallersOnly]
    private static nint KeyboardProc(int nCode, nuint wParam, nint lParam)
    {
        var self = s_instance;
        if (nCode == User32.HC_ACTION && self is not null)
        {
            try
            {
                ref readonly var k = ref *(KBDLLHOOKSTRUCT*)lParam;
                if ((k.flags & User32.LLKHF_INJECTED) != 0)
                {
                    if (k.dwExtraInfo == InputSignature.Probe)
                    {
                        Interlocked.Exchange(ref self._lastProbeTicks, Environment.TickCount64);
                        return 1;
                    }

                    var real = k.dwExtraInfo == InputSignature.Replay || (self.AcceptForeignInjectedInput && k.dwExtraInfo != InputSignature.Inlay);
                    if (!real)
                        return User32.CallNextHookEx(0, nCode, wParam, lParam);
                }

                var down = wParam is User32.WM_KEYDOWN or User32.WM_SYSKEYDOWN;
                var replay = (k.flags & User32.LLKHF_INJECTED) != 0 && k.dwExtraInfo == InputSignature.Replay;
                var started = Stopwatch.GetTimestamp();
                var swallow = self._client.OnKey(k, down, replay);
                if (Stopwatch.GetTimestamp() - started > s_slowCallback && self.CallbackWasSlow is { } slow)
                    ThreadPool.UnsafeQueueUserWorkItem(static s => s(), slow, preferLocal: false);
                if (swallow)
                    return 1;
            }
            catch (Exception ex)
            {
                // Never let an exception cross into user32, and never swallow a key because of a bug.
                Log.Error("keyboard hook", ex);
            }
        }

        return User32.CallNextHookEx(0, nCode, wParam, lParam);
    }

    [UnmanagedCallersOnly]
    private static nint MouseProc(int nCode, nuint wParam, nint lParam)
    {
        var self = s_instance;
        if (nCode == User32.HC_ACTION && self is not null)
        {
            var msg = (uint)wParam;
            if (msg is User32.WM_LBUTTONDOWN or User32.WM_LBUTTONUP or User32.WM_RBUTTONDOWN or User32.WM_MBUTTONDOWN or User32.WM_XBUTTONDOWN)
            {
                try
                {
                    ref readonly var m = ref *(MSLLHOOKSTRUCT*)lParam;
                    if ((m.flags & User32.LLMHF_INJECTED) == 0 || self.AcceptForeignInjectedInput)
                    {
                        if (self._client.OnMouseButton(msg, m.pt))
                            return 1;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("mouse hook", ex);
                }
            }
        }

        return User32.CallNextHookEx(0, nCode, wParam, lParam);
    }

    [UnmanagedCallersOnly]
    private static void WinEventProc(nint hook, uint winEvent, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            s_instance?._client.OnWinEvent(winEvent, hwnd, idObject);
        }
        catch (Exception ex)
        {
            Log.Error("win event", ex);
        }
    }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            User32.PostThreadMessageW(_threadId, User32.WM_QUIT, 0, 0);
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        _started.Dispose();
        Interlocked.CompareExchange(ref s_instance, null, this);
    }
}
