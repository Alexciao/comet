using Inlay.Core.Images;
using Inlay.Core.Settings;
using Inlay.Platform.Images;

namespace Inlay.App.UI.Settings.Images;

/// <summary>
/// The image files of the folders the Images page shows (tile thumbnails, the dialog's preview and collection names),
/// newest first. An entry Quick Insert already watches reads the service's live list; anything else (Images off, a
/// folder just chosen in the dialog) is enumerated once in the background and remembered until <see cref="Forget"/>.
/// UI thread only; nothing here waits on the disk.
/// </summary>
internal sealed class FolderFiles(ImagesService images)
{
    private readonly Dictionary<(string Folder, bool Recursive), Task<IReadOnlyList<ImageFile>>> _scans = [];
    private IReadOnlyList<ImageItem>? _itemsSeen;
    private Dictionary<string, FolderCache> _caches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _resolved = new(StringComparer.OrdinalIgnoreCase); // folder → path, per page visit

    /// <summary>
    /// A Folder or Collection entry's files, or null while they are being read; <paramref name="ready"/> completes
    /// when a later call can return them.
    /// </summary>
    public IReadOnlyList<ImageFile>? TryGet(ImageEntry entry, out Task ready)
    {
        if (CacheOf(entry) is { } cache)
        {
            var scanning = cache.Scanning;
            if (cache.Index.Loaded || scanning.IsCompleted)
            {
                ready = Task.CompletedTask;
                return cache.Index.Files;
            }

            ready = scanning;
            return null;
        }

        var scan = Scan(entry.Folder, entry.IncludeSubfolders);
        ready = scan;
        return scan.IsCompletedSuccessfully ? scan.Result : null;
    }

    /// <summary>A folder's files (a path or a known folder token), read once in the background.</summary>
    public Task<IReadOnlyList<ImageFile>> Scan(string? folder, bool recursive)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return Task.FromResult<IReadOnlyList<ImageFile>>([]);
        var key = (folder, recursive);
        if (!_scans.TryGetValue(key, out var scan) || scan.IsFaulted)
            _scans[key] = scan = Task.Run(() => ScanNow(folder, recursive));
        return scan;
    }

    /// <summary>Forgets the one-off scans (the page is shown again: folders may have changed meanwhile).</summary>
    public void Forget()
    {
        _scans.Clear();
        _resolved.Clear();
    }

    private FolderCache? CacheOf(ImageEntry entry)
    {
        var items = images.Items;
        if (!ReferenceEquals(items, _itemsSeen))
        {
            _itemsSeen = items;
            _caches = new(StringComparer.Ordinal);
            foreach (var item in items)
            {
                if (item.Cache is { } c)
                    _caches.TryAdd(item.Entry.Id, c);
            }
        }

        // The service's cache only while it still describes this folder (an edit may have just changed it).
        if (!_caches.TryGetValue(entry.Id, out var cache) || cache.Recursive != entry.IncludeSubfolders || entry.Folder is not { } folder)
            return null;
        if (!_resolved.TryGetValue(folder, out var path))
            _resolved[folder] = path = KnownFolders.Resolve(folder);
        return string.Equals(cache.FolderPath, path, StringComparison.OrdinalIgnoreCase) ? cache : null;
    }

    private static async Task<IReadOnlyList<ImageFile>> ScanNow(string folder, bool recursive)
    {
        if (KnownFolders.Resolve(folder) is not { } path)
            return [];
        using var cache = new FolderCache(path, recursive, watch: false);
        cache.Start();
        await cache.Scanning.ConfigureAwait(false);
        return cache.Index.Files;
    }
}
