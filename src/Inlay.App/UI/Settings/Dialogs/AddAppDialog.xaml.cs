using System.Collections.ObjectModel;
using Inlay.Platform.Context;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Inlay.App.UI.Settings.Dialogs;

/// <summary>
/// "Add an app": a searchable list of the apps that are open (with their icons), or Browse… for a program file.
/// The list is gathered in the background and shown as soon as it is ready; icons fill in as they are read.
/// </summary>
public sealed partial class AddAppDialog : ContentDialog
{
    private readonly HashSet<string> _excluded;
    private List<AppItem> _all = [];

    internal AddAppDialog(IEnumerable<string> alreadyExcluded)
    {
        _excluded = new HashSet<string>(alreadyExcluded, StringComparer.OrdinalIgnoreCase);
        InitializeComponent();
        PrimaryButtonClick += (_, _) => Chosen = (AppList.SelectedItem as AppItem)?.Program;
        Loaded += OnLoaded;
    }

    public ObservableCollection<AppItem> Shown { get; } = [];

    /// <summary>The app to exclude, or null when the dialog was cancelled.</summary>
    internal ProgramInfo? Chosen { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var running = await AppCatalog.RunningAsync().ConfigureAwait(true);
        var scale = XamlRoot?.RasterizationScale ?? 1;
        _all = [.. running.Where(p => !_excluded.Contains(p.FileName)).Select(p => new AppItem(p))];
        foreach (var item in _all)
            AppCatalog.LoadIcon(item, scale);
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
        Filter();
    }

    private void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => Filter();

    private void Filter()
    {
        var query = SearchBox.Text.Trim();
        Shown.SyncTo([.. _all.Where(a => query.Length == 0
            || a.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || a.FileName.Contains(query, StringComparison.OrdinalIgnoreCase))]);
        Empty.Visibility = LoadingRing.IsActive || Shown.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => IsPrimaryButtonEnabled = AppList.SelectedItem is AppItem;

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (AppList.SelectedItem is not AppItem item)
            return;
        Chosen = item.Program;
        Hide();
    }

    private async void OnBrowse(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is null)
            return;
        var path = await AppCatalog.BrowseAsync(XamlRoot).ConfigureAwait(true);
        if (path is null)
            return;
        Chosen = await Task.Run(() => ProgramInfo.FromPath(path)).ConfigureAwait(true);
        Hide();
    }
}
