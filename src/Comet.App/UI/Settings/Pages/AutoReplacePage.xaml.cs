using System.Collections.ObjectModel;
using System.Globalization;
using Comet.App.Services;
using Comet.App.UI.Settings.Dialogs;
using Comet.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Comet.App.UI.Settings.Pages;

/// <summary>
/// Settings › Auto-replace: the same layout as Snippets, with a switch on each entry, and the apps where
/// abbreviations aren't expanded below the list.
/// </summary>
public sealed partial class AutoReplacePage : SettingsPage
{
    private readonly Dictionary<AutoReplaceDefinition, ReplacementItem> _cache = new(ReferenceEqualityComparer.Instance);
    private readonly MenuFlyout _itemMenu = new();
    private ReplacementItem? _menuItem;
    private (AutoReplaceDefinition Item, int Index)? _deleted;
    private bool _loading;
    private bool _dialogOpen;

    public AutoReplacePage()
    {
        InitializeComponent();
        AddMenuItem(_itemMenu, "Edit", "", () => _ = EditAsync(_menuItem?.Definition));
        AddMenuItem(_itemMenu, "Duplicate", "", () => Duplicate(_menuItem?.Definition));
        AddMenuItem(_itemMenu, "Delete", "", () => Delete(_menuItem?.Definition));
        AddMenuItem(_itemMenu, "Export…", "", () => _ = ExportAsync(_menuItem?.Definition));
    }

    public ObservableCollection<ReplacementItem> Items { get; } = [];

    private List<AutoReplaceDefinition> Entries => Store.AutoReplace;


    protected override void OnAttached()
    {
        PerfProbe.TrackScrolling(Scroller, "auto-replace");
        FeatureToggle.IsOn = Settings.Features.AutoReplace;
        AfterFirstFrame(() =>
        {
            Refresh();
            ExcludedApps.Attach(Store, () => Settings.AutoReplaceExcludedApps);
        });
    }

    private protected override void OnSettingsChanged(SettingsParts parts)
    {
        if (parts.HasFlag(SettingsParts.AutoReplace) || parts.HasFlag(SettingsParts.Settings))
            Refresh();
        if (parts.HasFlag(SettingsParts.Settings))
            ExcludedApps.Refresh();
    }

    private void Refresh()
    {
        _loading = true;
        FeatureToggle.IsOn = Settings.Features.AutoReplace;
        _loading = false;

        var query = SearchBox.Text.Trim();
        var shown = new List<ReplacementItem>(Entries.Count);
        foreach (var entry in Entries.OrderBy(e => e.Abbreviation, StringComparer.CurrentCultureIgnoreCase))
        {
            if (query.Length > 0
                && !entry.Abbreviation.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                && !entry.Replacement.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                continue;
            if (!_cache.TryGetValue(entry, out var item) || !item.IsCurrent)
                _cache[entry] = item = new ReplacementItem(entry);
            shown.Add(item);
        }

        if (_cache.Count > Entries.Count)
        {
            var live = new HashSet<AutoReplaceDefinition>(Entries, ReferenceEqualityComparer.Instance);
            foreach (var gone in _cache.Keys.Where(k => !live.Contains(k)).ToList())
                _cache.Remove(gone);
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var changes = Items.SyncTo(shown);
        if (PerfProbe.Enabled)
            PerfProbe.Note($"sync {changes} changes in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms");
        ShowCounts(shown.Count, query.Length > 0);
    }

    private void ShowCounts(int shown, bool searching)
    {
        var total = Entries.Count;
        var any = total > 0;
        CountText.Text = !any ? "" : searching ? $"{shown.ToString(CultureInfo.CurrentCulture)} of {Count(total)}" : Count(total);
        SearchBox.Visibility = CountText.Visibility = AddCard.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        ExportAllItem.IsEnabled = any;
        ShowDeferred(ref EmptyState, nameof(EmptyState), !any);
        ShowDeferred(ref NoMatches, nameof(NoMatches), any && shown == 0);
    }

    private static string Count(int n) => n == 1 ? "1 replacement" : $"{n.ToString(CultureInfo.CurrentCulture)} replacements";


    private void OnFeatureToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || Settings.Features.AutoReplace == FeatureToggle.IsOn)
            return;
        Settings.Features.AutoReplace = FeatureToggle.IsOn;
        Store.SaveSettings();
    }

    private void OnEntryToggled(object sender, RoutedEventArgs e)
    {
        // Also raised when a recycled card is bound to another entry: only a real change is saved.
        if (sender is ToggleSwitch { DataContext: ReplacementItem item } toggle && item.SetEnabled(toggle.IsOn))
            Store.SaveAutoReplace();
    }

    private void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        PerfProbe.UntilNextFrame($"filter {Items.Count} -> ");
        Refresh();
    }

    private void OnAdd(object sender, RoutedEventArgs e) => _ = EditAsync(null);

    private void OnCardClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ReplacementItem item)
            _ = EditAsync(item.Definition);
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button || button.DataContext is not ReplacementItem item)
            return;
        _menuItem = item;
        _itemMenu.ShowAt(button);
    }

    private async Task EditAsync(AutoReplaceDefinition? existing)
    {
        if (_dialogOpen || XamlRoot is null)
            return;
        _dialogOpen = true;
        try
        {
            var others = Entries.Where(r => r != existing).Select(r => r.Abbreviation).ToList();
            var dialog = new AutoReplaceDialog(existing, others) { XamlRoot = XamlRoot };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            if (existing is null)
            {
                Entries.Add(new AutoReplaceDefinition { Abbreviation = dialog.Abbreviation, Replacement = dialog.Replacement, MatchCase = dialog.MatchCase });
                ShowStatus(InfoBarSeverity.Success, $"Added {dialog.Abbreviation}. It expands when you finish the word.", canUndo: false);
            }
            else
            {
                existing.Abbreviation = dialog.Abbreviation;
                existing.Replacement = dialog.Replacement;
                existing.MatchCase = dialog.MatchCase;
            }

            Store.SaveAutoReplace();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private void Duplicate(AutoReplaceDefinition? item)
    {
        if (item is null)
            return;
        var abbreviation = item.Abbreviation + "2";
        for (var n = 3; Entries.Any(r => string.Equals(r.Abbreviation, abbreviation, StringComparison.OrdinalIgnoreCase)); n++)
            abbreviation = item.Abbreviation + n.ToString(CultureInfo.InvariantCulture);
        Entries.Add(new AutoReplaceDefinition { Abbreviation = abbreviation, Replacement = item.Replacement, MatchCase = item.MatchCase, Enabled = item.Enabled });
        Store.SaveAutoReplace();
    }

    private void Delete(AutoReplaceDefinition? item)
    {
        var index = item is null ? -1 : Entries.IndexOf(item);
        if (index < 0)
            return;
        Entries.RemoveAt(index);
        _deleted = (item!, index);
        Store.SaveAutoReplace();
        ShowStatus(InfoBarSeverity.Informational, $"Deleted {item!.Abbreviation}.", canUndo: true);
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        if (_deleted is { } d)
        {
            Entries.Insert(Math.Min(d.Index, Entries.Count), d.Item);
            _deleted = null;
            Store.SaveAutoReplace();
        }

        if (StatusBar is not null)
            StatusBar.IsOpen = false;
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        try
        {
            var json = await FilePickers.OpenJsonAsync(OwnerWindow).ConfigureAwait(true);
            if (json is null)
                return;
            var (added, replaced) = ImportExport.ImportAutoReplace(json, Entries);
            Store.SaveAutoReplace();
            ShowStatus(InfoBarSeverity.Success, $"Imported {added} new and {replaced} updated replacements.", canUndo: false);
        }
        catch (Exception ex) when (FilePickers.IsFileError(ex) || ex is FormatException)
        {
            ShowStatus(InfoBarSeverity.Error, $"Couldn't import: {ex.Message}", canUndo: false);
        }
    }

    private void OnExportAll(object sender, RoutedEventArgs e) => _ = ExportAsync(null);

    private async Task ExportAsync(AutoReplaceDefinition? one)
    {
        List<AutoReplaceDefinition> entries = one is null ? [.. Entries] : [one];
        try
        {
            var name = await FilePickers.SaveJsonAsync(OwnerWindow, one is null ? "Comet replacements" : $"Replacement {one.Abbreviation}", ImportExport.ExportAutoReplace(entries)).ConfigureAwait(true);
            if (name is not null)
                ShowStatus(InfoBarSeverity.Success, entries.Count == 1 ? $"Exported 1 replacement to {name}." : $"Exported {entries.Count} replacements to {name}.", canUndo: false);
        }
        catch (Exception ex) when (FilePickers.IsFileError(ex))
        {
            ShowStatus(InfoBarSeverity.Error, $"Couldn't export: {ex.Message}", canUndo: false);
        }
    }

    private void ShowStatus(InfoBarSeverity severity, string message, bool canUndo)
    {
        StatusBar ??= (InfoBar)FindName(nameof(StatusBar));
        StatusBar.Severity = severity;
        StatusBar.Message = message;
        UndoButton.Visibility = canUndo ? Visibility.Visible : Visibility.Collapsed;
        StatusBar.IsOpen = true;
    }
}
