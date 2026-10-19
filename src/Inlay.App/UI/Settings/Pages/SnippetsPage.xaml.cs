using System.Collections.ObjectModel;
using System.Globalization;
using Inlay.App.Services;
using Inlay.App.UI.Settings.Dialogs;
using Inlay.Core.Settings;
using Inlay.Core.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Inlay.App.UI.Settings.Pages;

/// <summary>
/// Settings › Snippets, laid out like Windows Settings › Apps › Installed apps: an Add card, a search row with the
/// count and an Import/Export menu, and one card per snippet with its own "…" menu. The list is virtualized and
/// updated item by item, so filtering hundreds of snippets keeps up with typing.
/// </summary>
public sealed partial class SnippetsPage : SettingsPage, ICardHost<SnippetItem>
{
    private readonly Dictionary<SnippetDefinition, SnippetItem> _cache = new(ReferenceEqualityComparer.Instance);
    private readonly MenuFlyout _itemMenu = new();
    private SnippetItem? _menuItem;
    private (SnippetDefinition Item, int Index)? _deleted;
    private bool _loading;
    private bool _dialogOpen;

    public SnippetsPage()
    {
        InitializeComponent();
        AddMenuItem(_itemMenu, "Edit", "", () => _ = EditAsync(_menuItem?.Definition));
        AddMenuItem(_itemMenu, "Duplicate", "", () => Duplicate(_menuItem?.Definition));
        AddMenuItem(_itemMenu, "Delete", "", () => Delete(_menuItem?.Definition));
        AddMenuItem(_itemMenu, "Export…", "", () => _ = ExportAsync(_menuItem?.Definition));
    }

    public ObservableCollection<SnippetItem> Items { get; } = [];

    private List<SnippetDefinition> Snippets => Store.Snippets;


    protected override void OnAttached()
    {
        PerfProbe.TrackScrolling(Scroller, "snippets");
        FeatureToggle.IsOn = Settings.Features.Snippets;
        AfterFirstFrame(Refresh);
    }

    private protected override void OnSettingsChanged(SettingsParts parts)
    {
        if (parts.HasFlag(SettingsParts.Snippets) || parts.HasFlag(SettingsParts.Settings))
            Refresh();
    }

    /// <summary>Shows the current list, filtered by the search box, as incremental changes to <see cref="Items"/>.</summary>
    private void Refresh()
    {
        _loading = true;
        FeatureToggle.IsOn = Settings.Features.Snippets;
        _loading = false;

        var query = SearchBox.Text.Trim();
        var shown = new List<SnippetItem>(Snippets.Count);
        foreach (var snippet in Snippets.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (query.Length > 0
                && !snippet.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                && !snippet.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                continue;
            if (!_cache.TryGetValue(snippet, out var item) || !item.IsCurrent)
                _cache[snippet] = item = new SnippetItem(snippet, this);
            shown.Add(item);
        }

        if (_cache.Count > Snippets.Count)
        {
            var live = new HashSet<SnippetDefinition>(Snippets, ReferenceEqualityComparer.Instance);
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
        var total = Snippets.Count;
        var any = total > 0;
        CountText.Text = !any ? "" : searching ? $"{Count(shown)} of {Count(total, withNoun: true)}" : Count(total, withNoun: true);
        SearchBox.Visibility = CountText.Visibility = AddCard.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        ExportAllItem.IsEnabled = any;
        ShowDeferred(ref EmptyState, nameof(EmptyState), !any);
        ShowDeferred(ref NoMatches, nameof(NoMatches), any && shown == 0);
    }

    private static string Count(int n, bool withNoun = false) =>
        !withNoun ? n.ToString(CultureInfo.CurrentCulture) : n == 1 ? "1 snippet" : $"{n.ToString(CultureInfo.CurrentCulture)} snippets";


    private void OnFeatureToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || Settings.Features.Snippets == FeatureToggle.IsOn)
            return;
        Settings.Features.Snippets = FeatureToggle.IsOn;
        Store.SaveSettings();
    }

    private void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        PerfProbe.UntilNextFrame($"filter {Items.Count} -> ");
        Refresh();
    }

    private void OnAdd(object sender, RoutedEventArgs e) => _ = EditAsync(null);

    /// <summary>
    /// Opens the New snippet dialog with <paramref name="name"/> filled in (chosen from Quick Insert's "Create
    /// snippet" row). Returns the snippet when it was saved, null when the dialog was cancelled.
    /// </summary>
    internal Task<SnippetDefinition?> AddAsync(string name) => EditAsync(null, name);

    void ICardHost<SnippetItem>.Open(SnippetItem item) => _ = EditAsync(item.Definition);

    void ICardHost<SnippetItem>.ShowMenu(SnippetItem item, FrameworkElement anchor)
    {
        _menuItem = item;
        _itemMenu.ShowAt(anchor);
    }

    void ICardHost<SnippetItem>.EnabledChanged(SnippetItem item)
    {
    }

    /// <summary>After an edit the card is a new element: give it the keyboard focus the old one had.</summary>
    private void FocusCard(SnippetDefinition definition)
    {
        var index = Items.ToList().FindIndex(i => ReferenceEquals(i.Definition, definition));
        if (index >= 0 && List.GetOrCreateElement(index) is Control card)
            card.Focus(FocusState.Programmatic);
    }

    private async Task<SnippetDefinition?> EditAsync(SnippetDefinition? existing, string? newName = null)
    {
        // One dialog at a time (a second ShowAsync while one is open throws).
        if (_dialogOpen || XamlRoot is null)
            return null;
        _dialogOpen = true;
        try
        {
            var others = Snippets.Where(s => s != existing).Select(s => s.Name).ToList();
            var dialog = new SnippetDialog(existing, others, Host.IsEmojiShortcode, Settings.TriggerCharacter, newName) { XamlRoot = XamlRoot };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary && !dialog.SavedWithShortcut)
                return null;

            // The list may have been reloaded (a hand edit, an import) while the dialog was open: edit what is there now.
            var target = existing is null ? null : Snippets.Find(s => s.Id == existing.Id);
            if (target is null)
            {
                target = new SnippetDefinition { Name = dialog.SnippetName, Text = dialog.SnippetText };
                Snippets.Add(target);
                if (existing is null)
                    ShowStatus(InfoBarSeverity.Success, $"Added {dialog.SnippetName}. Type {TriggerFormat.Code(Settings.TriggerCharacter, dialog.SnippetName)} in any app.", canUndo: false);
            }
            else
            {
                target.Name = dialog.SnippetName;
                target.Text = dialog.SnippetText;
            }

            Store.SaveSnippets();
            FocusCard(target);
            return target;
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private void Duplicate(SnippetDefinition? item)
    {
        if (item is null)
            return;
        var name = item.Name + "-copy";
        for (var n = 2; Snippets.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)); n++)
            name = $"{item.Name}-copy{n}";
        Snippets.Add(new SnippetDefinition { Name = name, Text = item.Text });
        Store.SaveSnippets();
    }

    private void Delete(SnippetDefinition? item)
    {
        var index = item is null ? -1 : Snippets.IndexOf(item);
        if (index < 0)
            return;
        Snippets.RemoveAt(index);
        _deleted = (item!, index);
        Store.SaveSnippets();
        ShowStatus(InfoBarSeverity.Informational, $"Deleted {item!.Name}.", canUndo: true);
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        if (_deleted is { } d)
        {
            _deleted = null;
            if (Snippets.Any(s => string.Equals(s.Name, d.Item.Name, StringComparison.OrdinalIgnoreCase)))
            {
                ShowStatus(InfoBarSeverity.Warning, $"There's already a snippet named {d.Item.Name}, so the deleted one wasn't restored.", canUndo: false);
                return;
            }

            Snippets.Insert(Math.Min(d.Index, Snippets.Count), d.Item);
            Store.SaveSnippets();
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
            var (added, replaced) = ImportExport.ImportSnippets(json, Snippets);
            Store.SaveSnippets();
            ShowStatus(InfoBarSeverity.Success, $"Imported {added} new and {replaced} updated snippets.", canUndo: false);
        }
        catch (Exception ex) when (FilePickers.IsFileError(ex) || ex is FormatException)
        {
            ShowStatus(InfoBarSeverity.Error, $"Couldn't import: {ex.Message}", canUndo: false);
        }
    }

    private void OnExportAll(object sender, RoutedEventArgs e) => _ = ExportAsync(null);

    /// <summary>Exports one snippet, or all of them.</summary>
    private async Task ExportAsync(SnippetDefinition? one)
    {
        List<SnippetDefinition> snippets = one is null ? [.. Snippets] : [one];
        try
        {
            var name = await FilePickers.SaveJsonAsync(OwnerWindow, one is null ? "Inlay snippets" : $"Snippet {one.Name}", ImportExport.ExportSnippets(snippets)).ConfigureAwait(true);
            if (name is not null)
                ShowStatus(InfoBarSeverity.Success, snippets.Count == 1 ? $"Exported 1 snippet to {name}." : $"Exported {snippets.Count} snippets to {name}.", canUndo: false);
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
