using System.IO.Enumeration;
using Inlay.Core.Images;
using Inlay.Platform.Native;

namespace Inlay.Platform.Images;

/// <summary>
/// The image files of one folder an entry uses: enumerated once in the background, then kept current by a
/// <see cref="FileSystemWatcher"/> (event-driven, no polling). Reads go to <see cref="Index"/>, which never touches the
/// disk, so Quick Insert never waits on a folder. A watcher overflow (too many changes at once) or error rescans in the
/// background. Files are described from their directory entries only and never opened, so OneDrive cloud-only
/// placeholders are listed (flagged <see cref="ImageFile.CloudOnly"/>) without being downloaded.
/// </summary>
internal sealed class FolderCache : IDisposable
{
    private const int MaxDepth = 16; // subfolders: a guard against junction loops
    private readonly Lock _gate = new();
    private readonly bool _watch;
    private FileSystemWatcher? _watcher;
    private FileSystemWatcher? _parentWatcher;
    private bool _scanning;
    private bool _rescan;
    private List<string> _pending = [];
    private Task _scan = Task.CompletedTask;
    private bool _disposed;

    /// <param name="watch">False only in tests: no watcher, so changes are seen through <see cref="Rescan"/> alone.</param>
    public FolderCache(string path, bool recursive, bool watch = true)
    {
        FolderPath = path;
        Recursive = recursive;
        _watch = watch;
        Index.Changed += () => Changed?.Invoke();
    }

    public string FolderPath { get; }

    public bool Recursive { get; }

    public FolderIndex Index { get; } = new();

    /// <summary>Raised on a pool thread after the list changed (once per event; the service debounces).</summary>
    public event Action? Changed;

    /// <summary>The scan in progress (or the last one): tests wait on it.</summary>
    internal Task Scanning
    {
        get
        {
            lock (_gate)
                return _scan;
        }
    }

    /// <summary>Starts watching and the first scan, in the background.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
        }

        if (!Directory.Exists(FolderPath))
        {
            // Not there yet (Screenshots before the first Win+PrtScn): wait for it to appear.
            Index.Reset([]);
            WatchParent();
            return;
        }

        if (_watch)
            StartWatcher();
        Rescan();
    }

    /// <summary>A cloud placeholder whose content isn't local: opening it would download it.</summary>
    public static bool IsCloudOnly(FileAttributes attributes) =>
        ((uint)attributes & (ImageApi.FILE_ATTRIBUTE_OFFLINE | ImageApi.FILE_ATTRIBUTE_RECALL_ON_OPEN | ImageApi.FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS)) != 0;

    /// <summary>Enumerates the folder again in the background (coalesced with a scan already running).</summary>
    public void Rescan()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            if (_scanning)
            {
                _rescan = true;
                return;
            }

            _scanning = true;
            _scan = Task.Run(ScanLoop);
        }
    }

    /// <summary>The watcher's buffer overflowed: some events are lost, so the list is rebuilt.</summary>
    internal void OnOverflow()
    {
        Log.Warn("images: too many changes at once in a watched folder; rescanning");
        Rescan();
    }

    private void ScanLoop()
    {
        while (true)
        {
            List<ImageFile> files;
            try
            {
                files = Enumerate();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Log.Warn($"images: can't read a watched folder ({ex.GetType().Name})");
                files = [];
            }

            Index.Reset(files);
            List<string> pending;
            lock (_gate)
            {
                pending = _pending;
                _pending = [];
                if (_rescan && !_disposed)
                {
                    _rescan = false;
                    continue;
                }

                _scanning = false;
            }

            // Changes reported while the scan ran may predate what it saw: check each of them again.
            foreach (var path in pending.Distinct(StringComparer.OrdinalIgnoreCase))
                Apply(path);
            return;
        }
    }

    private List<ImageFile> Enumerate()
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = Recursive,
            MaxRecursionDepth = MaxDepth,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };
        // Directory entries only: the attributes, time and size come from the enumeration, no file is opened.
        var entries = new FileSystemEnumerable<ImageFile>(
            FolderPath,
            static (ref FileSystemEntry e) => new ImageFile(e.ToFullPath(), e.LastWriteTimeUtc.UtcDateTime, e.Length, IsCloudOnly(e.Attributes)),
            options)
        {
            ShouldIncludePredicate = static (ref FileSystemEntry e) => !e.IsDirectory && ImageTypes.IsSupported(e.FileName),
        };
        return [.. entries];
    }

    /// <summary>Brings one path up to date from its directory entry (metadata only).</summary>
    private void Apply(string path)
    {
        lock (_gate)
        {
            if (_scanning)
            {
                _pending.Add(path);
                return;
            }
        }

        if (!ImageTypes.IsSupported(path))
            return;
        var info = new FileInfo(path);
        try
        {
            if (!info.Exists || (info.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.Directory)) != 0)
                Index.Remove(path);
            else
                Index.Upsert(new ImageFile(path, info.LastWriteTimeUtc, info.Length, IsCloudOnly(info.Attributes)));
        }
        catch (IOException)
        {
            Index.Remove(path);
        }
    }

    private void StartWatcher()
    {
        var w = new FileSystemWatcher(FolderPath)
        {
            IncludeSubdirectories = Recursive,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes
                | (Recursive ? NotifyFilters.DirectoryName : 0),
            InternalBufferSize = 64 * 1024,
        };
        w.Created += (_, e) => OnCreated(e.FullPath);
        w.Changed += (_, e) => Apply(e.FullPath);
        w.Deleted += (_, e) => OnDeleted(e.FullPath);
        w.Renamed += (_, e) => OnRenamed(e.OldFullPath, e.FullPath);
        w.Error += (_, e) => OnError(e.GetException());
        w.EnableRaisingEvents = true;
        lock (_gate)
        {
            if (_disposed)
            {
                w.Dispose();
                return;
            }

            _watcher = w;
        }
    }

    private void OnCreated(string path)
    {
        if (ImageTypes.IsSupported(path))
            Apply(path);
        else if (Recursive && Directory.Exists(path))
            Rescan(); // a folder moved in reports only itself, not its files
    }

    private void OnDeleted(string path)
    {
        if (ImageTypes.IsSupported(path))
            Apply(path);
        else if (Recursive)
            Index.RemoveUnder(path);
    }

    private void OnRenamed(string oldPath, string newPath)
    {
        if (ImageTypes.IsSupported(oldPath))
            Apply(oldPath);
        if (ImageTypes.IsSupported(newPath))
            Apply(newPath);
        else if (Recursive && Directory.Exists(newPath))
        {
            Index.RemoveUnder(oldPath);
            Rescan();
        }
    }

    private void OnError(Exception ex)
    {
        if (ex is InternalBufferOverflowException)
        {
            OnOverflow();
            return;
        }

        // The folder went away (deleted, drive removed) or the watcher broke: start over.
        Log.Warn($"images: folder watcher failed ({ex.GetType().Name}); restarting it");
        StopWatchers();
        Start();
    }

    private void WatchParent()
    {
        var parent = Path.GetDirectoryName(FolderPath);
        if (!_watch || parent is null || !Directory.Exists(parent))
            return;
        var w = new FileSystemWatcher(parent, Path.GetFileName(FolderPath)) { NotifyFilter = NotifyFilters.DirectoryName };
        w.Created += (_, _) => Appeared();
        w.Renamed += (_, _) => Appeared();
        w.EnableRaisingEvents = true;
        lock (_gate)
        {
            if (_disposed)
            {
                w.Dispose();
                return;
            }

            _parentWatcher = w;
        }
    }

    private void Appeared()
    {
        if (!Directory.Exists(FolderPath))
            return;
        StopWatchers();
        Start();
    }

    private void StopWatchers()
    {
        FileSystemWatcher? w, p;
        lock (_gate)
        {
            (w, p) = (_watcher, _parentWatcher);
            (_watcher, _parentWatcher) = (null, null);
        }

        w?.Dispose();
        p?.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
            _disposed = true;
        StopWatchers();
    }
}
