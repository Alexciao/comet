using Comet.App.UI.Pages;
using Comet.Platform;
using Comet.Platform.Native;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Comet.App.UI;

/// <summary>The Settings window: Mica, a NavigationView and Windows 11 settings cards. Created on demand.</summary>
public sealed partial class SettingsWindow : Window
{
    private const int WidthEpx = 1000;
    private const int HeightEpx = 720;

    private readonly AppHost _host;
    private readonly Dictionary<string, UIElement> _pages = [];

    internal SettingsWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Comet.ico"));
        VersionText.Text = $"Comet {AppPaths.Version}";

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 640;
            presenter.PreferredMinimumHeight = 480;
        }

        SizeAndCenter();
        Show("general");
    }

    private void SizeAndCenter()
    {
        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var scale = User32.GetDpiForWindow(hwnd) / 96.0;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var w = (int)(WidthEpx * scale);
        var h = (int)(HeightEpx * scale);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + ((area.Width - w) / 2), area.Y + ((area.Height - h) / 2), w, h));
    }

    internal void BringToFront()
    {
        Activate();
        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        User32.SetForegroundWindow(hwnd);
    }

    private void OnNavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag })
            Show(tag);
    }

    private void Show(string tag)
    {
        if (!_pages.TryGetValue(tag, out var page))
        {
            page = tag switch
            {
                "snippets" => new SnippetsPage(_host, this),
                "autoreplace" => new AutoReplacePage(_host, this),
                "excluded" => new ExcludedAppsPage(_host),
                _ => new GeneralPage(_host),
            };
            _pages[tag] = page;
        }

        PageHost.Content = page;
        PageScroller.ChangeView(null, 0, null, disableAnimation: true);
    }
}
