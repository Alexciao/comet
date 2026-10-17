using Comet.App.UI.Pages;
using Comet.Platform;
using Comet.Platform.Native;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Comet.App.UI;

/// <summary>
/// The Settings window: Mica, a NavigationView and Windows 11 settings cards. Created on first use; closing hides it,
/// so opening it again is instant and pages keep their state.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private const int WidthEpx = 1060;
    private const int HeightEpx = 760;

    private readonly AppHost _host;
    private readonly Dictionary<string, UIElement> _pages = [];
    private bool _closing;

    internal SettingsWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        // Caption buttons follow the content's theme (it can differ from the system's app mode).
        RootGrid.ActualThemeChanged += (_, _) => ApplyCaptionTheme();
        ApplyCaptionTheme();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Comet.ico"));
        AppWindow.Closing += OnClosing;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 760;
            presenter.PreferredMinimumHeight = 520;
        }

        SizeAndCenter();
        ShowPaused(_host.Paused);
        _host.PausedChanged += ShowPaused;
        Nav.SelectedItem = GeneralItem;
    }

    private void ApplyCaptionTheme() =>
        AppWindow.TitleBar.PreferredTheme = RootGrid.ActualTheme == ElementTheme.Light ? TitleBarTheme.Light : TitleBarTheme.Dark;

    private void SizeAndCenter()
    {
        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var scale = User32.GetDpiForWindow(hwnd) / 96.0;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var w = Math.Min((int)(WidthEpx * scale), area.Width);
        var h = Math.Min((int)(HeightEpx * scale), area.Height);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + ((area.Width - w) / 2), area.Y + ((area.Height - h) / 2), w, h));
    }

    /// <summary>Closing only hides the window; it is really closed when Comet quits.</summary>
    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closing)
            return;
        args.Cancel = true;
        sender.Hide();
    }

    /// <summary>Closes the window for good (Comet is quitting).</summary>
    internal void CloseForGood()
    {
        _closing = true;
        _host.PausedChanged -= ShowPaused;
        Close();
    }

    internal void BringToFront()
    {
        AppWindow.Show();
        Activate();
        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        User32.SetForegroundWindow(hwnd);
    }

    private void ShowPaused(bool paused)
    {
        StatusText.Text = paused ? "Paused" : "Active";
        ActiveDot.Visibility = paused ? Visibility.Collapsed : Visibility.Visible;
        PausedDot.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnNavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
            Show(tag, item.Content as string ?? "");
    }

    /// <summary>Opens a page by its tag (the General page links to the others).</summary>
    internal void Navigate(string tag)
    {
        foreach (var item in Nav.MenuItems.Concat(Nav.FooterMenuItems))
        {
            if (item is NavigationViewItem nav && nav.Tag is string t && t == tag)
            {
                Nav.SelectedItem = nav;
                return;
            }
        }
    }

    private void Show(string tag, string title)
    {
        if (!_pages.TryGetValue(tag, out var page))
        {
            try
            {
                page = tag switch
                {
                    "snippets" => new SnippetsPage(_host, this),
                    "autoreplace" => new AutoReplacePage(_host, this),
                    "excluded" => new ExcludedAppsPage(_host),
                    "about" => new AboutPage(_host),
                    _ => new GeneralPage(_host, this),
                };
            }
            catch (Exception ex)
            {
                // A page that can't be built must not take Comet down with it: say so on the page instead.
                Log.WriteCrash(ex, $"settings page {tag}");
                page = new TextBlock { Text = "This page couldn't be opened. Details are in Comet's log folder.", TextWrapping = TextWrapping.Wrap };
            }

            _pages[tag] = page;
        }

        PageTitle.Text = title;
        PageHost.Content = page;
        PageScroller.ChangeView(null, 0, null, disableAnimation: true);
    }
}
