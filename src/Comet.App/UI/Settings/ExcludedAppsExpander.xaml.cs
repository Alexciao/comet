using System.Collections.ObjectModel;
using Comet.App.Services;
using Comet.App.UI.Settings.Dialogs;
using Comet.Platform.Context;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Comet.App.UI.Settings;

/// <summary>
/// An "Excluded apps" list (the : menu's in General, Auto-replace's on its page). Shows each app's icon, name
/// and path; program details and icons are read in the background and fill in as they arrive.
/// </summary>
public sealed partial class ExcludedAppsExpander : UserControl
{
    private readonly ObservableCollection<AppItem> _apps = [];
    private SettingsService? _store;
    private Func<List<string>>? _list;
    private bool _dialogOpen;

    public ExcludedAppsExpander()
    {
        InitializeComponent();
    }

    /// <summary>What exclusion means for this list (the card's description).</summary>
    public string Description { get; set; } = "";

    public ObservableCollection<AppItem> Apps => _apps;

    /// <summary>Connects the list to the setting it edits (a list of lowercase executable names).</summary>
    internal void Attach(SettingsService store, Func<List<string>> list)
    {
        _store = store;
        _list = list;
        Refresh();
    }

    /// <summary>Shows the current list (after a change here, in another window, or a hand edit).</summary>
    internal void Refresh()
    {
        if (_list is null || _store is null)
            return;
        var names = _list().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var existing = _apps.ToDictionary(a => a.FileName, StringComparer.OrdinalIgnoreCase);
        var target = new List<AppItem>(names.Count);
        foreach (var name in names)
        {
            if (existing.TryGetValue(name, out var item))
            {
                target.Add(item);
                continue;
            }

            item = new AppItem(ProgramInfo.FromFileName(name));
            target.Add(item);
            _ = ResolveAsync(item, _store.UiState.AppPaths.GetValueOrDefault(name));
        }

        _apps.SyncTo(target);
    }

    private async Task ResolveAsync(AppItem item, string? knownPath)
    {
        var program = await AppCatalog.ResolveAsync(item.FileName, knownPath).ConfigureAwait(true);
        item.Resolve(program);
        AppCatalog.LoadIcon(item, XamlRoot?.RasterizationScale ?? 1);
    }

    private async void OnAdd(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen || _list is null || _store is null || XamlRoot is null)
            return;
        _dialogOpen = true;
        try
        {
            var dialog = new AddAppDialog(_list()) { XamlRoot = XamlRoot };
            await dialog.ShowAsync();
            if (dialog.Chosen is not { } program)
                return;
            if (program.Path is { } path)
            {
                _store.UiState.AppPaths[program.FileName] = path;
                _store.SaveUiState();
            }

            var list = _list();
            if (!list.Contains(program.FileName, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(program.FileName);
                _store.SaveSettings();
            }

            Expander.IsExpanded = true;
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (_list is null || _store is null || (sender as FrameworkElement)?.DataContext is not AppItem item)
            return;
        _list().RemoveAll(a => string.Equals(a, item.FileName, StringComparison.OrdinalIgnoreCase));
        _store.SaveSettings();
    }
}
