using System.Collections.ObjectModel;
using System.ComponentModel;
using Inlay.App.Services;
using Inlay.App.UI.Settings.Dialogs;
using Inlay.Core.Settings;
using Inlay.Platform.Context;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Inlay.App.UI.Settings;

/// <summary>
/// Quick Insert's per-app list: each app with its mode (Show everything, Hide emoji, Off). Program details and icons
/// are read in the background and fill in as they arrive, as in <see cref="ExcludedAppsExpander"/>.
/// </summary>
public sealed partial class AppModesExpander : UserControl
{
    private readonly ObservableCollection<AppModeItem> _apps = [];
    private SettingsService? _store;
    private bool _dialogOpen;

    public AppModesExpander()
    {
        InitializeComponent();
    }

    public ObservableCollection<AppModeItem> Apps => _apps;

    private List<AppQuickInsertMode> List => _store!.Settings.QuickInsertApps;

    internal void Attach(SettingsService store)
    {
        _store = store;
        Refresh();
    }

    /// <summary>Shows the current list (after a change here, in another window, or a hand edit).</summary>
    internal void Refresh()
    {
        if (_store is null)
            return;
        var entries = List.OrderBy(a => a.App, StringComparer.OrdinalIgnoreCase).ToList();
        var existing = _apps.ToDictionary(a => a.App.FileName, StringComparer.OrdinalIgnoreCase);
        var target = new List<AppModeItem>(entries.Count);
        foreach (var entry in entries)
        {
            if (existing.TryGetValue(entry.App, out var item))
            {
                item.Show(entry.Mode);
                target.Add(item);
                continue;
            }

            item = new AppModeItem(new AppItem(ProgramInfo.FromFileName(entry.App)), entry.Mode, SetMode, Remove);
            target.Add(item);
            _ = ResolveAsync(item.App, _store.UiState.AppPaths.GetValueOrDefault(entry.App));
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
        if (_dialogOpen || _store is null || XamlRoot is null)
            return;
        _dialogOpen = true;
        try
        {
            var dialog = new AddAppDialog(List.Select(a => a.App)) { XamlRoot = XamlRoot };
            await dialog.ShowAsync();
            if (dialog.Chosen is not { } program)
                return;
            if (program.Path is { } path)
            {
                _store.UiState.AppPaths[program.FileName] = path;
                _store.SaveUiState();
            }

            if (!List.Any(a => string.Equals(a.App, program.FileName, StringComparison.OrdinalIgnoreCase)))
            {
                // Added apps start as Off, the usual reason to add one; the mode can be changed right here.
                List.Add(new AppQuickInsertMode { App = program.FileName.ToLowerInvariant(), Mode = QuickInsertMode.Off });
                _store.SaveSettings();
            }

            Expander.IsExpanded = true;
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private void SetMode(AppModeItem item, QuickInsertMode mode)
    {
        if (_store is null || List.Find(a => string.Equals(a.App, item.App.FileName, StringComparison.OrdinalIgnoreCase)) is not { } entry || entry.Mode == mode)
            return;
        entry.Mode = mode;
        _store.SaveSettings();
    }

    private void Remove(AppModeItem item)
    {
        if (_store is null)
            return;
        List.RemoveAll(a => string.Equals(a.App, item.App.FileName, StringComparison.OrdinalIgnoreCase));
        _store.SaveSettings();
    }
}

/// <summary>An app in Quick Insert's per-app list, with its mode as the combo box's index.</summary>
public sealed partial class AppModeItem : INotifyPropertyChanged
{
    private readonly Action<AppModeItem, QuickInsertMode> _setMode;
    private readonly Action<AppModeItem> _remove;
    private QuickInsertMode _mode;

    internal AppModeItem(AppItem app, QuickInsertMode mode, Action<AppModeItem, QuickInsertMode> setMode, Action<AppModeItem> remove)
    {
        App = app;
        _mode = mode;
        _setMode = setMode;
        _remove = remove;
        app.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppItem.DisplayName))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ModeName)));
        };
    }

    public AppItem App { get; }

    /// <summary>The combo box's selection: the <see cref="QuickInsertMode"/> values in order.</summary>
    public int ModeIndex
    {
        get => (int)_mode;
        set
        {
            if (value < 0 || (QuickInsertMode)value == _mode)
                return;
            _mode = (QuickInsertMode)value;
            _setMode(this, _mode);
        }
    }

    public string ModeName => $"Quick Insert in {App.DisplayName}";

    public void Remove() => _remove(this);

    /// <summary>Shows a mode changed elsewhere (another window, a hand edit).</summary>
    internal void Show(QuickInsertMode mode)
    {
        if (mode == _mode)
            return;
        _mode = mode;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ModeIndex)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
