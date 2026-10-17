using Comet.App.Services;
using Comet.App.UI.Controls;
using Comet.Platform.Context;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Comet.App.UI.Pages;

public sealed partial class ExcludedAppsPage : UserControl
{
    private readonly AppHost _host;
    private List<string> _running = [];

    internal ExcludedAppsPage(AppHost host)
    {
        _host = host;
        InitializeComponent();
    }

    private List<string> PickerApps => _host.Settings.Settings.PickerExcludedApps;

    private List<string> AutoReplaceApps => _host.Settings.Settings.AutoReplaceExcludedApps;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _host.Settings.Changed += OnSettingsChanged;
        Refresh();
        LoadRunningApps();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _host.Settings.Changed -= OnSettingsChanged;

    private void OnSettingsChanged(SettingsParts parts)
    {
        if (parts.HasFlag(SettingsParts.Settings))
            Refresh();
    }

    /// <summary>Suggestions for the add boxes: apps with a window open, read in the background.</summary>
    private void LoadRunningApps()
    {
        var dispatcher = DispatcherQueue;
        _ = Task.Run(() =>
        {
            var apps = RunningApps.WithWindows();
            dispatcher.TryEnqueue(() => _running = apps);
        });
    }

    private void Refresh()
    {
        Fill(PickerList, PickerSummary, PickerApps, "The : list opens in every app");
        Fill(AutoReplaceList, AutoReplaceSummary, AutoReplaceApps, "Abbreviations expand in every app");
    }

    private void Fill(StackPanel list, TextBlock summary, List<string> apps, string emptyText)
    {
        list.Children.Clear();
        summary.Text = apps.Count switch
        {
            0 => emptyText,
            1 => "Off in 1 app",
            _ => $"Off in {apps.Count} apps",
        };
        foreach (var app in apps.OrderBy(a => a, StringComparer.OrdinalIgnoreCase))
        {
            var row = new AppRow(app);
            var name = app;
            row.RemoveRequested += () =>
            {
                apps.RemoveAll(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
                _host.Settings.SaveSettings();
            };
            list.Children.Add(row);
        }
    }

    private void OnAppBoxGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is AutoSuggestBox box)
            ShowSuggestions(box);
    }

    private void OnAppBoxTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            ShowSuggestions(sender);
    }

    /// <summary>Open apps whose name contains what was typed, not yet excluded in this list.</summary>
    private void ShowSuggestions(AutoSuggestBox box)
    {
        var excluded = box == PickerAppBox ? PickerApps : AutoReplaceApps;
        var typed = box.Text.Trim();
        box.Items.Clear();
        foreach (var app in _running)
        {
            if (app.Contains(typed, StringComparison.OrdinalIgnoreCase) && !excluded.Contains(app, StringComparer.OrdinalIgnoreCase))
                box.Items.Add(app);
        }

        box.IsSuggestionListOpen = box.Items.Count > 0;
    }

    private void OnPickerQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) =>
        Add(PickerAppBox, PickerApps, args.ChosenSuggestion as string ?? args.QueryText);

    private void OnAutoReplaceQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) =>
        Add(AutoReplaceAppBox, AutoReplaceApps, args.ChosenSuggestion as string ?? args.QueryText);

    private void OnAddPicker(object sender, RoutedEventArgs e) => Add(PickerAppBox, PickerApps, null);

    private void OnAddAutoReplace(object sender, RoutedEventArgs e) => Add(AutoReplaceAppBox, AutoReplaceApps, null);

    private void Add(AutoSuggestBox box, List<string> apps, string? chosen)
    {
        var name = (chosen ?? box.Text).Trim().ToLowerInvariant();
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
        box.Items.Clear();
        box.IsSuggestionListOpen = false;
    }
}
