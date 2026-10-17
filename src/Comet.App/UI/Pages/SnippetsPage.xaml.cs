using Comet.App.Services;
using Comet.App.UI.Controls;
using Comet.App.UI.Dialogs;
using Comet.Core.Settings;
using Comet.Core.Snippets;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Comet.App.UI.Pages;

public sealed partial class SnippetsPage : UserControl
{
    private const string SnippetGlyph = "";

    private readonly AppHost _host;
    private readonly Window _owner;
    private (SnippetDefinition Item, int Index)? _deleted;
    private bool _loading;
    private bool _dialogOpen;

    internal SnippetsPage(AppHost host, Window owner)
    {
        _host = host;
        _owner = owner;
        InitializeComponent();
    }

    private List<SnippetDefinition> Snippets => _host.Settings.Snippets;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _host.Settings.Changed += OnSettingsChanged;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _host.Settings.Changed -= OnSettingsChanged;

    private void OnSettingsChanged(SettingsParts parts)
    {
        if (parts.HasFlag(SettingsParts.Snippets) || parts.HasFlag(SettingsParts.Settings))
            Refresh();
    }

    private void Refresh()
    {
        _loading = true;
        FeatureToggle.IsOn = _host.Settings.Settings.Features.Snippets;
        _loading = false;

        ListPanel.Children.Clear();
        var query = SearchBox.Text.Trim();
        var shown = 0;
        foreach (var snippet in Snippets.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (!PageHelpers.Matches(query, snippet.Name, snippet.Text))
                continue;
            var problems = SnippetTemplate.Validate(snippet.Text);
            var warning = problems.Count == 0 ? null : string.Join(" ", problems.Select(p => p.Message));
            var item = snippet;
            var row = new EntryRow();
            row.Show(SnippetGlyph, ":" + item.Name, PageHelpers.OneLine(item.Text), warning, enabled: null);
            row.EditRequested += () => _ = Edit(item);
            row.DuplicateRequested += () => Duplicate(item);
            row.DeleteRequested += () => Delete(item);
            ListPanel.Children.Add(row);
            shown++;
        }

        var any = Snippets.Count > 0;
        CountText.Text = any ? (Snippets.Count == 1 ? "1 snippet" : $"{Snippets.Count} snippets") : "";
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
        _host.Settings.Settings.Features.Snippets = FeatureToggle.IsOn;
        _host.Settings.SaveSettings();
    }

    private void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => Refresh();

    private void OnAdd(object sender, RoutedEventArgs e) => _ = Edit(null);

    private async Task Edit(SnippetDefinition? existing)
    {
        // One dialog at a time (a second ShowAsync while one is open throws).
        if (_dialogOpen || XamlRoot is null)
            return;
        _dialogOpen = true;
        try
        {
            var others = Snippets.Where(s => s != existing).Select(s => s.Name).ToList();
            var dialog = new SnippetDialog(existing, others, _host.IsEmojiShortcode) { XamlRoot = XamlRoot };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            if (existing is null)
            {
                Snippets.Add(new SnippetDefinition { Name = dialog.SnippetName, Text = dialog.SnippetText });
                ShowStatus(InfoBarSeverity.Success, $"Added :{dialog.SnippetName}. Type it in any app.", canUndo: false);
            }
            else
            {
                existing.Name = dialog.SnippetName;
                existing.Text = dialog.SnippetText;
            }

            _host.Settings.SaveSnippets();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private void Duplicate(SnippetDefinition item)
    {
        var name = item.Name + "-copy";
        for (var n = 2; Snippets.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)); n++)
            name = $"{item.Name}-copy{n}";
        Snippets.Add(new SnippetDefinition { Name = name, Text = item.Text });
        _host.Settings.SaveSnippets();
    }

    private void Delete(SnippetDefinition item)
    {
        var index = Snippets.IndexOf(item);
        if (index < 0)
            return;
        Snippets.RemoveAt(index);
        _deleted = (item, index);
        _host.Settings.SaveSnippets();
        ShowStatus(InfoBarSeverity.Informational, $"Deleted :{item.Name}.", canUndo: true);
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        if (_deleted is { } d)
        {
            Snippets.Insert(Math.Min(d.Index, Snippets.Count), d.Item);
            _deleted = null;
            _host.Settings.SaveSnippets();
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
            var (added, replaced) = ImportExport.ImportSnippets(await File.ReadAllTextAsync(path), Snippets);
            _host.Settings.SaveSnippets();
            ShowStatus(InfoBarSeverity.Success, $"Imported {added} new and {replaced} updated snippets.", canUndo: false);
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
            var path = await PageHelpers.PickSaveAsync(_owner, "Comet snippets");
            if (path is null)
                return;
            await File.WriteAllTextAsync(path, ImportExport.ExportSnippets(Snippets));
            ShowStatus(InfoBarSeverity.Success, $"Exported {Snippets.Count} snippets to {Path.GetFileName(path)}.", canUndo: false);
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
