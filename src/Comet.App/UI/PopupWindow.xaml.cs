using System.Diagnostics;
using Comet.Core.Search;
using Comet.Platform;
using Comet.Platform.Engine;
using Comet.Platform.Native;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace Comet.App.UI;

/// <summary>
/// The suggestion popup. Created once at startup, rendered once while cloaked (so fonts, the emoji font and the
/// acrylic backdrop are warm), then reused: never created on the hot path.
/// <para>
/// Flicker-free showing: the window stays DWM-cloaked while it is positioned, sized and its new content is
/// rendered; it is uncloaked only after <see cref="CompositionTarget.Rendered"/> reports that frame. Hiding
/// cloaks immediately — callable from the hook thread, so the popup vanishes even if the UI thread is busy.
/// </para>
/// It never activates: WS_EX_NOACTIVATE, and clicks are handled by the mouse hook before they reach it.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The backdrop controller is disposed when the window closes.")]
public sealed partial class PopupWindow : Window, IPopupSink
{
    private const int MaxVisibleRows = 8;
    private const double WidthEpx = 320;
    private const double PaddingEpx = 4;
    private const double CaretGapEpx = 4;

    internal const uint WmUpdate = User32.WM_APP + 1;
    internal const uint WmHide = User32.WM_APP + 2;

    private readonly nint _hwnd;
    private readonly PopupRow[] _rows = new PopupRow[PopupFrame.MaxResults];
    private readonly SearchResult[] _results = new SearchResult[PopupFrame.MaxResults];
    private readonly char[] _query = new char[32];
    private readonly UISettings _uiSettings = new();
    private readonly SystemBackdropConfiguration _backdropConfig;
    private readonly DesktopAcrylicController _acrylic;
    private readonly Visual _rootVisual;
    private MessageWindow? _messages;
    private PopupFrame? _frame;
    private int _updatePending;
    private int _shownVersion = -1;
    private int _count;
    private int _selected;
    private bool _visible;
    private bool _awaitingFrame;
    private bool _animateOnUncloak;
    private int _hideCount;
    private int _renderHideCount;
    private long _awaitingKeyTimestamp;
    private RECT _windowRect;
    private double _scale = 1;
    private volatile bool _cloaked;
    private PopupGeometry? _geometry;

    public PopupWindow()
    {
        InitializeComponent();
        _hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);

        for (var i = 0; i < _rows.Length; i++)
        {
            _rows[i] = new PopupRow { Visibility = Visibility.Collapsed };
            RowsPanel.Children.Add(_rows[i]);
        }

        // A borderless popup: WS_POPUP keeps exact sizing; DWM supplies Windows 11 corners, border and shadow.
        AppWindow.IsShownInSwitchers = false;
        User32.SetWindowLongPtr(_hwnd, User32.GWL_STYLE, User32.WS_POPUP);
        User32.SetWindowLongPtr(_hwnd, User32.GWL_EXSTYLE, User32.WS_EX_NOACTIVATE | User32.WS_EX_TOOLWINDOW | User32.WS_EX_TOPMOST);
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

        _rootVisual = ElementCompositionPreview.GetElementVisual(Root);
        Closed += (_, _) => _acrylic.Dispose();
    }

    internal nint Handle => _hwnd;

    /// <summary>Keystroke → popup rendered, measured per update.</summary>
    internal LatencyStats RenderLatency { get; } = new("keystroke to popup rendered");

    /// <summary>Connects the window that receives cross-thread update/hide messages.</summary>
    internal void Attach(MessageWindow messages) => _messages = messages;

    private void ApplyTheme()
    {
        _backdropConfig.Theme = Root.ActualTheme == ElementTheme.Light ? SystemBackdropTheme.Light : SystemBackdropTheme.Dark;
        Dwm.SetInt(_hwnd, Dwm.DWMWA_USE_IMMERSIVE_DARK_MODE, Root.ActualTheme == ElementTheme.Dark ? 1 : 0);
    }

    /// <summary>
    /// Renders sample rows once while cloaked so the first real show doesn't pay for font loading
    /// (including Segoe UI Emoji's color glyphs) or backdrop creation.
    /// </summary>
    internal void WarmUp()
    {
        string[] samples = ["😀", "😂", "❤️", "👍", "🎉", "🔥", "🙏", "✅"];
        for (var i = 0; i < samples.Length; i++)
        {
            _rows[i].ShowEmoji(samples[i], "warm_up", (0, 4));
            _rows[i].Visibility = Visibility.Visible;
        }

        var size = PhysicalSize(samples.Length, 1);
        User32.SetWindowPos(_hwnd, User32.HWND_TOPMOST, -32000, -32000, size.Width, size.Height, User32.SWP_NOACTIVATE | User32.SWP_SHOWWINDOW);
        void Done(object? sender, object e)
        {
            CompositionTarget.Rendered -= Done;
            User32.ShowWindow(_hwnd, User32.SW_HIDE);
            foreach (var row in _rows)
                row.Visibility = Visibility.Collapsed;
        }

        CompositionTarget.Rendered += Done;
    }

    // ------------------------------------------------------------------ IPopupSink (hook thread)

    PopupGeometry? IPopupSink.Geometry => _geometry;

    void IPopupSink.Update(PopupFrame frame)
    {
        _frame = frame;
        if (Interlocked.Exchange(ref _updatePending, 1) == 0)
            _messages?.Post(WmUpdate);
    }

    void IPopupSink.Hide()
    {
        // Cloaking is a DWM call, safe from any thread: the popup disappears now, not when the UI thread gets to it.
        _geometry = null;
        Interlocked.Increment(ref _hideCount);
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
                HideNow();
                return true;
            default:
                return false;
        }
    }

    private void HideNow()
    {
        _visible = false;
        _awaitingFrame = false;
        CompositionTarget.Rendered -= OnFrameRendered;
        Cloak(true);
        User32.ShowWindow(_hwnd, User32.SW_HIDE);
    }

    private void Render()
    {
        var frame = _frame;
        if (frame is null)
            return;

        RECT anchor;
        int skinTone;
        EngineData data;
        int queryLength;
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
            skinTone = frame.SkinTone;
            data = frame.Data;
            queryLength = frame.QueryLength;
            frame.Query.AsSpan(0, queryLength).CopyTo(_query);
            keyTimestamp = frame.KeyTimestamp;
        }

        var query = _query.AsSpan(0, queryLength);
        for (var i = 0; i < _rows.Length; i++)
        {
            var row = _rows[i];
            if (i >= _count)
            {
                if (row.Visibility == Visibility.Visible)
                    row.Visibility = Visibility.Collapsed;
                continue;
            }

            Fill(row, _results[i], data, skinTone, query);
            row.IsSelected = i == _selected;
            if (row.Visibility != Visibility.Visible)
                row.Visibility = Visibility.Visible;
        }

        Position(anchor, Math.Min(_count, MaxVisibleRows));
        KeepSelectionVisible();
        PublishGeometry();

        if (!_visible)
        {
            _visible = true;
            _animateOnUncloak = _uiSettings.AnimationsEnabled;
            if (_animateOnUncloak)
                PrepareEnterAnimation();
            User32.ShowWindow(_hwnd, User32.SW_SHOWNOACTIVATE); // still cloaked
        }

        // Uncloak (or just measure) once this content has actually been rendered.
        _awaitingKeyTimestamp = keyTimestamp;
        _renderHideCount = Volatile.Read(ref _hideCount);
        if (!_awaitingFrame)
        {
            _awaitingFrame = true;
            CompositionTarget.Rendered += OnFrameRendered;
        }
    }

    private void OnFrameRendered(object? sender, object e)
    {
        CompositionTarget.Rendered -= OnFrameRendered;
        _awaitingFrame = false;
        // Skip if the hook thread hid the popup after this content was produced.
        if (!_visible || Volatile.Read(ref _hideCount) != _renderHideCount)
            return;
        if (_cloaked)
        {
            Cloak(false);
            if (_animateOnUncloak)
                PlayEnterAnimation();
            _animateOnUncloak = false;
        }

        if (_awaitingKeyTimestamp != 0)
        {
            RenderLatency.Add(Stopwatch.GetElapsedTime(_awaitingKeyTimestamp).TotalMicroseconds);
            _awaitingKeyTimestamp = 0;
        }
    }

    private static void Fill(PopupRow row, SearchResult r, EngineData data, int skinTone, ReadOnlySpan<char> query)
    {
        if (r.Kind == ResultKind.Emoji && data.Emoji is { } emoji)
        {
            var shortcode = emoji.Shortcode(r.Index, r.Slot).ToString();
            row.ShowEmoji(emoji.Emoji(r.Index, skinTone).ToString(), shortcode, Highlighter.Find(shortcode, query));
        }
        else if (r.Kind == ResultKind.Snippet && r.Index < data.Snippets.Length)
        {
            var s = data.Snippets[r.Index];
            row.ShowSnippet(s.Name, Preview(s.Text), Highlighter.Find(s.Name, query));
        }
    }

    /// <summary>First line of the snippet text, whitespace collapsed (the row ellipsizes it).</summary>
    private static string Preview(string text)
    {
        var span = text.AsSpan().Trim();
        var newline = span.IndexOfAny('\r', '\n');
        var first = newline < 0 ? span : span[..newline];
        return newline < 0 ? first.ToString() : first.ToString() + " …";
    }

    // ------------------------------------------------------------------ geometry

    private (int Width, int Height) PhysicalSize(int rows, double scale) =>
        ((int)Math.Round(WidthEpx * scale), (int)Math.Round(((rows * PopupRow.RowHeight) + (2 * PaddingEpx)) * scale));

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
        if (y + h > work.Bottom)
            y = caret.Top - gap - h;
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - w));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - h));

        var rect = new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
        if (rect.Left == _windowRect.Left && rect.Top == _windowRect.Top && rect.Width == _windowRect.Width && rect.Height == _windowRect.Height && _visible)
            return;
        _windowRect = rect;
        User32.SetWindowPos(_hwnd, User32.HWND_TOPMOST, x, y, w, h, User32.SWP_NOACTIVATE | User32.SWP_NOOWNERZORDER);
    }

    private void KeepSelectionVisible()
    {
        var first = (int)Math.Round(Scroller.VerticalOffset / PopupRow.RowHeight);
        if (_selected < first)
            first = _selected;
        else if (_selected >= first + MaxVisibleRows)
            first = _selected - MaxVisibleRows + 1;
        if (_selected == 0)
            first = 0;
        Scroller.ChangeView(null, first * PopupRow.RowHeight, null, disableAnimation: true);
    }

    private void PublishGeometry()
    {
        var first = (int)Math.Round(Scroller.VerticalOffset / PopupRow.RowHeight);
        _geometry = new PopupGeometry(_windowRect, (int)Math.Round(PaddingEpx * _scale), (int)Math.Round(PopupRow.RowHeight * _scale), first, MaxVisibleRows);
    }

    // ------------------------------------------------------------------ cloak + motion

    private void Cloak(bool cloak)
    {
        _cloaked = cloak;
        Dwm.SetInt(_hwnd, Dwm.DWMWA_CLOAK, cloak ? 1 : 0);
    }

    private void PrepareEnterAnimation()
    {
        _rootVisual.Opacity = 0;
        _rootVisual.Offset = new System.Numerics.Vector3(0, -6, 0);
    }

    /// <summary>A quick fade and slide (~120 ms), like Windows 11 flyouts. Selection changes never animate.</summary>
    private void PlayEnterAnimation()
    {
        var compositor = _rootVisual.Compositor;
        var ease = compositor.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0f, 0f), new System.Numerics.Vector2(0f, 1f));
        var duration = TimeSpan.FromMilliseconds(120);

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, 1f, ease);
        fade.Duration = duration;
        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(1f, System.Numerics.Vector3.Zero, ease);
        slide.Duration = duration;
        _rootVisual.StartAnimation("Opacity", fade);
        _rootVisual.StartAnimation("Offset", slide);
    }

    /// <summary>Logs latency percentiles (debug builds call this on exit).</summary>
    internal void LogLatency() => Log.Info(RenderLatency.Describe());
}
