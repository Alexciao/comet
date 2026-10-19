using System.Diagnostics;
using Comet.App.Services;
using Comet.App.UI.Settings.Pages;
using Comet.Core.Settings;
using Comet.Platform;
using Comet.Platform.Native;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;

namespace Comet.App.UI.Settings;

/// <summary>
/// The Settings window, built to pass for a page of Windows 11 Settings: Mica, the WinUI title bar, a NavigationView
/// with the app card in the pane header, and Community Toolkit settings cards. It is created when opened and
/// destroyed when closed, so a tray app that rarely shows Settings doesn't carry its memory around. It appears
/// complete in one frame: cloaked until its first frame with content has rendered, as the popup does.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private const int DefaultWidth = 1040;
    private const int DefaultHeight = 720;
    private const int MinWidth = 640;
    private const int MinHeight = 480;
    private const string WindowTitle = "Comet Settings";

    private readonly AppHost _host;
    private readonly nint _hwnd;
    private readonly long _openedAt;
    private bool _shown;

    internal SettingsWindow(AppHost host, long openedAt)
    {
        _host = host;
        _openedAt = openedAt;
        InitializeComponent();
        _hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        Dwm.SetInt(_hwnd, Dwm.DWMWA_CLOAK, 1);
        Instance = this;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Comet.ico"));
        RootGrid.ActualThemeChanged += (_, _) => ApplyTheme();
        ApplyTheme();

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            var scale = Scale;
            presenter.PreferredMinimumWidth = (int)(MinWidth * scale);
            presenter.PreferredMinimumHeight = (int)(MinHeight * scale);
        }

        PlaceWindow();
        ShowStatus();
        _host.PausedChanged += OnPausedChanged;
        _host.Settings.Changed += OnHostSettingsChanged;
        _host.AppUpdateChanged += OnHostAppUpdateChanged;
        AppWindow.Closing += OnClosing;
        Closed += OnClosed;

        Nav.SelectedItem = ItemFor(_host.Settings.UiState.SettingsPage) ?? EmojiItem;
    }

    internal AppHost Host => _host;

    /// <summary>
    /// The open Settings window, for its pages. Deliberately not passed as the navigation parameter: the frame's
    /// page stack would hold it from native code, a cycle the garbage collector can't see through, and every
    /// closed window would stay in memory.
    /// </summary>
    internal static SettingsWindow? Instance { get; private set; }

    /// <summary>Raised on the UI thread when settings change (from Settings itself, the tray or a hand edit).</summary>
    internal event Action<SettingsParts>? SettingsChanged;

    /// <summary>Raised when Comet is paused or resumed.</summary>
    internal event Action? PausedChanged;

    /// <summary>Raised when the weekly app update check finds (or stops finding) a newer version.</summary>
    internal event Action? AppUpdateChanged;

    private double Scale => User32.GetDpiForWindow(_hwnd) / 96.0;

    /// <summary>Shows the window: the first time once its content has rendered, afterwards at once.</summary>
    internal void BringToFront()
    {
        if (!_shown)
        {
            Activate();
            return;
        }

        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
        User32.SetForegroundWindow(_hwnd);
    }

    /// <summary>Opens a page by its tag ("emoji", "snippets", "autoreplace", "general").</summary>
    internal void Navigate(string tag)
    {
        if (ItemFor(tag) is { } item)
            Nav.SelectedItem = item;
    }

    private NavigationViewItem? ItemFor(string? tag) => tag switch
    {
        "emoji" => EmojiItem,
        "snippets" => SnippetsItem,
        "autoreplace" => AutoReplaceItem,
        "general" => GeneralItem,
        _ => null,
    };

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag })
            return;
        Type page = tag switch
        {
            "snippets" => typeof(SnippetsPage),
            "autoreplace" => typeof(AutoReplacePage),
            "general" => typeof(GeneralPage),
            _ => typeof(EmojiPage),
        };
        if (ContentFrame.CurrentSourcePageType == page)
            return;
        var started = Stopwatch.GetTimestamp();

        ContentFrame.Navigate(page, null, new EntranceNavigationTransitionInfo());
        if (!_shown)
            ContentFrame.Loaded += OnFirstContentLoaded;
        else
            PerfProbe.UntilNextFrame($"page {tag} (navigate {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms)");
        _host.Settings.UiState.SettingsPage = tag;
    }

    // ------------------------------------------------------------------ appearing without a flash

    private void OnFirstContentLoaded(object sender, RoutedEventArgs e)
    {
        ContentFrame.Loaded -= OnFirstContentLoaded;
        CompositionTarget.Rendered += OnFirstFrameRendered;
    }

    private void OnFirstFrameRendered(object? sender, RenderedEventArgs e)
    {
        CompositionTarget.Rendered -= OnFirstFrameRendered;
        // The title bar shows "Comet" (and copies it to the window once loaded); the taskbar, Alt+Tab and UI
        // Automation say which Comet window this is.
        AppWindow.Title = WindowTitle;
        Dwm.SetInt(_hwnd, Dwm.DWMWA_CLOAK, 0);
        _shown = true;
        User32.SetForegroundWindow(_hwnd);
        Log.Info($"settings shown in {Stopwatch.GetElapsedTime(_openedAt).TotalMilliseconds:F0} ms");
    }

    private void ApplyTheme()
    {
        var dark = RootGrid.ActualTheme == ElementTheme.Dark;
        // Caption buttons and the frame follow the content's theme (it can differ from the system's app mode).
        AppWindow.TitleBar.PreferredTheme = dark ? TitleBarTheme.Dark : TitleBarTheme.Light;
        Dwm.SetInt(_hwnd, Dwm.DWMWA_USE_IMMERSIVE_DARK_MODE, dark ? 1 : 0);
    }

    private void OnDisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args) =>
        AppTitleBar.IsPaneToggleButtonVisible = args.DisplayMode != NavigationViewDisplayMode.Expanded;

    private void OnPaneToggleRequested(TitleBar sender, object args) => Nav.IsPaneOpen = !Nav.IsPaneOpen;

    // ------------------------------------------------------------------ status line in the pane header

    private void OnPausedChanged(bool paused)
    {
        ShowStatus();
        PausedChanged?.Invoke();
    }

    private void OnHostSettingsChanged(SettingsParts parts)
    {
        ShowStatus();
        SettingsChanged?.Invoke(parts);
    }

    private void OnHostAppUpdateChanged() => AppUpdateChanged?.Invoke();

    private void ShowStatus() => StatusText.Text = $"{_host.Status()} · v{AppPaths.Version}";

    // ------------------------------------------------------------------ placement

    /// <summary>Restores the last size and position, kept on a monitor that is still there.</summary>
    private void PlaceWindow()
    {
        var state = _host.Settings.UiState;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        RectInt32 rect;
        if (state.SettingsWindow is { Width: > 0, Height: > 0 } saved
            && DisplayArea.GetFromPoint(new PointInt32(saved.X + 40, saved.Y + 20), DisplayAreaFallback.None) is { } savedArea)
        {
            // Position in physical pixels, size in DIPs (scaled by the monitor the window lands on).
            area = savedArea;
            var scale = MonitorScale(area);
            rect = new RectInt32(saved.X, saved.Y, (int)(saved.Width * scale), (int)(saved.Height * scale));
        }
        else
        {
            var scale = MonitorScale(area);
            var work = area.WorkArea;
            var w = (int)(DefaultWidth * scale);
            var h = (int)(DefaultHeight * scale);
            rect = new RectInt32(work.X + ((work.Width - w) / 2), work.Y + ((work.Height - h) / 2), w, h);
        }

        AppWindow.MoveAndResize(Clamp(rect, area.WorkArea));
        if (state.SettingsMaximized && AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Maximize();
    }

    /// <summary>Fits the window inside the work area, moving it rather than shrinking it where possible.</summary>
    internal static RectInt32 Clamp(RectInt32 r, RectInt32 work)
    {
        var w = Math.Min(r.Width, work.Width);
        var h = Math.Min(r.Height, work.Height);
        var x = Math.Clamp(r.X, work.X, work.X + work.Width - w);
        var y = Math.Clamp(r.Y, work.Y, work.Y + work.Height - h);
        return new RectInt32(x, y, w, h);
    }

    private static double MonitorScale(DisplayArea area)
    {
        var monitor = User32.MonitorFromPoint(new POINT { X = area.WorkArea.X + 1, Y = area.WorkArea.Y + 1 }, User32.MONITOR_DEFAULTTONEAREST);
        return ShCore.GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
    }

    private void SavePlacement()
    {
        var state = _host.Settings.UiState;
        var presenter = AppWindow.Presenter as OverlappedPresenter;
        state.SettingsMaximized = presenter?.State == OverlappedPresenterState.Maximized;
        if (presenter?.State == OverlappedPresenterState.Restored)
        {
            var scale = Scale;
            state.SettingsWindow = new WindowBounds
            {
                X = AppWindow.Position.X,
                Y = AppWindow.Position.Y,
                Width = (int)Math.Round(AppWindow.Size.Width / scale),
                Height = (int)Math.Round(AppWindow.Size.Height / scale),
            };
        }

        _host.Settings.SaveUiState();
    }

    // ------------------------------------------------------------------ closing: destroyed, not hidden

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args) => SavePlacement();

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (Instance == this)
            Instance = null;
        // Window and AppWindow aren't part of XAML's reference tracking: a handler on them that points back at this
        // window is a cycle through native code the garbage collector can't break, so every closed window stayed.
        Closed -= OnClosed;
        AppWindow.Closing -= OnClosing;
        _host.PausedChanged -= OnPausedChanged;
        _host.Settings.Changed -= OnHostSettingsChanged;
        _host.AppUpdateChanged -= OnHostAppUpdateChanged;
        CompositionTarget.Rendered -= OnFirstFrameRendered;
        // WinUI never releases a closed Window (even an empty one stays referenced from native code), so cut the
        // content loose: the pages, their cached lists and the backdrop can then go, and only the empty shell stays.
        ContentFrame.BackStack.Clear();
        ContentFrame.Content = null;
        SetTitleBar(null);
        SystemBackdrop = null;
        Content = null;
        _host.OnSettingsClosed(this);
    }
}
