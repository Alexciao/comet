using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Inlay.App.Services;
using Inlay.App.UI.Settings.Dialogs;
using Inlay.App.UI.Settings.Images;
using Inlay.Core.Images;
using Inlay.Core.Settings;
using Inlay.Core.Text;
using Inlay.Platform;
using Inlay.Platform.Images;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.System;

namespace Inlay.App.UI.Settings.Pages;

/// <summary>
/// Settings › Images, laid out like Windows Settings › Personalization › Background: the feature switch, an Add split
/// button with search and a count, and one tile per image, folder or collection (thumbnail, code, caption) in a
/// virtualized grid. Image files dropped on the page or pasted with Ctrl+V are added at once, with Undo. Library
/// copies, clipboard reads, folder scans and thumbnails all run in the background.
/// </summary>
public sealed partial class ImagesPage : SettingsPage, IImageTileHost
{
    /// <summary>
    /// The thumbnail asked for, in effective pixels: the tile is 150 × 110, so a 16:9 picture fitted into this square
    /// still fills the tile's height.
    /// </summary>
    private const double ThumbnailRequestEpx = 200;

    private const string ScreenshotName = "screenshot";
    private const string ScreenshotsFolder = "{Screenshots}";

    private readonly Dictionary<ImageEntry, ImageTileItem> _cache = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<ImageTileItem> _realized = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Task> _waiting = [];
    private readonly MenuFlyout _itemMenu = new();
    private ImageTileItem? _menuItem;
    private TileThumbnails? _thumbnails;
    private FolderFiles? _folders;
    private Task _libraryWork = Task.CompletedTask;
    private Undo? _undo;
    private string? _dialogFile;
    private bool _loading;
    private bool _dialogOpen;
    private bool _sourcesQueued;
    private bool _subscribed;

    /// <summary>What the InfoBar's Undo reverts: a deletion, or entries just added.</summary>
    private sealed record Undo(ImageEntry? Deleted, int Index, IReadOnlyList<string> AddedIds);

    public ImagesPage()
    {
        InitializeComponent();
        AddMenuItem(_itemMenu, "Edit", "", () => _ = EditAsync(_menuItem?.Entry));
        AddMenuItem(_itemMenu, "Duplicate", "", () => Duplicate(_menuItem?.Entry));
        AddMenuItem(_itemMenu, "Delete", "", () => Delete(_menuItem?.Entry));
        var paste = new KeyboardAccelerator { Key = VirtualKey.V, Modifiers = VirtualKeyModifiers.Control };
        paste.Invoked += OnPasteInvoked;
        KeyboardAccelerators.Add(paste);
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public ObservableCollection<ImageTileItem> Items { get; } = [];

    private List<ImageEntry> Entries => Store.Images;

    private ImagesService Images => Host.Images;

    private FolderFiles Folders => _folders ??= new FolderFiles(Images);

    protected override void OnAttached()
    {
        PerfProbe.TrackScrolling(Scroller, "images");
        FeatureToggle.IsOn = Settings.Features.Images;
        AfterFirstFrame(Refresh);
    }

    private protected override void OnSettingsChanged(SettingsParts parts)
    {
        if (parts.HasFlag(SettingsParts.Images) || parts.HasFlag(SettingsParts.Settings))
            Refresh();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            Images.ItemsChanged += OnItemsChanged;
            _subscribed = true;
        }

        // A later visit: folders may have new images, and files may have thumbnails now.
        if (_thumbnails is not null)
        {
            Folders.Forget();
            _thumbnails.ForgetMissing();
            QueueSources();
        }
    }

    /// <summary>Leaving the page or closing the window: the service outlives both, so stop listening to it.</summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
            return;
        Images.ItemsChanged -= OnItemsChanged;
        _subscribed = false;
    }

    /// <summary>A pool thread: a collection's folder changed, or the items were rebuilt after a save.</summary>
    private void OnItemsChanged() => DispatcherQueue.TryEnqueue(QueueSources);

    // ------------------------------------------------------------------ the tiles

    /// <summary>Shows the entries, filtered by the search box, as incremental changes to <see cref="Items"/>.</summary>
    private void Refresh()
    {
        _loading = true;
        FeatureToggle.IsOn = Settings.Features.Images;
        _loading = false;
        _thumbnails ??= new TileThumbnails(DispatcherQueue, Images.Thumbnails);

        var trigger = Settings.TriggerCharacter;
        var query = SearchBox.Text.Trim();
        var shown = new List<ImageTileItem>(Entries.Count);
        foreach (var entry in Entries.OrderBy(SortKey, StringComparer.CurrentCultureIgnoreCase))
        {
            if (query.Length > 0 && !Matches(entry, query))
                continue;
            if (!_cache.TryGetValue(entry, out var item) || item.IsStale(trigger))
                _cache[entry] = item = new ImageTileItem(entry, trigger, this);
            shown.Add(item);
        }

        if (_cache.Count > Entries.Count)
        {
            var live = new HashSet<ImageEntry>(Entries, ReferenceEqualityComparer.Instance);
            foreach (var gone in _cache.Keys.Where(k => !live.Contains(k)).ToList())
                _cache.Remove(gone);
        }

        Items.SyncTo(shown);
        ResolveSources();
        ShowCounts(shown.Count, query.Length > 0);
    }

    private static string SortKey(ImageEntry e) => e.Kind == ImageEntryKind.Collection ? e.Prefix : e.Name;

    private static bool Matches(ImageEntry e, string query) =>
        e.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || e.Keywords.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || (e.Kind == ImageEntryKind.Collection && e.Prefix.Contains(query, StringComparison.CurrentCultureIgnoreCase))
        || (e.Kind != ImageEntryKind.Image && ImageText.FolderName(e.Folder).Contains(query, StringComparison.CurrentCultureIgnoreCase));

    private void ShowCounts(int shown, bool searching)
    {
        var total = Entries.Count;
        var any = total > 0;
        CountText.Text = !any ? "" : searching ? $"{shown.ToString(CultureInfo.CurrentCulture)} of {Count(total)}" : Count(total);
        SearchBox.Visibility = CountText.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        ShowDeferred(ref EmptyState, nameof(EmptyState), !any);
        ShowDeferred(ref NoMatches, nameof(NoMatches), any && shown == 0);
    }

    private static string Count(int n) => n == 1 ? "1 image" : $"{n.ToString(CultureInfo.CurrentCulture)} images";

    /// <summary>Resolves the tiles' thumbnail sources once the current burst of changes is over.</summary>
    private void QueueSources()
    {
        if (_sourcesQueued || _thumbnails is null)
            return;
        _sourcesQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ResolveSources);
    }

    private void ResolveSources()
    {
        _sourcesQueued = false;
        foreach (var item in Items)
            ResolveSource(item);
    }

    /// <summary>What a tile shows: the image, or the newest image of the folder (whatever the folder's mode).</summary>
    private void ResolveSource(ImageTileItem item)
    {
        var entry = item.Entry;
        string? path;
        DateTime version = default;
        if (entry.Kind == ImageEntryKind.Image)
        {
            path = entry.File is { } file ? Images.Library.PathOf(file) : null;
        }
        else
        {
            var files = Folders.TryGet(entry, out var ready);
            if (files is null)
            {
                WhenReady(ready);
                return; // the placeholder (or the previous picture) until the folder has been read
            }

            var newest = FolderResolve.Newest(files, ImageTypes.Filter(entry.FileTypes));
            path = newest?.Path;
            version = newest?.LastWriteUtc ?? default;
        }

        if (item.SetSource(path, version) && _realized.Contains(item))
            LoadThumbnail(item);
    }

    private void WhenReady(Task ready)
    {
        if (!_waiting.Add(ready))
            return;
        _ = ready.ContinueWith(
            _ => DispatcherQueue.TryEnqueue(() =>
            {
                _waiting.Remove(ready);
                QueueSources();
            }),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private void OnTilePrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (sender.ItemsSourceView?.GetAt(args.Index) is not ImageTileItem item)
            return;
        _realized.Add(item);
        LoadThumbnail(item);
    }

    private void OnTileClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is FrameworkElement { DataContext: ImageTileItem item })
        {
            _realized.Remove(item);
            item.ShowThumbnail(null); // lets the bitmap go; it stays in the small LRU for a quick return
        }
    }

    private void LoadThumbnail(ImageTileItem item)
    {
        if (item.SourcePath is not { } path || _thumbnails is null)
        {
            item.ShowThumbnail(null);
            return;
        }

        if (!item.NeedsThumbnail)
            return;
        var version = item.SourceVersion;
        var size = (int)Math.Round(ThumbnailRequestEpx * (XamlRoot?.RasterizationScale ?? 1));
        var bitmap = _thumbnails.Get(path, version, size, arrived =>
        {
            // Still the tile's source, and still on screen.
            if (_realized.Contains(item) && item.SourcePath == path && item.SourceVersion == version)
                item.ShowThumbnail(arrived);
        });
        if (bitmap is not null)
            item.ShowThumbnail(bitmap);
    }

    void IImageTileHost.Open(ImageTileItem item) => _ = EditAsync(item.Entry);

    void IImageTileHost.ShowMenu(ImageTileItem item, FrameworkElement anchor, Point? at)
    {
        _menuItem = item;
        if (at is { } point)
            _itemMenu.ShowAt(anchor, new FlyoutShowOptions { Position = point });
        else
            _itemMenu.ShowAt(anchor);
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the entry's tile button (the template's first child) once the grid has laid
    /// out the latest change: creating an element in the middle of a change leaves the grid's rows misplaced.
    /// </summary>
    private void WithTile(ImageEntry entry, Action<Control> action) =>
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            for (var i = 0; i < Items.Count; i++)
            {
                if (!ReferenceEquals(Items[i].Entry, entry))
                    continue;
                if (Tiles.GetOrCreateElement(i) is Panel { Children.Count: > 0 } tile && tile.Children[0] is Control button)
                    action(button);
                return;
            }
        });

    // ------------------------------------------------------------------ toolbar

    private void OnFeatureToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || Settings.Features.Images == FeatureToggle.IsOn)
            return;
        Settings.Features.Images = FeatureToggle.IsOn;
        Store.SaveSettings();
    }

    private void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => Refresh();

    private void OnAddImageSplit(SplitButton sender, SplitButtonClickEventArgs args) => _ = AddImageAsync();

    private void OnAddImage(object sender, RoutedEventArgs e) => _ = AddImageAsync();

    private void OnAddFolder(object sender, RoutedEventArgs e) => _ = EditAsync(null, new ImageEntry { Kind = ImageEntryKind.Folder });

    private void OnAddCollection(object sender, RoutedEventArgs e) => _ = EditAsync(null, new ImageEntry { Kind = ImageEntryKind.Collection });

    /// <summary>The Latest screenshot preset: <c>screenshot</c> → the newest image in Screenshots, in one click.</summary>
    private void OnAddScreenshot(object sender, RoutedEventArgs e)
    {
        var entry = new ImageEntry
        {
            Kind = ImageEntryKind.Folder,
            Folder = ScreenshotsFolder,
            Mode = FolderMode.Newest,
            Name = NewNames().Take(ScreenshotName),
        };
        Entries.Add(entry);
        Store.SaveImages();
        ShowStatus(InfoBarSeverity.Success, $"Added {CodeOf(entry)}. Type it in any app to insert your latest screenshot.", new Undo(null, 0, [entry.Id]));
        WithTile(entry, tile => tile.StartBringIntoView());
    }

    // ------------------------------------------------------------------ adding files

    /// <summary>Add image…: pick a file, copy it into the library, then name it in the dialog.</summary>
    private async Task AddImageAsync()
    {
        if (_dialogOpen)
            return;
        string? path;
        try
        {
            path = await FilePickers.OpenImageAsync(OwnerWindow).ConfigureAwait(true);
        }
        catch (COMException ex)
        {
            Log.Warn($"images: file picker failed ({ex.HResult:X8})");
            return;
        }

        if (path is null)
            return;
        var library = Images.Library;
        LibraryImage? image = null;
        var failed = false;
        await EnqueueLibrary(async () =>
        {
            try
            {
                image = await Task.Run(() => library.Add(path)).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed = true;
            }

            _dialogFile = image?.File; // kept while the dialog is open
        }).ConfigureAwait(true);

        if (image is null)
        {
            ShowStatus(InfoBarSeverity.Error, failed ? "Couldn't copy the image. Check that the file still exists." : "Inlay adds PNG, JPG, GIF, WebP and BMP images.");
            return;
        }

        try
        {
            await EditAsync(null, new ImageEntry
            {
                Kind = ImageEntryKind.Image,
                File = image.File,
                SourceName = image.SourceName,
                Name = NewNames().Take(ImageNames.FromFile(image.SourceName)),
            }).ConfigureAwait(true);
        }
        finally
        {
            _dialogFile = null;
            CollectGarbage(); // the copy goes if the dialog was cancelled
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (_dialogOpen || !e.DataView.Contains(StandardDataFormats.StorageItems))
            return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        if (e.DragUIOverride is { } ui)
            ui.Caption = "Add to Images";
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (_dialogOpen || !e.DataView.Contains(StandardDataFormats.StorageItems))
            return;
        var deferral = e.GetDeferral();
        List<string> paths;
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            paths = [.. items.Where(i => i.IsOfType(StorageItemTypes.File) && !string.IsNullOrEmpty(i.Path)).Select(i => i.Path)];
        }
        catch (COMException ex)
        {
            Log.Warn($"images: drop failed ({ex.HResult:X8})");
            ShowStatus(InfoBarSeverity.Error, "Couldn't read the dropped files. Try dragging them again.");
            return;
        }
        finally
        {
            deferral.Complete();
        }

        if (paths.Count == 0)
            return;
        if (!paths.Any(p => ImageTypes.IsSupported(p)))
        {
            ShowStatus(InfoBarSeverity.Warning, "Inlay adds PNG, JPG, GIF, WebP and BMP images.");
            return;
        }

        var library = Images.Library;
        await EnqueueLibrary(async () =>
        {
            var (added, failed) = await Task.Run(() => CopyIn(library, paths)).ConfigureAwait(true);
            AddEntries(added, failed);
        }).ConfigureAwait(true);
    }

    /// <summary>Copies the supported files into the library. Background thread.</summary>
    private static (List<LibraryImage> Added, int Failed) CopyIn(ImageLibrary library, List<string> paths)
    {
        List<LibraryImage> added = [];
        var failed = 0;
        foreach (var path in paths.Where(p => ImageTypes.IsSupported(p)))
        {
            try
            {
                if (library.Add(path) is { } image)
                    added.Add(image);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
            }
        }

        return (added, failed);
    }

    /// <summary>Delete on a focused tile deletes it (with Undo).</summary>
    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _dialogOpen || e.Key != VirtualKey.Delete || e.OriginalSource is not FrameworkElement { DataContext: ImageTileItem item })
            return;
        e.Handled = true;
        Delete(item.Entry);
    }

    /// <summary>
    /// Ctrl+V anywhere in the window while the page is shown (the accelerator isn't scoped to focus), except in a text
    /// box, where it pastes text as usual.
    /// </summary>
    private void OnPasteInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_dialogOpen || XamlRoot is null || FocusManager.GetFocusedElement(XamlRoot) is TextBox)
            return;
        args.Handled = true;
        _ = PasteAsync();
    }

    /// <summary>Ctrl+V: the image files or the picture on the clipboard.</summary>
    private async Task PasteAsync()
    {
        var library = Images.Library;
        await EnqueueLibrary(async () =>
        {
            IReadOnlyList<LibraryImage> added;
            try
            {
                added = await Task.Run(library.AddFromClipboard).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowStatus(InfoBarSeverity.Error, "Couldn't paste the image. Copy it again, then press Ctrl+V.");
                return;
            }

            if (added.Count == 0)
                ShowStatus(InfoBarSeverity.Informational, "There's no image on the clipboard. Copy an image or an image file first.");
            else
                AddEntries(added, 0);
        }).ConfigureAwait(true);
    }

    /// <summary>One entry per new library file, named after its file, with Undo. Files already added are skipped.</summary>
    private void AddEntries(IReadOnlyList<LibraryImage> images, int failed)
    {
        var names = NewNames();
        List<string> ids = [];
        ImageEntry? first = null;
        ImageEntry? already = null;
        foreach (var image in images)
        {
            if (Entries.Find(e => e.Kind == ImageEntryKind.Image && string.Equals(e.File, image.File, StringComparison.OrdinalIgnoreCase)) is { } existing)
            {
                already ??= existing;
                continue;
            }

            var entry = new ImageEntry
            {
                Kind = ImageEntryKind.Image,
                File = image.File,
                SourceName = image.SourceName,
                Name = names.Take(ImageNames.FromFile(image.SourceName)),
            };
            Entries.Add(entry);
            ids.Add(entry.Id);
            first ??= entry;
        }

        if (first is null)
        {
            if (failed > 0)
                ShowStatus(InfoBarSeverity.Error, failed == 1 ? "Couldn't copy the image. Check that the file still exists." : "Couldn't copy the images. Check that the files still exist.");
            else if (already is not null)
                ShowStatus(InfoBarSeverity.Informational, images.Count == 1 ? $"This image is already here as {CodeOf(already)}." : "These images are already here.");
            return;
        }

        Store.SaveImages();
        var message = ids.Count == 1 ? $"Added {CodeOf(first)}." : $"Added {Count(ids.Count)}.";
        if (failed > 0)
            message += failed == 1 ? " One file couldn't be copied." : $" {failed.ToString(CultureInfo.CurrentCulture)} files couldn't be copied.";
        ShowStatus(failed > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success, message, new Undo(null, 0, ids));
        WithTile(first, tile => tile.StartBringIntoView());
    }

    // ------------------------------------------------------------------ edit, duplicate, delete

    /// <summary>Edits <paramref name="existing"/>, or adds <paramref name="draft"/>.</summary>
    private async Task EditAsync(ImageEntry? existing, ImageEntry? draft = null)
    {
        // One dialog at a time (a second ShowAsync while one is open throws).
        if (_dialogOpen || XamlRoot is null || (existing is null ? draft : Copy(existing, keepId: true)) is not { } editing)
            return;
        _dialogOpen = true;
        try
        {
            var context = new ImageDialogContext(
                Settings.TriggerCharacter,
                [.. Store.Snippets.Select(s => s.Name)],
                [.. ImageNames.InUse(Entries, Images.Names(editing.Id), editing.Id)],
                Host.IsEmojiShortcode,
                Images.Library,
                Folders,
                () => FilePickers.OpenFolderAsync(OwnerWindow));
            var dialog = new ImageDialog(editing, existing is null, context) { XamlRoot = XamlRoot };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            // The list may have been reloaded (a hand edit, a restore) while the dialog was open: edit what is there now.
            var result = dialog.Result;
            var target = Entries.Find(e => e.Id == result.Id);
            if (target is null)
            {
                target = result;
                Entries.Add(target);
                if (existing is null)
                    ShowStatus(InfoBarSeverity.Success, AddedMessage(target, dialog.CollectionCount));
            }
            else
            {
                CopyFields(result, target);
            }

            Store.SaveImages();
            WithTile(target, tile => tile.Focus(FocusState.Programmatic));
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private string AddedMessage(ImageEntry entry, int count)
    {
        if (entry.Kind != ImageEntryKind.Collection)
            return $"Added {CodeOf(entry)}. Type it in any app to insert it.";
        var folder = ImageText.FolderName(entry.Folder);
        return count < 0 ? $"Added the collection from {folder}." : $"Added {Count(count)} from {folder}.";
    }

    private void Duplicate(ImageEntry? entry)
    {
        var index = entry is null ? -1 : Entries.IndexOf(entry);
        if (index < 0)
            return;
        var copy = Copy(entry!, keepId: false);
        if (copy.Kind != ImageEntryKind.Collection)
            copy.Name = NewNames().Take(entry!.Name);
        Entries.Insert(index + 1, copy);
        Store.SaveImages();
        WithTile(copy, tile => tile.Focus(FocusState.Programmatic));
    }

    private void Delete(ImageEntry? entry)
    {
        var index = entry is null ? -1 : Entries.IndexOf(entry);
        if (index < 0)
            return;
        Entries.RemoveAt(index);
        Store.SaveImages();
        var what = entry!.Kind == ImageEntryKind.Collection ? $"the collection from {ImageText.FolderName(entry.Folder)}" : CodeOf(entry);
        ShowStatus(InfoBarSeverity.Informational, $"Deleted {what}.", new Undo(entry, index, []));
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        if (_undo is { } undo)
        {
            _undo = null;
            if (undo.Deleted is { } entry)
            {
                if (entry.Kind != ImageEntryKind.Collection && IsNameTaken(entry.Name))
                {
                    ShowStatus(InfoBarSeverity.Warning, $"{CodeOf(entry)} is in use now, so the deleted image wasn't restored.");
                    CollectGarbage();
                    return;
                }

                Entries.Insert(Math.Min(undo.Index, Entries.Count), entry);
            }
            else
            {
                var ids = new HashSet<string>(undo.AddedIds, StringComparer.Ordinal);
                Entries.RemoveAll(x => ids.Contains(x.Id));
            }

            Store.SaveImages();
            CollectGarbage();
        }

        if (StatusBar is not null)
            StatusBar.IsOpen = false;
    }

    private bool IsNameTaken(string name) =>
        Store.Snippets.Any(s => string.Equals(s.Name.Trim(), name, StringComparison.OrdinalIgnoreCase))
        || ImageNames.InUse(Entries, Images.Names()).Any(n => string.Equals(n.Trim(), name, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ helpers

    private string CodeOf(ImageEntry entry) => TriggerFormat.Code(Settings.TriggerCharacter, entry.Name);

    /// <summary>Made-up names for new entries, unique across snippets, images and emoji.</summary>
    private ImageNames NewNames() =>
        new(Store.Snippets.Select(s => s.Name), ImageNames.InUse(Entries, Images.Names()), Host.IsEmojiShortcode);

    private static ImageEntry Copy(ImageEntry e, bool keepId)
    {
        var copy = new ImageEntry();
        CopyFields(e, copy);
        if (keepId)
            copy.Id = e.Id;
        return copy;
    }

    /// <summary>Every field but the id.</summary>
    private static void CopyFields(ImageEntry from, ImageEntry to)
    {
        to.Name = from.Name;
        to.Keywords = from.Keywords;
        to.Kind = from.Kind;
        to.InsertAs = from.InsertAs;
        to.File = from.File;
        to.SourceName = from.SourceName;
        to.Folder = from.Folder;
        to.Mode = from.Mode;
        to.PickCount = from.PickCount;
        to.IncludeSubfolders = from.IncludeSubfolders;
        to.FileTypes = [.. from.FileTypes];
        to.Prefix = from.Prefix;
    }

    /// <summary>
    /// Runs library work one piece at a time, on the UI thread between its awaits: a copy and the entry that uses it
    /// are saved before a garbage collection can see the library, so a new file is never collected.
    /// </summary>
    private Task EnqueueLibrary(Func<Task> work)
    {
        var previous = _libraryWork;
        var next = Run();
        _libraryWork = next;
        return next;

        async Task Run()
        {
            await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            await work().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Deletes library files nothing uses any more, in the background. Files an Undo or the open dialog could still
    /// need are kept.
    /// </summary>
    private void CollectGarbage()
    {
        var library = Images.Library;
        _ = EnqueueLibrary(async () =>
        {
            List<string> keep = [.. Entries.Select(e => e.File).Append(_undo?.Deleted?.File).Append(_dialogFile).OfType<string>()];
            var deleted = await Task.Run(() =>
            {
                try
                {
                    return library.CollectGarbage(keep);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warn($"images: library cleanup failed ({ex.GetType().Name})");
                    return 0;
                }
            }).ConfigureAwait(true);
            if (deleted > 0)
                Log.Info($"images: removed {deleted} unused library files");
        });
    }

    private void ShowStatus(InfoBarSeverity severity, string message, Undo? undo = null)
    {
        var expired = _undo is not null;
        _undo = undo;
        StatusBar ??= (InfoBar)FindName(nameof(StatusBar));
        StatusBar.Severity = severity;
        StatusBar.Message = message;
        UndoButton.Visibility = undo is null ? Visibility.Collapsed : Visibility.Visible;
        StatusBar.IsOpen = true;
        if (expired)
            CollectGarbage();
    }

    /// <summary>The InfoBar was closed: its Undo is gone, and so are the files only it kept.</summary>
    private void OnStatusClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (_undo is null)
            return;
        _undo = null;
        CollectGarbage();
    }
}
