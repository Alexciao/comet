using System.Diagnostics;
using Inlay.Core.Search;
using Inlay.Core.Snippets;
using Inlay.Platform;
using Inlay.Platform.Engine;
using Inlay.Platform.Native;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Inlay.App.UI;

/// <summary>
/// The suggestion popup. Created once at startup and reused: no window or row is ever created while typing.
/// <para>
/// No flash, ever: between sessions the window stays shown but DWM-cloaked. To show it, it is positioned and
/// sized while cloaked, its rows are filled, and it is uncloaked only after <see cref="CompositionTarget.Rendered"/>
/// reports the frame with that content. Hiding cloaks immediately and can be done from the hook thread, so the
/// popup vanishes even if the UI thread is busy.
/// </para>
/// <para>
/// Fast updates: eight row elements form a virtualized window over the results, are refilled in place, and draw
/// emoji from pre-rendered bitmaps (see <see cref="EmojiBitmapCache"/>).
/// </para>
/// It never activates: WS_EX_NOACTIVATE, and clicks are handled by the mouse hook before they reach it.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The backdrop controller is disposed when the window closes.")]
public sealed partial class QuickInsertWindow : Window, IPopupSink
{
    private const int VisibleRows = 8;
    private const double WidthEpx = 320;
    private const double PaddingEpx = 4;
    private const double CaretGapEpx = 4;

    /// <summary>Without a caret, Quick Insert sits this far above the bottom of the window.</summary>
    private const double WindowBottomGapEpx = 48;

    internal const uint WmUpdate = User32.WM_APP + 1;
    internal const uint WmHide = User32.WM_APP + 2;

    private readonly nint _hwnd;
    private readonly QuickInsertRow[] _rows = new QuickInsertRow[VisibleRows];
    private readonly SearchResult[] _results = new SearchResult[PopupFrame.MaxResults];
    private readonly char[] _query = new char[32];
    private readonly SystemBackdropConfiguration _backdropConfig;
    private readonly DesktopAcrylicController _acrylic;
    private readonly EmojiBitmapCache _emojiBitmaps;
    private MessageWindow? _messages;
    private PopupFrame? _frame;
    private EngineData _data = EngineData.Empty;
    private int _skinTone;
    private string _trigger = ":";
    private bool _recents;
    private bool _createRow;
    private bool _fromShortcut;
    private bool _anchorIsWindow;
    private int _queryLength;
    private int _updatePending;
    private int _shownVersion = -1;
    private int _count;
    private int _selected;
    private int _first;
    private bool _visible;
    private volatile bool _uncloaked; // also cleared by Hide on the hook thread
    private bool _awaitingFrame;
    private int _hideCount;
    private int _renderHideCount;
    private long _awaitingKeyTimestamp;
    private RECT _windowRect;
    private double _scale = 1;
    private volatile PopupGeometry? _geometry;

    // Ordering of the hook thread's Update and Hide calls: a hide is applied only if no update came after it.
    private long _sequence;
    private long _lastUpdate;
    private long _lastHide;

    public QuickInsertWindow()
    {
        InitializeComponent();
        _hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);

        for (var i = 0; i < _rows.Length; i++)
        {
            _rows[i] = new QuickInsertRow { Visibility = Visibility.Collapsed };
            RowsPanel.Children.Add(_rows[i]);
        }

        // A borderless popup: WS_POPUP keeps exact sizing; DWM supplies Windows 11 corners, border and shadow.
        // Styles are adjusted, not replaced: WinUI's own (e.g. WS_EX_NOREDIRECTIONBITMAP) must stay.
        AppWindow.IsShownInSwitchers = false;
        const int frame = User32.WS_CAPTION | User32.WS_THICKFRAME | User32.WS_SYSMENU | User32.WS_MINIMIZEBOX | User32.WS_MAXIMIZEBOX;
        var style = User32.GetWindowLongPtr(_hwnd, User32.GWL_STYLE);
        User32.SetWindowLongPtr(_hwnd, User32.GWL_STYLE, (style & ~frame) | User32.WS_POPUP);
        var exStyle = User32.GetWindowLongPtr(_hwnd, User32.GWL_EXSTYLE);
        User32.SetWindowLongPtr(_hwnd, User32.GWL_EXSTYLE, (exStyle & ~User32.WS_EX_APPWINDOW) | User32.WS_EX_NOACTIVATE | User32.WS_EX_TOOLWINDOW | User32.WS_EX_TOPMOST);
        User32.SetWindowPos(_hwnd, 0, 0, 0, 0, 0, User32.SWP_FRAMECHANGED | User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
        Dwm.SetInt(_hwnd, Dwm.DWMWA_WINDOW_CORNER_PREFERENCE, Dwm.DWMWCP_ROUND);
        Dwm.SetInt(_hwnd, Dwm.DWMWA_TRANSITIONS_FORCEDISABLED, 1);
        Cloak(true);

        // Desktop acrylic, always drawn as "active" (the popup never has focus). The controller falls back to
        // a solid theme color by itself when transparency effects are off or energy saver is on.
        _backdropConfig = new SystemBackdropConfiguration { IsInputActive = true };
        _acrylic = new DesktopAcrylicController();
        _acrylic.AddSystemBackdropTarget(WinRT.CastExtensions.As<ICompositionSupportsSystemBackdrop>(this));
        _acrylic.SetSystemBackdropConfiguration(_backdropConfig);
        Root.ActualThemeChanged += (_, _) => ApplyTheme();
        ApplyTheme();

        _emojiBitmaps = new EmojiBitmapCache(Stage, DispatcherQueue);
        Closed += (_, _) => _acrylic.Dispose();
    }

    /// <summary>Keystroke → popup rendered, for updates whose emoji were all pre-rendered (the normal, warm case).</summary>
    internal LatencyStats RenderLatency { get; } = new("keystroke to popup rendered");

    /// <summary>Same, for updates that showed an emoji for the first time (its glyph had to be rasterized).</summary>
    internal LatencyStats ColdRenderLatency { get; } = new("keystroke to popup rendered, first-time emoji");

    /// <summary>Connects the window that receives cross-thread update/hide messages.</summary>
    internal void Attach(MessageWindow messages) => _messages = messages;

    private void ApplyTheme()
    {
        _backdropConfig.Theme = Root.ActualTheme == ElementTheme.Light ? SystemBackdropTheme.Light : SystemBackdropTheme.Dark;
        Dwm.SetInt(_hwnd, Dwm.DWMWA_USE_IMMERSIVE_DARK_MODE, Root.ActualTheme == ElementTheme.Dark ? 1 : 0);
    }

    /// <summary>
    /// Shows the window once, cloaked, so the window, its backdrop and the text stack are created and rendered
    /// before the first real use. It then stays shown but cloaked between sessions.
    /// </summary>
    internal void WarmUp()
    {
        _rows[0].ShowEmoji("😀", null, ":", "warm_up", (0, 4));
        _rows[0].Visibility = Visibility.Visible;
        var (w, h) = PhysicalSize(1, 1);
        User32.SetWindowPos(_hwnd, User32.HWND_TOPMOST, 0, 0, w, h, User32.SWP_NOACTIVATE | User32.SWP_SHOWWINDOW);
        void Done(object? sender, object e)
        {
            CompositionTarget.Rendered -= Done;
            _rows[0].Visibility = Visibility.Collapsed;
        }

        CompositionTarget.Rendered += Done;
    }

    /// <summary>Renders these emoji into the bitmap cache when the UI thread is idle (startup warm-up).</summary>
    internal void Prefetch(IEnumerable<string> emoji) => _emojiBitmaps.Prefetch(emoji);

    // ------------------------------------------------------------------ IPopupSink (hook thread)

    PopupGeometry? IPopupSink.Geometry => _geometry;

    void IPopupSink.Update(PopupFrame frame)
    {
        _frame = frame;
        Volatile.Write(ref _lastUpdate, Interlocked.Increment(ref _sequence));
        if (Interlocked.Exchange(ref _updatePending, 1) == 0)
            _messages?.Post(WmUpdate);
    }

    void IPopupSink.Hide()
    {
        // Cloaking is a DWM call, safe from any thread: the popup disappears now, not when the UI thread gets to it.
        _geometry = null;
        Interlocked.Increment(ref _hideCount);
        Volatile.Write(ref _lastHide, Interlocked.Increment(ref _sequence));
        Cloak(true);
        _messages?.Post(WmHide);
    }

    // ------------------------------------------------------------------ UI thread

    /// <summary>Handles a message posted by <see cref="IPopupSink"/>; called by the message window.</summary>
    internal bool HandleMessage(uint message)
    {
        switch (message)
        {
            case WmUpdate:
                Interlocked.Exchange(ref _updatePending, 0);
                Render();
                return true;
            case WmHide:
                // Messages can be handled out of the order the calls were made (updates are coalesced): if the
                // hook thread showed the popup again after this hide, the pending or just-rendered update wins.
                if (Volatile.Read(ref _lastUpdate) > Volatile.Read(ref _lastHide))
                    return true;
                _visible = false;
                _awaitingFrame = false;
                CompositionTarget.Rendered -= OnFrameRendered;
                Cloak(true);
                _data = EngineData.Empty; // hidden: hold no dataset
                if (SearchLine.Visibility == Visibility.Visible)
                {
                    CaretBlink.Stop(); // nothing animates while Quick Insert is hidden
                    SearchLine.Visibility = Visibility.Collapsed;
                }
                return true;
            default:
                return false;
        }
    }

    private void Render()
    {
        var frame = _frame;
        if (frame is null)
            return;

        RECT anchor;
        long keyTimestamp;
        lock (frame.Gate)
        {
            if (frame.Version == _shownVersion && _visible)
                return;
            _shownVersion = frame.Version;
            _count = frame.Count;
            Array.Copy(frame.Results, _results, _count);
            _selected = frame.Selected;
            anchor = frame.Anchor;
            _skinTone = frame.SkinTone;
            _trigger = frame.Trigger;
            _recents = frame.Recents;
            _createRow = frame.CreateRow;
            _fromShortcut = frame.FromShortcut;
            _anchorIsWindow = frame.AnchorIsWindow;
            _data = frame.Data;
            _queryLength = frame.QueryLength;
            frame.Query.AsSpan(0, _queryLength).CopyTo(_query);
            keyTimestamp = frame.KeyTimestamp;
        }

        var workStart = Stopwatch.GetTimestamp();
        _debugQueueMs = Stopwatch.GetElapsedTime(keyTimestamp, workStart).TotalMilliseconds;

        // Keep the selection in the visible window; a new list starts at the top.
        if (!_visible || _selected == 0)
            _first = 0;
        if (_selected < _first)
            _first = _selected;
        else if (_selected >= _first + VisibleRows)
            _first = _selected - VisibleRows + 1;
        _first = Math.Clamp(_first, 0, Math.Max(0, _count - VisibleRows));

        ShowSearchLine();
        FillRows();
        Position(anchor, Math.Min(_count, VisibleRows));
        PublishGeometry();

        _visible = true;

        _debugWorkMs = Stopwatch.GetElapsedTime(workStart).TotalMilliseconds;
        _debugWorkEnd = Stopwatch.GetTimestamp();

        // Uncloak (or just measure) once this content has actually been rendered.
        _awaitingKeyTimestamp = keyTimestamp;
        _renderHideCount = Volatile.Read(ref _hideCount);
        if (!_awaitingFrame)
        {
            _awaitingFrame = true;
            CompositionTarget.Rendered += OnFrameRendered;
        }
    }

    private void FillRows()
    {
        var query = _query.AsSpan(0, _queryLength);
        for (var i = 0; i < _rows.Length; i++)
        {
            var row = _rows[i];
            var index = _first + i;
            if (index >= _count)
            {
                if (row.Visibility == Visibility.Visible)
                    row.Visibility = Visibility.Collapsed;
                continue;
            }

            if (_createRow && index == _count - 1)
            {
                row.ShowCreateSnippet(_trigger, query.ToString());
                row.IsRecent = false;
            }
            else
            {
                Fill(row, _results[index], query);
                row.IsRecent = _recents;
            }

            row.IsSelected = index == _selected;
            if (row.Visibility != Visibility.Visible)
                row.Visibility = Visibility.Visible;
        }

        UpdateScrollThumb();
    }

    private void Fill(QuickInsertRow row, SearchResult r, ReadOnlySpan<char> query)
    {
        if (r.Kind == ResultKind.Emoji && _data.Emoji is { } data && data.IsSymbol(r.Index))
        {
            // Symbols are text in the UI font (with Windows' font fallback), not color emoji bitmaps.
            var code = data.Shortcode(r.Index, r.Slot).ToString();
            row.ShowSymbol(data.Emoji(r.Index).ToString(), _trigger, code, Highlighter.Find(code, query));
        }
        else if (r.Kind == ResultKind.Emoji && _data.Emoji is { } emoji)
        {
            var shortcode = emoji.Shortcode(r.Index, r.Slot).ToString();
            var text = emoji.Emoji(r.Index, _skinTone).ToString();
            var bitmap = _emojiBitmaps.Get(text, Root.XamlRoot?.RasterizationScale ?? _scale);
            if (bitmap is null)
                _misses++;
            row.ShowEmoji(text, bitmap, _trigger, shortcode, Highlighter.Find(shortcode, query));
        }
        else if (r.Kind == ResultKind.Snippet && r.Index < _data.UserItems.Length && _data.UserItems[r.Index] is { Snippet: { } s })
        {
            row.ShowSnippet(_trigger, s.Name, Preview(s.Text), Highlighter.Find(s.Name, query));
        }
    }

    /// <summary>The search line (from the shortcut) shows the query; its caret blinks only while it is visible.</summary>
    private void ShowSearchLine()
    {
        if (_fromShortcut)
        {
            QueryText.Text = new string(_query, 0, _queryLength);
            if (SearchLine.Visibility != Visibility.Visible)
            {
                SearchLine.Visibility = Visibility.Visible;
                CaretBlink.Begin();
            }
        }
        else if (SearchLine.Visibility == Visibility.Visible)
        {
            CaretBlink.Stop();
            SearchLine.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Height of the search line when it is shown (rows start below it).</summary>
    private double HeaderEpx => _fromShortcut ? QuickInsertRow.RowHeight : 0;

    /// <summary>
    /// The first line of what the snippet inserts now (dates and times resolved; the row ellipsizes it), with " …"
    /// when more lines follow.
    /// </summary>
    private static string Preview(string template)
    {
        var text = SnippetTemplate.Preview(template, DateTime.Now, System.Globalization.CultureInfo.CurrentCulture);
        var span = text.AsSpan().Trim();
        var newline = span.IndexOfAny('\r', '\n');
        var first = newline < 0 ? span : span[..newline];
        return newline < 0 ? first.ToString() : first.ToString() + " …";
    }

    private void UpdateScrollThumb()
    {
        if (_count <= VisibleRows)
        {
            ScrollThumb.Visibility = Visibility.Collapsed;
            return;
        }

        var track = VisibleRows * QuickInsertRow.RowHeight;
        ScrollThumb.Height = Math.Max(16, track * VisibleRows / _count);
        ScrollThumb.Margin = new Thickness(0, PaddingEpx + HeaderEpx + ((track - ScrollThumb.Height) * _first / (_count - VisibleRows)), 2, 0);
        ScrollThumb.Visibility = Visibility.Visible;
    }

    private void OnPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        if (!_visible || _count <= VisibleRows)
            return;
        var delta = e.GetCurrentPoint(Root).Properties.MouseWheelDelta;
        var first = Math.Clamp(_first - Math.Sign(delta), 0, _count - VisibleRows);
        if (first == _first)
            return;
        _first = first;
        FillRows();
        PublishGeometry();
        e.Handled = true;
    }

    private void OnFrameRendered(object? sender, object e)
    {
        CompositionTarget.Rendered -= OnFrameRendered;
        _awaitingFrame = false;
        // Skip if the hook thread hid the popup after this content was produced.
        if (!_visible || Volatile.Read(ref _hideCount) != _renderHideCount)
            return;
        // No enter animation: the acrylic backdrop belongs to the window and can't fade with the content, so a fade
        // showed an empty panel for a frame (seen in frame captures). The popup appears complete in one frame.
        if (!_uncloaked)
            Cloak(false);

        if (_awaitingKeyTimestamp != 0)
        {
            (_misses == 0 ? RenderLatency : ColdRenderLatency).Add(Stopwatch.GetElapsedTime(_awaitingKeyTimestamp).TotalMicroseconds);
            Log.Debug(FormattableString.Invariant($"render: queue {_debugQueueMs:F1} ms, work {_debugWorkMs:F1} ms, to frame {Stopwatch.GetElapsedTime(_debugWorkEnd).TotalMilliseconds:F1} ms, new emoji {_misses}"));
            _misses = 0;
            _awaitingKeyTimestamp = 0;
        }
    }

    // Debug-build diagnostics for the render path (Log.Debug is compiled out of release builds).
    private double _debugQueueMs;
    private double _debugWorkMs;
    private long _debugWorkEnd;
    private int _misses;

    // ------------------------------------------------------------------ geometry

    private (int Width, int Height) PhysicalSize(int rows, double scale) =>
        ((int)Math.Round(WidthEpx * scale), (int)Math.Round(((rows * QuickInsertRow.RowHeight) + HeaderEpx + (2 * PaddingEpx)) * scale));

    /// <summary>Below the caret; above it near the bottom of the screen; always inside the monitor's work area.</summary>
    private void Position(RECT caret, int rows)
    {
        var monitor = User32.MonitorFromRect(caret, User32.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        User32.GetMonitorInfoW(monitor, ref info);
        _scale = ShCore.GetDpiForMonitor(monitor, ShCore.MDT_EFFECTIVE_DPI, out var dpi, out _) >= 0 ? dpi / 96.0 : 1.0;
        var work = info.rcWork;
        var (w, h) = PhysicalSize(rows, _scale);
        var gap = (int)Math.Round(CaretGapEpx * _scale);

        var x = caret.Left;
        var y = caret.Bottom + gap;
        if (_anchorIsWindow)
        {
            // No caret: the bottom center of the window, like a command bar.
            x = ((caret.Left + caret.Right) / 2) - (w / 2);
            y = caret.Bottom - (int)Math.Round(WindowBottomGapEpx * _scale) - h;
        }
        else if (y + h > work.Bottom)
        {
            y = caret.Top - gap - h;
        }
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - w));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - h));

        var rect = new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
        if (rect.Left == _windowRect.Left && rect.Top == _windowRect.Top && rect.Width == _windowRect.Width && rect.Height == _windowRect.Height)
            return;
        _windowRect = rect;
        User32.SetWindowPos(_hwnd, User32.HWND_TOPMOST, x, y, w, h, User32.SWP_NOACTIVATE | User32.SWP_NOOWNERZORDER);
    }

    private void PublishGeometry() =>
        _geometry = new PopupGeometry(_windowRect, (int)Math.Round((PaddingEpx + HeaderEpx) * _scale), (int)Math.Round(QuickInsertRow.RowHeight * _scale), _first, VisibleRows, _shownVersion);

    // ------------------------------------------------------------------ cloak

    private void Cloak(bool cloak)
    {
        _uncloaked = !cloak;
        Dwm.SetInt(_hwnd, Dwm.DWMWA_CLOAK, cloak ? 1 : 0);
    }

    /// <summary>Logs latency percentiles (on exit).</summary>
    internal void LogLatency()
    {
        Log.Info(RenderLatency.Describe());
        Log.Info(ColdRenderLatency.Describe());
    }
}
