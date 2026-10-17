using Comet.App.Services;
using Comet.App.UI.Controls;
using Comet.App.UI.Dialogs;
using Comet.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Comet.App.UI.Pages;

public sealed partial class AutoReplacePage : UserControl
{
    private const string ReplaceGlyph = "";

    private readonly AppHost _host;
    private readonly Window _owner;
    private (AutoReplaceDefinition Item, int Index)? _deleted;
    private bool _loading;
    private bool _dialogOpen;

    internal AutoReplacePage(AppHost host, Window owner)
    {
        _host = host;
        _owner = owner;
        InitializeComponent();
    }

    private List<AutoReplaceDefinition> Entries => _host.Settings.AutoReplace;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _host.Settings.Changed += OnSettingsChanged;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _host.Settings.Changed -= OnSettingsChanged;

    private void OnSettingsChanged(SettingsParts parts)
    {
        if (parts.HasFlag(SettingsParts.AutoReplace) || parts.HasFlag(SettingsParts.Settings))
            Refresh();
    }

    private void Refresh()
    {
        _loading = true;
        FeatureToggle.IsOn = _host.Settings.Settings.Features.AutoReplace;
        _loading = false;

        ListPanel.Children.Clear();
        var query = SearchBox.Text.Trim();
        var shown = 0;
        foreach (var entry in Entries.OrderBy(e => e.Abbreviation, StringComparer.CurrentCultureIgnoreCase))
        {
            if (!PageHelpers.Matches(query, entry.Abbreviation, entry.Replacement))
                continue;
            var item = entry;
            var row = new EntryRow();
            row.Show(
                ReplaceGlyph,
                $"{item.Abbreviation}  →  {PageHelpers.OneLine(item.Replacement)}",
                item.MatchCase ? "Matches case exactly" : "",
                warning: null,
                enabled: item.Enabled);
            row.EnabledChanged += on =>
            {
                item.Enabled = on;
                _host.Settings.SaveAutoReplace();
            };
            row.EditRequested += () => _ = Edit(item);
            row.DuplicateRequested += () => Duplicate(item);
            row.DeleteRequested += () => Delete(item);
            ListPanel.Children.Add(row);
            shown++;
        }

        var any = Entries.Count > 0;
        CountText.Text = any ? (Entries.Count == 1 ? "1 replacement" : $"{Entries.Count} replacements") : "";
        EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        NoMatches.Visibility = any && shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        AddButton.Visibility = any ? Visibility.Visible : Visibility.Collapsed; // the empty state has its own
        ExportItem.IsEnabled = any;
    }

    private void OnFeatureToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        _host.Settings.Settings.Features.AutoReplace = FeatureToggle.IsOn;
        _host.Settings.SaveSettings();
    }

    private void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => Refresh();

    private void OnAdd(object sender, RoutedEventArgs e) => _ = Edit(null);

    private async Task Edit(AutoReplaceDefinition? existing)
    {
        // One dialog at a time (a second ShowAsync while one is open throws).
        if (_dialogOpen || XamlRoot is null)
            return;
        _dialogOpen = true;
        try
        {
            var others = Entries.Where(x => x != existing).Select(x => x.Abbreviation).ToList();
            var dialog = new AutoReplaceDialog(existing, others) { XamlRoot = XamlRoot };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            if (existing is null)
            {
                Entries.Add(new AutoReplaceDefinition { Abbreviation = dialog.Abbreviation, Replacement = dialog.Replacement, MatchCase = dialog.MatchCase });
                ShowStatus(InfoBarSeverity.Success, $"Added {dialog.Abbreviation} → {PageHelpers.OneLine(dialog.Replacement)}.", canUndo: false);
            }
            else
            {
                existing.Abbreviation = dialog.Abbreviation;
                existing.Replacement = dialog.Replacement;
                existing.MatchCase = dialog.MatchCase;
            }

            _host.Settings.SaveAutoReplace();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private void Duplicate(AutoReplaceDefinition item)
    {
        var abbreviation = item.Abbreviation + "2";
        for (var n = 3; Entries.Any(e => string.Equals(e.Abbreviation, abbreviation, StringComparison.OrdinalIgnoreCase)); n++)
            abbreviation = item.Abbreviation + n;
        Entries.Add(new AutoReplaceDefinition { Abbreviation = abbreviation, Replacement = item.Replacement, MatchCase = item.MatchCase, Enabled = item.Enabled });
        _host.Settings.SaveAutoReplace();
    }

    private void Delete(AutoReplaceDefinition item)
    {
        var index = Entries.IndexOf(item);
        if (index < 0)
            return;
        Entries.RemoveAt(index);
        _deleted = (item, index);
        _host.Settings.SaveAutoReplace();
        ShowStatus(InfoBarSeverity.Informational, $"Deleted {item.Abbreviation}.", canUndo: true);
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        if (_deleted is { } d)
        {
            Entries.Insert(Math.Min(d.Index, Entries.Count), d.Item);
            _deleted = null;
            _host.Settings.SaveAutoReplace();
        }

        StatusBar.IsOpen = false;
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = await PageHelpers.PickOpenAsync(_owner);
            if (path is null)
                return;
            var (added, replaced) = ImportExport.ImportAutoReplace(await File.ReadAllTextAsync(path), Entries);
            _host.Settings.SaveAutoReplace();
            ShowStatus(InfoBarSeverity.Success, $"Imported {added} new and {replaced} updated replacements.", canUndo: false);
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            ShowStatus(InfoBarSeverity.Error, $"Couldn't import: {ex.Message}", canUndo: false);
        }
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = await PageHelpers.PickSaveAsync(_owner, "Comet auto-replace");
            if (path is null)
                return;
            await File.WriteAllTextAsync(path, ImportExport.ExportAutoReplace(Entries));
            ShowStatus(InfoBarSeverity.Success, $"Exported {Entries.Count} replacements to {Path.GetFileName(path)}.", canUndo: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            ShowStatus(InfoBarSeverity.Error, $"Couldn't export: {ex.Message}", canUndo: false);
        }
    }

    private void ShowStatus(InfoBarSeverity severity, string message, bool canUndo)
    {
        StatusBar.Severity = severity;
        StatusBar.Message = message;
        UndoButton.Visibility = canUndo ? Visibility.Visible : Visibility.Collapsed;
        StatusBar.IsOpen = true;
    }
}
