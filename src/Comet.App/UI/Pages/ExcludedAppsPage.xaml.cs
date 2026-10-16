using System.Diagnostics;
using Comet.App.Services;
using Comet.App.UI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Comet.App.UI.Pages;

public sealed partial class ExcludedAppsPage : UserControl
{
    private readonly AppHost _host;

    internal ExcludedAppsPage(AppHost host)
    {
        _host = host;
        InitializeComponent();
        Refresh();
        _host.Settings.Changed += OnSettingsChanged;
        Unloaded += (_, _) => _host.Settings.Changed -= OnSettingsChanged;
    }

    private void OnSettingsChanged(SettingsParts parts)
    {
        if (parts.HasFlag(SettingsParts.Settings))
            Refresh();
    }

    private void Refresh()
    {
        Fill(PickerList, _host.Settings.Settings.PickerExcludedApps, "Emoji and snippets work everywhere.");
        Fill(AutoReplaceList, _host.Settings.Settings.AutoReplaceExcludedApps, "Auto-replace works everywhere.");
    }

    private void Fill(StackPanel panel, List<string> apps, string emptyText)
    {
        panel.Children.Clear();
        if (apps.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = emptyText, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], Margin = new Thickness(1, 0, 0, 4) });
            return;
        }

        foreach (var app in apps.OrderBy(a => a, StringComparer.OrdinalIgnoreCase))
        {
            var remove = new Button { Content = "Remove" };
            AutomationProperties.SetName(remove, $"Remove {app}");
            var name = app;
            remove.Click += (_, _) =>
            {
                apps.Remove(name);
                _host.Settings.SaveSettings();
            };
            panel.Children.Add(new SettingsCard { Header = name, Glyph = "", Content = remove, MinHeight = 56 });
        }
    }

    /// <summary>Lists the apps that have a window open right now.</summary>
    private void OnAppBoxOpened(object? sender, object e)
    {
        if (sender is not ComboBox box)
            return;
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle != 0 && p.Id != Environment.ProcessId)
                    names.Add(p.ProcessName.ToLowerInvariant() + ".exe");
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                p.Dispose();
            }
        }

        box.ItemsSource = names.ToList();
    }

    private void OnAddPicker(object sender, RoutedEventArgs e) => Add(PickerAppBox, _host.Settings.Settings.PickerExcludedApps);

    private void OnAddAutoReplace(object sender, RoutedEventArgs e) => Add(AutoReplaceAppBox, _host.Settings.Settings.AutoReplaceExcludedApps);

    private void Add(ComboBox box, List<string> apps)
    {
        var name = (box.SelectedItem as string ?? box.Text ?? "").Trim().ToLowerInvariant();
        if (name.Length == 0)
            return;
        if (!name.EndsWith(".exe", StringComparison.Ordinal))
            name += ".exe";
        if (!apps.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            apps.Add(name);
            _host.Settings.SaveSettings();
        }

        box.Text = "";
        box.SelectedItem = null;
    }
}
