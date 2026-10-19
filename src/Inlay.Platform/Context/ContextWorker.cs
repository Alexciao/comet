using System.Collections.Concurrent;
using Inlay.Platform.Native;

namespace Inlay.Platform.Context;

internal enum CaretMethod : byte
{
    None,
    GuiThreadInfo,
    UiAutomation,
    Msaa,
    FocusedElement,
    Mouse,
}

internal enum ContextRequestKind : byte
{
    Password,
    Caret,
}

internal sealed record ContextRequest(ContextRequestKind Kind, FocusContext Context, long Started);

internal sealed record ContextResult(ContextRequestKind Kind, int Generation, Tristate Password, RECT Caret, CaretMethod Method, long Started);

/// <summary>
/// Answers questions that need cross-process calls (UI Automation can take tens of milliseconds, or hang
/// with an unresponsive app) on its own MTA thread, so the hook thread never waits. Results carry the focus
/// generation they were computed for; the engine drops stale ones.
/// </summary>
internal sealed class ContextWorker : IDisposable
{
    private readonly Action<ContextResult> _deliver;
    private readonly Thread _thread;
    private readonly BlockingCollection<ContextRequest> _queue = new(new ConcurrentQueue<ContextRequest>());
    private readonly ConcurrentDictionary<string, CaretMethod> _caretMethodByProcess = new(StringComparer.Ordinal);
    private nint _uia;

    public ContextWorker(Action<ContextResult> deliver)
    {
        _deliver = deliver;
        _thread = new Thread(Run) { IsBackground = true, Name = "Inlay context" };
        _thread.Start();
    }

    public void Request(ContextRequestKind kind, FocusContext context)
    {
        Volatile.Write(ref _latestGeneration, Math.Max(Volatile.Read(ref _latestGeneration), context.Generation));
        _queue.TryAdd(new ContextRequest(kind, context, Environment.TickCount64));
    }

    // Requests for a focus that has moved on are skipped, so a hung app can't pile up work behind it.
    private int _latestGeneration;

    private void Run()
    {
        _ = Ole32.CoInitializeEx(0, Ole32.COINIT_MULTITHREADED);
        try
        {
            foreach (var request in _queue.GetConsumingEnumerable())
            {
                if (request.Context.Generation < Volatile.Read(ref _latestGeneration))
                    continue;
                try
                {
                    _deliver(request.Kind == ContextRequestKind.Password ? Password(request) : Caret(request));
                }
                catch (Exception ex)
                {
                    Log.Error("context worker", ex);
                }
            }
        }
        finally
        {
            Uia.Release(_uia);
            Ole32.CoUninitialize();
        }
    }

    private nint Automation => _uia != 0 ? _uia : (_uia = Uia.Create());

    private ContextResult Password(ContextRequest r)
    {
        var answer = Tristate.No;
        var element = Automation != 0 ? Uia.FocusedElement(_uia) : 0;
        if (element != 0)
        {
            try
            {
                // Only trust the answer if focus is still in the process we were asked about.
                if (Uia.ProcessId(element) == r.Context.ProcessId)
                    answer = Uia.IsPassword(element) == true ? Tristate.Yes : Tristate.No;
                else
                    answer = Tristate.Unknown;
            }
            finally
            {
                Uia.Release(element);
            }
        }

        return new ContextResult(ContextRequestKind.Password, r.Context.Generation, answer, default, CaretMethod.None, r.Started);
    }

    /// <summary>Fastest method first; the method that worked is remembered per process and tried first next time.</summary>
    private ContextResult Caret(ContextRequest r)
    {
        var ctx = r.Context;
        User32.GetWindowRect(ctx.Foreground, out var window);

        if (_caretMethodByProcess.TryGetValue(ctx.ProcessName, out var cached) && TryMethod(cached, ctx, window, out var rect))
            return new ContextResult(ContextRequestKind.Caret, ctx.Generation, Tristate.Unknown, rect, cached, r.Started);

        foreach (var method in s_chain)
        {
            if (method == cached)
                continue;
            if (TryMethod(method, ctx, window, out rect))
            {
                if (method != CaretMethod.Mouse)
                    _caretMethodByProcess[ctx.ProcessName] = method;
                return new ContextResult(ContextRequestKind.Caret, ctx.Generation, Tristate.Unknown, rect, method, r.Started);
            }
        }

        return new ContextResult(ContextRequestKind.Caret, ctx.Generation, Tristate.Unknown, default, CaretMethod.None, r.Started);
    }

    private static readonly CaretMethod[] s_chain = [CaretMethod.GuiThreadInfo, CaretMethod.UiAutomation, CaretMethod.Msaa, CaretMethod.FocusedElement, CaretMethod.Mouse];

    private bool TryMethod(CaretMethod method, FocusContext ctx, RECT window, out RECT rect)
    {
        rect = default;
        RECT? found = method switch
        {
            CaretMethod.GuiThreadInfo => FromGuiThreadInfo(ctx.ThreadId),
            CaretMethod.UiAutomation => WithFocusedElement(Uia.CaretRect),
            CaretMethod.Msaa => Uia.MsaaCaret(ctx.Focus),
            CaretMethod.FocusedElement => WithFocusedElement(e => Uia.BoundingRectangle(e) is { } b ? new RECT { Left = b.Left, Top = b.Bottom - 20, Right = b.Left + 1, Bottom = b.Bottom } : null),
            CaretMethod.Mouse => User32.GetCursorPos(out var p) ? new RECT { Left = p.X, Top = p.Y, Right = p.X + 1, Bottom = p.Y + 16 } : null,
            _ => null,
        };

        if (found is not { } r || r.Height <= 0)
            return false;
        // Reject stale or bogus carets that lie outside the focused window.
        if (method != CaretMethod.Mouse && !window.IsEmpty && (r.Bottom < window.Top || r.Top > window.Bottom || r.Right < window.Left || r.Left > window.Right))
            return false;
        rect = r;
        return true;
    }

    private RECT? WithFocusedElement(Func<nint, RECT?> read)
    {
        if (Automation == 0)
            return null;
        var element = Uia.FocusedElement(_uia);
        if (element == 0)
            return null;
        try
        {
            return read(element);
        }
        finally
        {
            Uia.Release(element);
        }
    }

    private static RECT? FromGuiThreadInfo(uint threadId)
    {
        var info = new GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<GUITHREADINFO>() };
        if (!User32.GetGUIThreadInfo(threadId, ref info) || info.hwndCaret == 0 || info.rcCaret.Height <= 0)
            return null;
        var topLeft = new POINT { X = info.rcCaret.Left, Y = info.rcCaret.Top };
        var bottomRight = new POINT { X = info.rcCaret.Right, Y = info.rcCaret.Bottom };
        User32.ClientToScreen(info.hwndCaret, ref topLeft);
        User32.ClientToScreen(info.hwndCaret, ref bottomRight);
        return new RECT { Left = topLeft.X, Top = topLeft.Y, Right = Math.Max(bottomRight.X, topLeft.X + 1), Bottom = bottomRight.Y };
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        // A thread still busy (UI Automation into a hung app) would throw on a disposed queue: leave it to the GC.
        if (_thread.Join(TimeSpan.FromSeconds(1)))
            _queue.Dispose();
    }
}
