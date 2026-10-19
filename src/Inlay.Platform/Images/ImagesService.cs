using Inlay.Core.Images;
using Inlay.Core.Settings;
using Inlay.Platform.Input;

namespace Inlay.Platform.Images;

/// <summary>
/// One name Quick Insert knows an image by: an Image entry, a Folder entry (resolved when used) or one file of a
/// Collection. Built by <see cref="ImagesService"/>; immutable.
/// </summary>
internal sealed record ImageItem(string Name, string Keywords, ImageEntry Entry)
{
    /// <summary>Images and collection items: the file. Null for folders, which resolve when used.</summary>
    public ImageFile? File { get; init; }

    /// <summary>Folders and collections: where the folder's files are cached (null when the folder can't be resolved).</summary>
    internal FolderCache? Cache { get; init; }

    internal ImageType Types { get; init; } = ImageType.All;

    public ImageEntryKind Kind => Entry.Kind;

    /// <summary>The frecency key: per entry, and per file for collection items.</summary>
    public string Key => Entry.Kind == ImageEntryKind.Collection ? $"image:{Entry.Id}:{Name}" : $"image:{Entry.Id}";
}

/// <summary>
/// The Images feature's non-UI side, wired up by the app: the list of <see cref="ImageItem"/>s for the search index,
/// one <see cref="FolderCache"/> per folder an entry actually uses, the library and thumbnails. Off (feature off or
/// no entries) means nothing: no watcher, no scan, no thread, no cached thumbnail.
/// <para>
/// Threads: <see cref="Update"/> from the UI thread; <see cref="Items"/>, <see cref="Resolve"/>,
/// <see cref="PickCandidates"/> and <see cref="ToInsertion"/> from any thread, lock-free and without disk access (the
/// keyboard-hook thread calls them); <see cref="ItemsChanged"/> is raised on a pool thread.
/// </para>
/// </summary>
internal sealed class ImagesService : IDisposable
{
    /// <summary>Folder events come in bursts (a copy of 50 files); the item list is rebuilt once they settle.</summary>
    internal const int ChangeDebounceMs = 300;

    private readonly Lock _gate = new();
    private readonly Lock _rebuildGate = new();
    private readonly Timer _rebuild;
    private readonly Dictionary<string, long> _sizes = new(StringComparer.OrdinalIgnoreCase); // library files never change
    private State _state = State.Off;
    private ImageItem[] _items = [];
    private bool _disposed;

    public ImagesService(ImageLibrary library)
    {
        Library = library;
        _rebuild = new Timer(_ => Rebuild());
    }

    /// <param name="Caches">By folder key (see <see cref="CacheKey"/>).</param>
    /// <param name="Keys">Entry id → its folder key (folders resolved once per update, not per event).</param>
    private sealed record State(bool On, IReadOnlyList<ImageEntry> Entries, HashSet<string> SnippetNames, Dictionary<string, FolderCache> Caches, Dictionary<string, string> Keys)
    {
        public static State Off { get; } = new(false, [], [], [], []);

        public FolderCache? CacheOf(ImageEntry e) => Keys.TryGetValue(e.Id, out var key) ? Caches.GetValueOrDefault(key) : null;
    }

    public ImageLibrary Library { get; }

    /// <summary>Thumbnails for the popup and the Settings tiles (its thread starts on the first request).</summary>
    public Thumbnails Thumbnails { get; } = new();

    /// <summary>Everything Quick Insert can show for images, names unique (against snippets too). Rebuilt in the background.</summary>
    public IReadOnlyList<ImageItem> Items => Volatile.Read(ref _items);

    /// <summary>Raised on a pool thread when <see cref="Items"/> changed: rebuild the search index.</summary>
    public event Action? ItemsChanged;

    /// <summary>
    /// Call at startup and whenever the feature is toggled, the image entries change, or the snippet names change.
    /// Starts caches for newly used folders (background scan), stops those no longer used, and rebuilds the items.
    /// </summary>
    public void Update(bool enabled, IReadOnlyList<ImageEntry> entries, IEnumerable<string> snippetNames)
    {
        List<FolderCache> stale;
        List<FolderCache> started = [];
        lock (_gate)
        {
            if (_disposed)
                return;
            var old = _state;
            var on = enabled && entries.Count > 0;
            var caches = new Dictionary<string, FolderCache>(StringComparer.OrdinalIgnoreCase);
            var keys = new Dictionary<string, string>(StringComparer.Ordinal);
            if (on)
            {
                foreach (var e in entries)
                {
                    if (e.Kind == ImageEntryKind.Image || CacheKey(e) is not { } key)
                        continue;
                    keys[e.Id] = key;
                    if (caches.ContainsKey(key))
                        continue;
                    if (!old.Caches.TryGetValue(key, out var cache))
                    {
                        cache = new FolderCache(KnownFolders.Resolve(e.Folder)!, e.IncludeSubfolders);
                        cache.Changed += () => FolderChanged(cache);
                        started.Add(cache);
                    }

                    caches[key] = cache;
                }
            }

            stale = [.. old.Caches.Values.Where(c => !caches.ContainsValue(c))];
            _state = on ? new State(true, [.. entries], new(snippetNames.Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase), caches, keys) : State.Off;
        }

        foreach (var c in stale)
            c.Dispose();
        foreach (var c in started)
            c.Start();
        if (!enabled || entries.Count == 0)
            Thumbnails.Clear();
        _rebuild.Change(0, Timeout.Infinite);
    }

    /// <summary>
    /// The file to insert for an item: the library file, the collection's file, or for a folder the newest / a random /
    /// the <paramref name="pick"/>-th newest image (0-based; -1 = the first). Null when there is none (empty folder,
    /// not scanned yet). Lock-free, no disk access.
    /// </summary>
    public string? Resolve(ImageItem item, int pick = -1)
    {
        if (item.Kind != ImageEntryKind.Folder)
            return item.File?.Path;
        if (item.Cache is not { } cache)
            return null;
        var files = cache.Index.Files;
        return item.Entry.Mode switch
        {
            FolderMode.Random => FolderResolve.Random(files, item.Types, Random.Shared)?.Path,
            FolderMode.Pick when pick > 0 => FolderResolve.Newest(files, item.Types, item.Entry.PickCount) is { } c && pick < c.Count ? c[pick].Path : null,
            _ => FolderResolve.Newest(files, item.Types)?.Path,
        };
    }

    /// <summary>A folder item's newest image (its thumbnail, and what Newest inserts), or null.</summary>
    public ImageFile? Newest(ImageItem item) =>
        item.Kind == ImageEntryKind.Folder && item.Cache is { } cache ? FolderResolve.Newest(cache.Index.Files, item.Types) : item.File;

    /// <summary>Let me pick: the newest <c>PickCount</c> images, newest first (the strip Quick Insert shows).</summary>
    public IReadOnlyList<ImageFile> PickCandidates(ImageItem item) =>
        item.Cache is { } cache ? FolderResolve.Newest(cache.Index.Files, item.Types, item.Entry.PickCount) : [];

    /// <summary>False while a folder item's first scan is still running (show "Loading…" rather than "Empty folder").</summary>
    public bool IsLoaded(ImageItem item) => item.Cache?.Index.Loaded ?? true;

    /// <summary>A Collection entry's files with their names, from the current items (the Settings card's count and tiles).</summary>
    public IReadOnlyList<ImageItem> ItemsOf(ImageEntry entry) => [.. Items.Where(i => i.Entry.Id == entry.Id)];

    /// <summary>Every name images use (for validation), the items of the entry <paramref name="exceptEntryId"/> excluded.</summary>
    public IReadOnlyList<string> Names(string? exceptEntryId = null) =>
        [.. Items.Where(i => i.Entry.Id != exceptEntryId).Select(i => i.Name)];

    /// <summary>
    /// The engine's image job payload for an item (null when it resolves to nothing). The method for Insert as Image
    /// comes from <paramref name="learnedMethods"/> (<c>AppSettings.ImagePasteMethods</c>) or the built-in defaults.
    /// </summary>
    public ImageInsertion? ToInsertion(ImageItem item, string process, IReadOnlyDictionary<string, string>? learnedMethods, int pick = -1)
    {
        if (Resolve(item, pick) is not { } path)
            return null;
        // Library files are named by their hash; the app sees the entry's name instead ("lgtm.gif").
        var display = item.Kind == ImageEntryKind.Image ? $"{item.Name}{Path.GetExtension(path)}" : null;
        return new ImageInsertion(path, item.Entry.InsertAs, ImagePasteMethod.For(process, learnedMethods), process) { DisplayName = display };
    }

    /// <summary>The folder a Folder or Collection entry uses, resolved (known folders followed), or null.</summary>
    public static string? FolderPath(ImageEntry entry) => KnownFolders.Resolve(entry.Folder);

    private static string? CacheKey(ImageEntry e) =>
        KnownFolders.Resolve(e.Folder) is { } path ? $"{path}|{(e.IncludeSubfolders ? 1 : 0)}" : null;

    private void FolderChanged(FolderCache cache)
    {
        State state;
        lock (_gate)
            state = _state;
        // Folder entries read the live list when used; only collections turn files into names.
        if (state.Entries.Any(e => e.Kind == ImageEntryKind.Collection && state.CacheOf(e) == cache))
            _rebuild.Change(ChangeDebounceMs, Timeout.Infinite);
    }

    private void Rebuild()
    {
        lock (_rebuildGate)
        {
            State state;
            lock (_gate)
            {
                if (_disposed)
                    return;
                state = _state;
            }

            var items = state.On ? Build(state) : [];
            var old = Interlocked.Exchange(ref _items, items);
            if (old.Length == 0 && items.Length == 0)
                return;
            try
            {
                ItemsChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Error("images changed", ex);
            }
        }
    }

    private ImageItem[] Build(State state)
    {
        var items = new List<ImageItem>();
        var taken = new HashSet<string>(state.SnippetNames, StringComparer.OrdinalIgnoreCase);
        foreach (var e in state.Entries)
        {
            if (e.Kind == ImageEntryKind.Collection || !taken.Add(e.Name))
                continue; // a repeated name (a hand edit) keeps its first entry
            var cache = e.Kind == ImageEntryKind.Folder ? state.CacheOf(e) : null;
            items.Add(new ImageItem(e.Name, e.Keywords, e)
            {
                File = e.Kind == ImageEntryKind.Image && e.File is { } f ? LibraryFile(f) : null,
                Cache = cache,
                Types = ImageTypes.Filter(e.FileTypes),
            });
        }

        foreach (var e in state.Entries)
        {
            if (e.Kind != ImageEntryKind.Collection || state.CacheOf(e) is not { } cache)
                continue;
            var types = ImageTypes.Filter(e.FileTypes);
            foreach (var (name, file) in CollectionNaming.Name(cache.Index.Files, e.Prefix, types, taken.Contains))
            {
                taken.Add(name);
                items.Add(new ImageItem(name, e.Keywords, e) { File = file, Cache = cache, Types = types });
            }
        }

        return [.. items];
    }

    /// <summary>A library file as an <see cref="ImageFile"/> (size read once; library files never change).</summary>
    private ImageFile LibraryFile(string file)
    {
        var path = Library.PathOf(file);
        long size;
        lock (_sizes)
        {
            if (!_sizes.TryGetValue(file, out size))
            {
                var info = new FileInfo(path);
                size = info.Exists ? info.Length : -1;
                if (size >= 0)
                    _sizes[file] = size;
            }
        }

        return new ImageFile(path, default, size);
    }

    public void Dispose()
    {
        State state;
        lock (_gate)
        {
            _disposed = true;
            state = _state;
            _state = State.Off;
        }

        _rebuild.Dispose();
        foreach (var c in state.Caches.Values)
            c.Dispose();
        Thumbnails.Dispose();
    }
}
