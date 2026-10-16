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
/// <param name="IsTerminal">The target is a console or terminal (command-line editing: no select-and-replace).</param>
internal sealed record InsertionJob(
    int DeleteCount,
    string? Text,
    string? SnippetTemplate,
    Boundary? After,
    InsertionMethod Method,
    long KeyTimestamp,
    bool IsTerminal = false)
{
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

    string ISnippetEnvironment.ClipboardText() => ReadClipboardText();

    private void Run()
    {
        CreateMessageWindow();
        _ready.Set();
        foreach (var job in _queue.GetConsumingEnumerable())
        {
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

        User32.DestroyWindow(_window);
    }

    private uint Execute(InsertionJob job)
    {
        if (job.Text is null && job.SnippetTemplate is null)
            return Replay(job);

        var text = job.Text ?? "";
        var caretBack = 0;
        if (job.SnippetTemplate is not null)
        {
            var expansion = SnippetTemplate.Expand(job.SnippetTemplate, this);
            text = expansion.Text;
            caretBack = SearchText.GraphemeCount(expansion.AfterCursor);
        }

        // Multi-line text goes through the clipboard: typing Enter would send a chat message mid-snippet.
        var method = job.Method == InsertionMethod.Clipboard || text.Contains('\n', StringComparison.Ordinal) ? InsertionMethod.Clipboard : InsertionMethod.Unicode;
        uint accepted;
        if (method == InsertionMethod.Unicode)
        {
            _batch.Clear();
            // Emoji (surrogate pairs) replace a selection instead of following backspaces; see InputBatch.SelectBack.
            // Terminals edit a command line, where typing doesn't replace a selection: they always get backspaces.
            if (!job.IsTerminal && HasSurrogates(text))
                _batch.SelectBack(job.DeleteCount, (User32.GetAsyncKeyState(VirtualKey.VK_SHIFT) & 0x8000) != 0);
            else
                _batch.Backspaces(job.DeleteCount);
            _batch.Text(text).Lefts(caretBack);
            if (job.After is { } after)
                _batch.Boundary(after);
            accepted = _batch.Send();
        }
        else
        {
            accepted = PasteViaClipboard(job.DeleteCount, text, caretBack, job.After);
        }

        return accepted + Replay(job);
    }

    private static bool HasSurrogates(string text) => text.AsSpan().IndexOfAnyInRange('\uD800', '\uDFFF') >= 0;

    private uint Replay(InsertionJob job)
    {
        if (job.Replay is not { Length: > 0 } keys)
            return 0;
        _batch.Clear();
        for (var i = 0; i < keys.Length; i++)
            _batch.Replay(keys[i], job.ReplayKeyUp![i]);
        return _batch.Send();
    }

    // ---------------- clipboard fallback ----------------

    private uint PasteViaClipboard(int deleteCount, string text, int caretBack, Boundary? after)
    {
        var saved = SaveClipboard();
        if (saved is null || !OpenClipboardRetry())
            return 0;

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

        _batch.Clear().Backspaces(deleteCount).Paste().Lefts(caretBack);
        if (after is { } b)
            _batch.Boundary(b);
        var accepted = _batch.Send();

        var sw = Stopwatch.StartNew();
        PumpUntil(() => _rendered, TimeSpan.FromMilliseconds(1000));
        if (!_rendered)
            Log.Warn($"clipboard paste was not picked up within {sw.ElapsedMilliseconds} ms");
        _pendingRender = null;
        RestoreClipboard(saved);
        return accepted;
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
