using System.Collections.ObjectModel;
using System.Globalization;
using Inlay.App.Services;
using Inlay.App.UI.Settings.Dialogs;
using Inlay.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Inlay.App.UI.Settings.Pages;

/// <summary>
/// Settings › Shorthands: the same layout as Snippets, with a switch on each entry, and the apps where
/// abbreviations aren't expanded below the list.
/// </summary>
public sealed partial class ShorthandsPage : SettingsPage, ICardHost<ShorthandCardItem>
{
    private readonly Dictionary<ShorthandDefinition, ShorthandCardItem> _cache = new(ReferenceEqualityComparer.Instance);
    private readonly MenuFlyout _itemMenu = new();
    private ShorthandCardItem? _menuItem;
    private (ShorthandDefinition Item, int Index)? _deleted;
    private bool _loading;
    private bool _dialogOpen;

    public ShorthandsPage()
    {
        InitializeComponent();
        AddMenuItem(_itemMenu, "Edit", "", () => _ = EditAsync(_menuItem?.Definition));
        AddMenuItem(_itemMenu, "Duplicate", "", () => Duplicate(_menuItem?.Definition));
        AddMenuItem(_itemMenu, "Delete", "", () => Delete(_menuItem?.Definition));
        AddMenuItem(_itemMenu, "Export…", "", () => _ = ExportAsync(_menuItem?.Definition));
    }

    public ObservableCollection<ShorthandCardItem> Items { get; } = [];

    private List<ShorthandDefinition> Entries => Store.Shorthands;


    protected override void OnAttached()
    {
        PerfProbe.TrackScrolling(Scroller, "shorthands");
        FeatureToggle.IsOn = Settings.Features.Shorthands;
        AfterFirstFrame(() =>
        {
            Refresh();
            ExcludedApps.Attach(Store, () => Settings.ShorthandsExcludedApps);
        });
    }

    private protected override void OnSettingsChanged(SettingsParts parts)
    {
        if (parts.HasFlag(SettingsParts.Shorthands) || parts.HasFlag(SettingsParts.Settings))
            Refresh();
        if (parts.HasFlag(SettingsParts.Settings))
            ExcludedApps.Refresh();
    }

    private void Refresh()
    {
        _loading = true;
        FeatureToggle.IsOn = Settings.Features.Shorthands;
        _loading = false;

        var query = SearchBox.Text.Trim();
        var shown = new List<ShorthandCardItem>(Entries.Count);
        foreach (var entry in Entries.OrderBy(e => e.Shorthand, StringComparer.CurrentCultureIgnoreCase))
        {
            if (query.Length > 0
                && !entry.Shorthand.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                && !entry.Expansion.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                continue;
            if (!_cache.TryGetValue(entry, out var item) || !item.IsCurrent)
                _cache[entry] = item = new ShorthandCardItem(entry, this);
            shown.Add(item);
        }

        if (_cache.Count > Entries.Count)
        {
            var live = new HashSet<ShorthandDefinition>(Entries, ReferenceEqualityComparer.Instance);
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
        if (_loading || Settings.Features.Shorthands == FeatureToggle.IsOn)
            return;
        Settings.Features.Shorthands = FeatureToggle.IsOn;
        Store.SaveSettings();
    }


    private void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        PerfProbe.UntilNextFrame($"filter {Items.Count} -> ");
        Refresh();
    }

    private void OnAdd(object sender, RoutedEventArgs e) => _ = EditAsync(null);

    void ICardHost<ShorthandCardItem>.Open(ShorthandCardItem item) => _ = EditAsync(item.Definition);

    void ICardHost<ShorthandCardItem>.ShowMenu(ShorthandCardItem item, FrameworkElement anchor)
    {
        _menuItem = item;
        _itemMenu.ShowAt(anchor);
    }

    void ICardHost<ShorthandCardItem>.EnabledChanged(ShorthandCardItem item) => Store.SaveShorthands();

    private async Task EditAsync(ShorthandDefinition? existing)
    {
        if (_dialogOpen || XamlRoot is null)
            return;
        _dialogOpen = true;
        try
        {
            var others = Entries.Where(r => r != existing).Select(r => r.Shorthand).ToList();
            var dialog = new ShorthandDialog(existing, others) { XamlRoot = XamlRoot };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            // The list may have been reloaded (a hand edit, an import) while the dialog was open: edit what is there now.
            var target = existing is null ? null : Entries.Find(r => r.Id == existing.Id);
            if (target is null)
            {
                target = new ShorthandDefinition { Shorthand = dialog.Shorthand, Expansion = dialog.Expansion, MatchCase = dialog.MatchCase };
                Entries.Add(target);
                if (existing is null)
                    ShowStatus(InfoBarSeverity.Success, $"Added {dialog.Shorthand}. It expands when you finish the word.", canUndo: false);
            }
            else
            {
                target.Shorthand = dialog.Shorthand;
                target.Expansion = dialog.Expansion;
                target.MatchCase = dialog.MatchCase;
            }

            Store.SaveShorthands();
            var index = Items.ToList().FindIndex(i => ReferenceEquals(i.Definition, target));
            if (index >= 0 && List.GetOrCreateElement(index) is Control card)
                card.Focus(FocusState.Programmatic);
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private void Duplicate(ShorthandDefinition? item)
    {
        if (item is null)
            return;
        var abbreviation = item.Shorthand + "2";
        for (var n = 3; Entries.Any(r => string.Equals(r.Shorthand, abbreviation, StringComparison.OrdinalIgnoreCase)); n++)
            abbreviation = item.Shorthand + n.ToString(CultureInfo.InvariantCulture);
        Entries.Add(new ShorthandDefinition { Shorthand = abbreviation, Expansion = item.Expansion, MatchCase = item.MatchCase, Enabled = item.Enabled });
        Store.SaveShorthands();
    }

    private void Delete(ShorthandDefinition? item)
    {
        var index = item is null ? -1 : Entries.IndexOf(item);
        if (index < 0)
            return;
        Entries.RemoveAt(index);
        _deleted = (item!, index);
        Store.SaveShorthands();
        ShowStatus(InfoBarSeverity.Informational, $"Deleted {item!.Shorthand}", canUndo: true);
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        if (_deleted is { } d)
        {
            _deleted = null;
            if (Entries.Any(r => string.Equals(r.Shorthand, d.Item.Shorthand, StringComparison.OrdinalIgnoreCase)))
            {
                ShowStatus(InfoBarSeverity.Warning, $"{d.Item.Shorthand} is already in the list, so the deleted entry wasn't restored.", canUndo: false);
                return;
            }

            Entries.Insert(Math.Min(d.Index, Entries.Count), d.Item);
            Store.SaveShorthands();
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
            var (added, replaced) = ImportExport.ImportShorthands(json, Entries);
            Store.SaveShorthands();
            ShowStatus(InfoBarSeverity.Success, $"Imported {added} new and {replaced} updated shorthands", canUndo: false);
        }
        catch (Exception ex) when (FilePickers.IsFileError(ex) || ex is FormatException)
        {
            ShowStatus(InfoBarSeverity.Error, $"Couldn't import the file. {ex.Message}", canUndo: false);
        }
    }

    private void OnExportAll(object sender, RoutedEventArgs e) => _ = ExportAsync(null);

    private async Task ExportAsync(ShorthandDefinition? one)
    {
        List<ShorthandDefinition> entries = one is null ? [.. Entries] : [one];
        try
        {
            var name = await FilePickers.SaveJsonAsync(OwnerWindow, one is null ? "Inlay replacements" : $"Expansion {one.Shorthand}", ImportExport.ExportShorthands(entries)).ConfigureAwait(true);
            if (name is not null)
                ShowStatus(InfoBarSeverity.Success, entries.Count == 1 ? $"Exported 1 shorthand to {name}" : $"Exported {entries.Count} shorthands to {name}", canUndo: false);
        }
        catch (Exception ex) when (FilePickers.IsFileError(ex))
        {
            ShowStatus(InfoBarSeverity.Error, $"Couldn't export the file. {ex.Message}", canUndo: false);
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
