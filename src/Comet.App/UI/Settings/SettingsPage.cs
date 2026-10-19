using Comet.App.Services;
using Comet.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Comet.App.UI.Settings;

/// <summary>
/// Base of the Settings pages. Pages are cached by the frame (<c>NavigationCacheMode.Required</c>) and live exactly
/// as long as their window, so they listen to the window's events, never to the app's: closing the window lets
/// everything go.
/// </summary>
public partial class SettingsPage : Page
{
    private SettingsWindow? _window;

    internal SettingsWindow OwnerWindow => _window ?? throw new InvalidOperationException("The page hasn't been navigated to.");

    internal AppHost Host => OwnerWindow.Host;

    internal SettingsService Store => Host.Settings;

    internal AppSettings Settings => Host.Settings.Settings;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (_window is not null || SettingsWindow.Instance is not { } window)
            return;
        _window = window;
        window.SettingsChanged += OnSettingsChanged;
        window.PausedChanged += OnPausedChanged;
        window.AppUpdateChanged += OnAppUpdateChanged;
        OnAttached();
    }

    /// <summary>The first navigation to the page: show the current settings.</summary>
    protected virtual void OnAttached()
    {
    }

    /// <summary>Settings changed (in Settings, from the tray, or by hand-editing a file).</summary>
    private protected virtual void OnSettingsChanged(SettingsParts parts)
    {
    }

    protected virtual void OnPausedChanged()
    {
    }

    protected virtual void OnAppUpdateChanged()
    {
    }

    /// <summary>
    /// Runs <paramref name="action"/> once the page's first frame has rendered. A page's first visit shows what is
    /// on screen in its first frame and builds the rest (list cards, sections below the fold) right after, while
    /// the entrance transition is still playing.
    /// </summary>
    protected void AfterFirstFrame(Action action)
    {
        void OnRendered(object? sender, RenderedEventArgs e)
        {
            CompositionTarget.Rendered -= OnRendered;
            action();
        }

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            CompositionTarget.Rendered += OnRendered;
        }

        Loaded += OnLoaded;
    }

    /// <summary>Realizes an <c>x:Load="False"</c> element the first time it is needed, then just toggles it.</summary>
    protected void ShowDeferred<T>(ref T? element, string name, bool visible)
        where T : UIElement
    {
        if (element is null)
        {
            if (!visible)
                return;
            element = (T)FindName(name);
        }

        element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    protected static void AddMenuItem(MenuFlyout menu, string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }
}
