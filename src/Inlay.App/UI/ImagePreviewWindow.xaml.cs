using Inlay.Platform;
using Inlay.Platform.Engine;
using Inlay.Platform.Native;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Inlay.App.UI;

/// <summary>
/// A larger preview of the selected image row, beside Quick Insert. Like Quick Insert it is created once at startup,
/// never activates and stays DWM-cloaked between uses; Quick Insert never resizes for it. The image is decoded at
/// the preview's display size only, and a GIF animates only while the preview is visible. The window is placed and
/// laid out while cloaked and uncloaked only after the first frame rendered with the decoded image in it (the same
/// rule as Quick Insert), so it never appears empty and then fills in.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The backdrop controller is disposed when the window closes.")]
public sealed partial class ImagePreviewWindow : Window
{
    /// <summary>The longest side of the preview, in effective pixels.</summary>
    public const double MaxSizeEpx = 240;
    private const double PaddingEpx = 8;
    private const double GapEpx = 6;

    /// <summary>Show → first visible frame with the image in it (logged on exit with Quick Insert's).</summary>
    internal LatencyStats ShowLatency { get; } = new("preview shown to first frame");

    private readonly nint _hwnd;
    private readonly SystemBackdropConfiguration _backdropConfig;
    private readonly DesktopAcrylicController _acrylic;
    private string? _path;
    private BitmapImage? _bitmap;
    private bool _shown;
    private bool _opened;
    private int _framesToWait;
    private int _showId;

    /// <summary>
    /// Rendered frames to let pass after the image opened before uncloaking: the first can still be the one composed
    /// before the picture reached the surface (seen as an empty acrylic frame for one refresh).
    /// </summary>
    private const int FramesBeforeUncloak = 2;
    private long _showStart;

    public ImagePreviewWindow()
    {
        InitializeComponent();
        _hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        AppWindow.IsShownInSwitchers = false;
        const int frame = User32.WS_CAPTION | User32.WS_THICKFRAME | User32.WS_SYSMENU | User32.WS_MINIMIZEBOX | User32.WS_MAXIMIZEBOX;
        var style = User32.GetWindowLongPtr(_hwnd, User32.GWL_STYLE);
        User32.SetWindowLongPtr(_hwnd, User32.GWL_STYLE, (style & ~frame) | User32.WS_POPUP);
        var exStyle = User32.GetWindowLongPtr(_hwnd, User32.GWL_EXSTYLE);
        User32.SetWindowLongPtr(_hwnd, User32.GWL_EXSTYLE, (exStyle & ~User32.WS_EX_APPWINDOW) | User32.WS_EX_NOACTIVATE | User32.WS_EX_TOOLWINDOW | User32.WS_EX_TOPMOST | User32.WS_EX_TRANSPARENT);
        User32.SetWindowPos(_hwnd, 0, 0, 0, 0, 0, User32.SWP_FRAMECHANGED | User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
        Dwm.SetInt(_hwnd, Dwm.DWMWA_WINDOW_CORNER_PREFERENCE, Dwm.DWMWCP_ROUND);
        Dwm.SetInt(_hwnd, Dwm.DWMWA_TRANSITIONS_FORCEDISABLED, 1);
        Cloak(true);

        _backdropConfig = new SystemBackdropConfiguration { IsInputActive = true };
        _acrylic = new DesktopAcrylicController();
        _acrylic.AddSystemBackdropTarget(WinRT.CastExtensions.As<ICompositionSupportsSystemBackdrop>(this));
        _acrylic.SetSystemBackdropConfiguration(_backdropConfig);
        Root.ActualThemeChanged += (_, _) => ApplyTheme();
        ApplyTheme();
        Closed += (_, _) => _acrylic.Dispose();

        // Shown once, cloaked and tiny, so the window exists before the first preview.
        User32.SetWindowPos(_hwnd, User32.HWND_TOPMOST, 0, 0, 1, 1, User32.SWP_NOACTIVATE | User32.SWP_SHOWWINDOW);
    }

    private void ApplyTheme()
    {
        _backdropConfig.Theme = Root.ActualTheme == ElementTheme.Light ? SystemBackdropTheme.Light : SystemBackdropTheme.Dark;
        Dwm.SetInt(_hwnd, Dwm.DWMWA_USE_IMMERSIVE_DARK_MODE, Root.ActualTheme == ElementTheme.Dark ? 1 : 0);
    }

    /// <summary>
    /// Shows <paramref name="path"/> beside <paramref name="beside"/> (Quick Insert's window, physical pixels): to its
    /// right, or to its left when there is no room, top-aligned with it and inside the monitor's work area.
    /// </summary>
    internal void Show(string path, RECT beside)
    {
        var monitor = User32.MonitorFromRect(beside, User32.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        User32.GetMonitorInfoW(monitor, ref info);
        var scale = ShCore.GetDpiForMonitor(monitor, ShCore.MDT_EFFECTIVE_DPI, out var dpi, out _) >= 0 ? dpi / 96.0 : 1.0;

        if (!string.Equals(path, _path, StringComparison.OrdinalIgnoreCase))
        {
            _path = path;
            _opened = false;
            // Decode no larger than the preview shows it (in physical pixels): never the full image.
            var bitmap = new BitmapImage { DecodePixelWidth = (int)Math.Round(MaxSizeEpx * scale), DecodePixelType = DecodePixelType.Physical, AutoPlay = false };
            bitmap.ImageOpened += OnImageOpened;
            bitmap.ImageFailed += (_, e) => Log.Warn($"preview: {e.ErrorMessage}"); // stays cloaked: no empty preview
            bitmap.UriSource = new Uri(path);
            _bitmap = bitmap;
            Picture.Source = bitmap;
        }

        _showStart = System.Diagnostics.Stopwatch.GetTimestamp();
        _showId++;

        var size = (int)Math.Round((MaxSizeEpx + (2 * PaddingEpx)) * scale);
        var gap = (int)Math.Round(GapEpx * scale);
        var work = info.rcWork;
        var x = beside.Right + gap;
        if (x + size > work.Right)
            x = beside.Left - gap - size;
        var y = Math.Clamp(beside.Top, work.Top, Math.Max(work.Top, work.Bottom - size));
        User32.SetWindowPos(_hwnd, User32.HWND_TOPMOST, x, y, size, size, User32.SWP_NOACTIVATE | User32.SWP_NOOWNERZORDER);

        _shown = true;
        if (_bitmap is { IsAnimatedBitmap: true, IsPlaying: false })
            _bitmap.Play();
        // Uncloak once the image is decoded and the frame with it has rendered, so neither an empty preview nor
        // the old picture ever shows. A same-image Show (already decoded) only waits for the frame.
        if (_opened)
            AwaitFrame();
    }

    private void OnImageOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, _bitmap))
            return; // a picture that was replaced before it finished decoding
        _opened = true;
        if (!_shown)
            return;
        if (_bitmap is { IsAnimatedBitmap: true } bitmap)
            bitmap.Play();
        AwaitFrame();
    }

    private void AwaitFrame()
    {
        _framesToWait = FramesBeforeUncloak;
        CompositionTarget.Rendered -= OnRendered;
        CompositionTarget.Rendered += OnRendered;
    }

    private void OnRendered(object? sender, RenderedEventArgs e)
    {
        if (_shown && --_framesToWait > 0)
            return;
        CompositionTarget.Rendered -= OnRendered;
        if (!_shown)
            return;
        // Let DWM compose once more (off the UI thread) so the uncloak can't show the frame from before the picture.
        var show = _showId;
        _ = Task.Run(() =>
        {
            _ = Dwm.DwmFlush();
            DispatcherQueue.TryEnqueue(() =>
            {
                if (show != _showId || !_shown)
                    return; // hidden or replaced meanwhile
                Cloak(false);
                ShowLatency.Add(System.Diagnostics.Stopwatch.GetElapsedTime(_showStart).TotalMicroseconds);
            });
        });
    }

    /// <summary>Hides the preview at once and stops any animation.</summary>
    internal void HidePreview()
    {
        if (!_shown)
            return;
        _shown = false;
        _showId++;
        CompositionTarget.Rendered -= OnRendered;
        Cloak(true);
        if (_bitmap is { IsPlaying: true })
            _bitmap.Stop();
    }

    /// <summary>Lets go of the decoded image (Quick Insert closed); the next preview decodes again.</summary>
    internal void Release()
    {
        HidePreview();
        Picture.Source = null;
        _bitmap = null;
        _path = null;
        _opened = false;
    }

    private void Cloak(bool cloak) => Dwm.SetInt(_hwnd, Dwm.DWMWA_CLOAK, cloak ? 1 : 0);
}
