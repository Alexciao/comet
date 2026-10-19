using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Comet.Core.AutoReplace;
using Comet.Core.Snippets;
using Comet.Core.Text;
using Comet.Platform.Hook;
using Comet.Platform.Native;

namespace Comet.Platform.Input;

internal enum InsertionMethod : byte
{
    /// <summary>KEYEVENTF_UNICODE key events (default).</summary>
    Unicode,
    /// <summary>Clipboard + Ctrl+V, previous clipboard restored afterwards.</summary>
    Clipboard,
}

/// <summary>One insertion: erase what was typed, insert text (or an expanded snippet), then re-send the boundary key.</summary>
/// <param name="PacedEditor">An editor edited directly instead of with key events (Windows 11 Notepad; see TextInjector.EditDirectly), or 0.</param>
internal sealed record InsertionJob(
    int DeleteCount,
    string? Text,
    string? SnippetTemplate,
    Boundary? After,
    InsertionMethod Method,
    long KeyTimestamp,
    nint PacedEditor = 0)
{
    /// <summary>
    /// UTF-16 units to remove when the editor is edited directly by index (the text being replaced may contain
    /// surrogate pairs, which <see cref="DeleteCount"/> counts as one character each). Defaults to DeleteCount.
    /// </summary>
    public int? DeleteUnits { get; init; }

    /// <summary>Keys the user typed while this job was in flight, replayed after it (set by the engine).</summary>
    public KBDLLHOOKSTRUCT[]? Replay { get; init; }

    public bool[]? ReplayKeyUp { get; init; }
}

/// <summary>
/// Sends insertions from its own thread. Keeping <c>SendInput</c> off the hook thread matters: injected events
/// pass through the hook again, and the hook thread must be free to wave them through.
/// </summary>
internal sealed unsafe class TextInjector : IDisposable, ISnippetEnvironment
{
    private const uint CF_UNICODETEXT = 13;

    private static TextInjector? s_instance;

    private readonly BlockingCollection<InsertionJob> _queue = new(new ConcurrentQueue<InsertionJob>());
    private readonly Thread _thread;
    private readonly Action<InsertionJob, uint> _completed;
    private readonly InputBatch _batch = new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly uint _excludeFromHistory;
    private readonly uint _canIncludeInHistory;
    private nint _window;
    private string? _pendingRender;
    private bool _rendered;

    /// <param name="completed">Called on the injector thread after each job with the number of events Windows accepted.</param>
    public TextInjector(Action<InsertionJob, uint> completed)
    {
        _completed = completed;
        _excludeFromHistory = User32.RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");
        _canIncludeInHistory = User32.RegisterClipboardFormatW("CanIncludeInClipboardHistory");
        s_instance = this;
        _thread = new Thread(Run) { IsBackground = true, Name = "Comet injector", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        _ready.Wait();
    }

    public void Enqueue(InsertionJob job) => _queue.TryAdd(job);

    /// <summary>Sends a watchdog probe key (swallowed by a live hook).</summary>
    public void SendProbe() => new InputBatch().Key(0xFF, InputSignature.Probe).Send();

    // ISnippetEnvironment: snippets expand here, at the moment of insertion.
    DateTime ISnippetEnvironment.Now => DateTime.Now;

    CultureInfo ISnippetEnvironment.Culture => CultureInfo.CurrentCulture;

    /// <summary>
    /// The user's clipboard text. While one of Comet's own pastes is still on the clipboard (it is restored once
    /// insertions pause), that is the user's saved copy, not Comet's previous insertion.
    /// </summary>
    string ISnippetEnvironment.ClipboardText() =>
        _clipboardToRestore is { } saved ? SavedText(saved) : ReadClipboardText();

    private static string SavedText(List<(uint Format, byte[] Data)> saved)
    {
        foreach (var (format, data) in saved)
        {
            if (format != CF_UNICODETEXT)
                continue;
            var text = System.Text.Encoding.Unicode.GetString(data);
            var nul = text.IndexOf('\0');
            return nul < 0 ? text : text[..nul];
        }

        return "";
    }

    private void Run()
    {
        CreateMessageWindow();
        _ready.Set();
        while (!_queue.IsCompleted)
        {
            // Idle: wait forever. With the user's clipboard still to restore: wait only until insertions pause.
            if (!_queue.TryTake(out var job, _clipboardToRestore is null ? Timeout.Infinite : ClipboardRestoreDelayMs))
            {
                RestorePendingClipboard();
                continue;
            }

            uint accepted = 0;
            try
            {
                accepted = Execute(job);
            }
            catch (Exception ex)
            {
                Log.Error("injector", ex);
            }

            try
            {
                _completed(job, accepted);
            }
            catch (Exception ex)
            {
                Log.Error("injector completion", ex);
            }
        }

        RestorePendingClipboard();
        User32.DestroyWindow(_window);
    }

    private uint Execute(InsertionJob job)
    {
        if (job.Text is null && job.SnippetTemplate is null)
            return Replay(job);

        var text = job.Text ?? "";
        var afterCursor = "";
        if (job.SnippetTemplate is not null)
        {
            var expansion = SnippetTemplate.Expand(job.SnippetTemplate, this);
            text = expansion.Text;
            afterCursor = expansion.AfterCursor.ToString();
        }

        uint accepted;
        if (job.Method == InsertionMethod.Clipboard)
            accepted = PasteViaClipboard(job, text, afterCursor);
        else if (job.PacedEditor != 0 && EditDirectly(job, text, afterCursor) is { } direct)
            accepted = direct;
        else
            accepted = TypeBatch(job, text, afterCursor);
        return accepted + Replay(job);
    }

    /// <summary>
    /// How far the caret goes back after the text and its boundary, in characters as the app sees them: the part
    /// after <c>{cursor}</c>, plus a typed boundary character (the commit space goes at the end, not at the caret).
    /// </summary>
    internal static int CaretBack(InsertionJob job, string afterCursor) =>
        afterCursor.Length == 0 ? 0 : SearchText.GraphemeCount(afterCursor) + (job.After is { Kind: BoundaryKind.Char } ? 1 : 0);

    /// <summary>The normal path: one atomic SendInput batch.</summary>
    private uint TypeBatch(InsertionJob job, string text, string afterCursor)
    {
        _batch.Clear().ReleaseModifiers().Backspaces(job.DeleteCount).Text(text);
        if (job.After is { } after)
            _batch.Boundary(after);
        return _batch.Lefts(CaretBack(job, afterCursor)).RestoreModifiers().Send();
    }

    /// <summary>
    /// Inserts by editing the control directly (EM_SETSEL + EM_REPLACESEL), for editors that mishandle synthetic
    /// key events: Windows 11 Notepad falls behind its input queue and then reads the *current* keyboard state
    /// for queued keys, so measured batches lost Ctrl from Ctrl+V and the high surrogate of emoji in about half
    /// the insertions. The editor may still be working through keys the user typed, so first the token's last
    /// character is selected with Shift held until EM_GETSEL reports that selection — proof that everything typed
    /// so far was applied, and an exact index. Returns null (use the key-event path) if the editor doesn't answer.
    /// </summary>
    private uint? EditDirectly(InsertionJob job, string text, string afterCursor)
    {
        var hwnd = job.PacedEditor;
        uint accepted = 0;
        int end;
        if (job.DeleteCount > 0)
        {
            accepted += _batch.Clear().ReleaseModifiers().ShiftLeftHeld().Send();
            var deadline = Environment.TickCount64 + 500;
            (int Start, int End)? selection;
            while ((selection = Selection(hwnd)) is { } sel && sel.End - sel.Start != 1 && Environment.TickCount64 < deadline)
                Thread.Yield();
            accepted += _batch.Clear().ReleaseLeftShift().Send();
            if (selection is not { } found || found.End - found.Start != 1)
            {
                Log.Warn("direct edit: the editor did not report the selection; typing instead");
                accepted += _batch.Clear().RestoreModifiers().Send();
                return null;
            }

            end = found.End;
        }
        else if (Selection(hwnd) is { } caret)
        {
            end = caret.End;
        }
        else
        {
            return null;
        }

        var (start, insert, cursor) = PlanDirectEdit(end, job.DeleteUnits ?? job.DeleteCount, text, afterCursor, job.After);
        Log.Debug($"direct edit: replace {start}-{end} with {insert.Length} units");
        User32.SendMessageTimeoutW(hwnd, User32.EM_SETSEL, (nuint)start, end, User32.SMTO_ABORTIFHUNG, 200, out _);
        ReplaceSelection(hwnd, insert);
        if (cursor is { } at)
            User32.SendMessageTimeoutW(hwnd, User32.EM_SETSEL, (nuint)at, at, User32.SMTO_ABORTIFHUNG, 200, out _);

        return accepted + _batch.Clear().RestoreModifiers().Send();
    }

    /// <summary>
    /// What a direct edit replaces and inserts, and where the caret goes, in the editor's UTF-16 units. The text is
    /// measured as it is inserted (a line break is one <c>\r</c> there), and the boundary key becomes part of the
    /// edit: a paragraph mark for Enter, a tab for Tab, the character itself otherwise, always at the end.
    /// </summary>
    internal static (int Start, string Insert, int? Cursor) PlanDirectEdit(int end, int deleteUnits, string text, string afterCursor, Boundary? after)
    {
        static string AsEdited(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\n', '\r');
        var beforeCursor = AsEdited(text[..^afterCursor.Length]);
        var insert = beforeCursor + AsEdited(afterCursor);
        if (after is { } boundary)
            insert += boundary.Kind switch { BoundaryKind.Enter => '\r', BoundaryKind.Tab => '\t', _ => boundary.Char };
        var start = end - deleteUnits;
        return (start, insert, afterCursor.Length > 0 ? start + beforeCursor.Length : null);
    }

    /// <summary>EM_REPLACESEL (undoable). The window manager marshals the string for this system edit message.</summary>
    private static unsafe void ReplaceSelection(nint hwnd, string text)
    {
        fixed (char* p = text)
            User32.SendMessageTimeoutW(hwnd, User32.EM_REPLACESEL, 1, (nint)p, User32.SMTO_ABORTIFHUNG, 500, out _);
    }

    /// <summary>The selection of an Edit/RichEdit control (EM_GETSEL with output pointers: full 32-bit indexes), or null.</summary>
    private static unsafe (int Start, int End)? Selection(nint hwnd)
    {
        uint start = 0, end = 0;
        return User32.SendMessageTimeoutW(hwnd, User32.EM_GETSEL, (nuint)(&start), (nint)(&end), User32.SMTO_ABORTIFHUNG, 100, out _) != 0
            ? ((int)start, (int)end)
            : null;
    }

    private uint Replay(InsertionJob job)
    {
        if (job.Replay is not { Length: > 0 } keys)
            return 0;
        if (job.PacedEditor != 0)
            return ReplayPaced(job.PacedEditor, keys, job.ReplayKeyUp!);
        _batch.Clear();
        for (var i = 0; i < keys.Length; i++)
            _batch.Replay(keys[i], job.ReplayKeyUp![i]);
        return _batch.Send();
    }

    /// <summary>
    /// Replays held keys into an editor that applies queued keys against the live keyboard state: sent as one
    /// burst, a lagging Notepad typed "." for ":" and dropped letters (measured). One event at a time, and after
    /// each key-down the editor must show it was applied (its selection changes) before the next event goes.
    /// </summary>
    private uint ReplayPaced(nint editor, KBDLLHOOKSTRUCT[] keys, bool[] keyUp)
    {
        uint accepted = 0;
        for (var i = 0; i < keys.Length; i++)
        {
            var before = keyUp[i] || IsModifier(keys[i].vkCode) ? null : Selection(editor);
            accepted += _batch.Clear().Replay(keys[i], keyUp[i]).Send();
            if (before is { } b)
            {
                var deadline = Environment.TickCount64 + 200;
                while (Selection(editor) is { } now && now == b && Environment.TickCount64 < deadline)
                    Thread.Yield();
            }
        }

        return accepted;
    }

    private static bool IsModifier(uint vk) => vk is VirtualKey.VK_SHIFT or VirtualKey.VK_LSHIFT or VirtualKey.VK_RSHIFT
        or VirtualKey.VK_CONTROL or VirtualKey.VK_LCONTROL or VirtualKey.VK_RCONTROL or VirtualKey.VK_MENU
        or VirtualKey.VK_LMENU or VirtualKey.VK_RMENU or VirtualKey.VK_LWIN or VirtualKey.VK_RWIN or VirtualKey.VK_CAPITAL;

    // ---------------- clipboard fallback ----------------

    private uint PasteViaClipboard(InsertionJob job, string text, string afterCursor)
    {
        // The user's clipboard is restored once insertions pause (see Run), not between back-to-back pastes:
        // replacing and restoring it in quick succession made apps miss some pastes (measured in Notepad).
        _clipboardToRestore ??= SaveClipboard();
        if (_clipboardToRestore is null || !OpenClipboardRetry())
        {
            // Another program holds the clipboard: type the text rather than lose it (the commit key is gone).
            Log.Warn("clipboard busy; typing instead");
            return TypeBatch(job, text, afterCursor);
        }

        var caretBack = CaretBack(job, afterCursor);
        // A character boundary (the space after an abbreviation) is pasted with the text: one edit, not two.
        var after = job.After;
        if (after is { Kind: BoundaryKind.Char } boundary)
        {
            text += boundary.Char;
            after = null;
        }

        try
        {
            User32.EmptyClipboard();
            // Delayed rendering: the text is handed over in WM_RENDERFORMAT, which tells us exactly when the
            // target app has pasted, so the old clipboard can be restored without guessing a delay.
            _pendingRender = text;
            _rendered = false;
            User32.SetClipboardData(CF_UNICODETEXT, 0);
            MarkPrivate();
        }
        finally
        {
            User32.CloseClipboard();
        }

        var accepted = _batch.Clear().ReleaseModifiers().Backspaces(job.DeleteCount).PressCtrlV().Send();

        // Ctrl stays down until the app has read the clipboard: an app that handles the V key late must still see it.
        var sw = Stopwatch.StartNew();
        PumpUntil(() => _rendered, TimeSpan.FromMilliseconds(1000));
        if (!_rendered)
            Log.Warn($"clipboard paste was not picked up within {sw.ElapsedMilliseconds} ms");
        _pendingRender = null;
        accepted += _batch.Clear().ReleaseCtrl().Send();
        if (!_rendered)
        {
            // The app ignored Ctrl+V: type the text instead so it isn't lost.
            accepted += _batch.Clear().Text(text).Send();
        }

        // Caret moves and an Enter/Tab boundary only now: apps may paste asynchronously.
        _batch.Clear().Lefts(caretBack);
        if (after is { } b)
            _batch.Boundary(b);
        accepted += _batch.RestoreModifiers().Send();
        return accepted;
    }

    private const int ClipboardRestoreDelayMs = 250;

    private List<(uint Format, byte[] Data)>? _clipboardToRestore;

    private void RestorePendingClipboard()
    {
        if (_clipboardToRestore is { } saved)
        {
            _clipboardToRestore = null;
            RestoreClipboard(saved);
        }
    }

    private static void PumpUntil(Func<bool> done, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (!done())
        {
            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
                return;
            _ = User32.MsgWaitForMultipleObjectsEx(0, null, (uint)remaining, User32.QS_ALLINPUT, User32.MWMO_INPUTAVAILABLE);
            while (User32.PeekMessageW(out var msg, 0, 0, 0, User32.PM_REMOVE))
                User32.DispatchMessageW(msg);
        }
    }

    private bool OpenClipboardRetry()
    {
        // Another app may hold the clipboard for a moment (clipboard managers); give it up to ~200 ms.
        for (var i = 0; i < 20; i++)
        {
            if (User32.OpenClipboard(_window))
                return true;
            PumpUntil(static () => false, TimeSpan.FromMilliseconds(10));
        }

        Log.Warn("clipboard busy");
        return false;
    }

    private List<(uint Format, byte[] Data)>? SaveClipboard()
    {
        if (!OpenClipboardRetry())
            return null;
        try
        {
            var saved = new List<(uint, byte[])>();
            uint format = 0;
            while ((format = User32.EnumClipboardFormats(format)) != 0)
            {
                if (IsGdiFormat(format))
                    continue;
                var handle = User32.GetClipboardData(format);
                if (handle == 0)
                    continue;
                var size = (int)Kernel32.GlobalSize(handle);
                var p = Kernel32.GlobalLock(handle);
                if (p == 0)
                    continue;
                try
                {
                    var bytes = new byte[size];
                    Marshal.Copy(p, bytes, 0, size);
                    saved.Add((format, bytes));
                }
                finally
                {
                    Kernel32.GlobalUnlock(handle);
                }
            }

            return saved;
        }
        finally
        {
            User32.CloseClipboard();
        }
    }

    private void RestoreClipboard(List<(uint Format, byte[] Data)> saved)
    {
        if (!OpenClipboardRetry())
            return;
        try
        {
            User32.EmptyClipboard();
            foreach (var (format, data) in saved)
                SetData(format, data);
            if (saved.Count > 0)
                MarkPrivate(); // the user's content is already in their history; don't add it twice
        }
        finally
        {
            User32.CloseClipboard();
        }
    }

    private void MarkPrivate()
    {
        SetData(_excludeFromHistory, [0, 0, 0, 0]);
        SetData(_canIncludeInHistory, [0, 0, 0, 0]);
    }

    private static void SetData(uint format, ReadOnlySpan<byte> data)
    {
        var handle = Kernel32.GlobalAlloc(Kernel32.GMEM_MOVEABLE, (nuint)Math.Max(data.Length, 1));
        if (handle == 0)
            return;
        var p = Kernel32.GlobalLock(handle);
        data.CopyTo(new Span<byte>((void*)p, data.Length));
        Kernel32.GlobalUnlock(handle);
        if (User32.SetClipboardData(format, handle) == 0)
            Kernel32.GlobalFree(handle);
    }

    private static bool IsGdiFormat(uint f) => f is 2 or 3 or 9 or 14 or 0x80 or 0x82 or 0x83 or 0x8E || (f >= 0x0300 && f <= 0x03FF);

    private string ReadClipboardText()
    {
        if (!User32.IsClipboardFormatAvailable(CF_UNICODETEXT) || !OpenClipboardRetry())
            return "";
        try
        {
            var handle = User32.GetClipboardData(CF_UNICODETEXT);
            var p = handle == 0 ? 0 : Kernel32.GlobalLock(handle);
            if (p == 0)
                return "";
            try
            {
                return new string((char*)p);
            }
            finally
            {
                Kernel32.GlobalUnlock(handle);
            }
        }
        finally
        {
            User32.CloseClipboard();
        }
    }

    // ---------------- message-only window (clipboard owner) ----------------

    private void CreateMessageWindow()
    {
        fixed (char* name = "Comet.Injector")
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = Kernel32.GetModuleHandleW(null),
                lpszClassName = name,
            };
            User32.RegisterClassExW(wc);
        }

        _window = User32.CreateWindowExW(0, "Comet.Injector", null, 0, 0, 0, 0, 0, User32.HWND_MESSAGE, 0, Kernel32.GetModuleHandleW(null), 0);
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        var self = s_instance;
        if (self is not null && (msg == User32.WM_RENDERFORMAT || msg == User32.WM_RENDERALLFORMATS))
        {
            try
            {
                var text = self._pendingRender ?? "";
                var bytes = new byte[(text.Length + 1) * 2];
                MemoryMarshal.AsBytes(text.AsSpan()).CopyTo(bytes);
                if (msg == User32.WM_RENDERALLFORMATS)
                {
                    if (!User32.OpenClipboard(hwnd))
                        return 0;
                    SetData(CF_UNICODETEXT, bytes);
                    User32.CloseClipboard();
                }
                else
                {
                    SetData(CF_UNICODETEXT, bytes);
                }

                self._rendered = true;
            }
            catch (Exception ex)
            {
                Log.Error("clipboard render", ex);
            }

            return 0;
        }

        return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
        _ready.Dispose();
    }
}
