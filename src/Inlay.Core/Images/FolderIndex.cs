namespace Inlay.Core.Images;

/// <summary>One image file in a watched folder, as its directory entry describes it (the file is never opened).</summary>
/// <param name="CloudOnly">
/// A cloud placeholder whose content isn't on this PC (OneDrive "free up space"): opening it would download it.
/// Folder modes skip it; collections keep it (its name needs no content).
/// </param>
public sealed record ImageFile(string Path, DateTime LastWriteUtc, long Size, bool CloudOnly = false)
{
    public ImageType Type { get; } = ImageTypes.FromPath(Path);

    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// The image files of one folder, newest first (by last-write time), kept up to date from file system events.
/// Writers copy on write under a lock; readers take <see cref="Files"/> without locking, so Quick Insert can read
/// it on the keyboard-hook thread. Nothing here touches the disk.
/// </summary>
public sealed class FolderIndex
{
    private readonly Lock _gate = new();
    private ImageFile[] _files = [];
    private bool _loaded;

    /// <summary>Raised (on the writer's thread) after every change, outside the lock.</summary>
    public event Action? Changed;

    /// <summary>The current snapshot, newest first. Never modified: every change publishes a new array.</summary>
    public IReadOnlyList<ImageFile> Files => Volatile.Read(ref _files);

    /// <summary>True once the first full scan has been applied.</summary>
    public bool Loaded => Volatile.Read(ref _loaded);

    /// <summary>Replaces everything (a full scan). Unsupported types are dropped.</summary>
    public void Reset(IEnumerable<ImageFile> files)
    {
        var sorted = files.Where(f => f.Type != ImageType.None).DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        Array.Sort(sorted, Newer);
        lock (_gate)
        {
            Volatile.Write(ref _files, sorted);
            Volatile.Write(ref _loaded, true);
        }

        Changed?.Invoke();
    }

    /// <summary>Adds a file, or moves it to its new place when it changed. Unsupported types are ignored.</summary>
    public void Upsert(ImageFile file)
    {
        if (file.Type == ImageType.None)
            return;
        lock (_gate)
        {
            var current = _files;
            var existing = IndexOf(current, file.Path);
            if (existing >= 0 && current[existing] == file)
                return;
            var list = new List<ImageFile>(current.Length + 1);
            list.AddRange(current);
            if (existing >= 0)
                list.RemoveAt(existing);
            var at = list.BinarySearch(file, Comparer<ImageFile>.Create(Newer));
            list.Insert(at < 0 ? ~at : at, file);
            Volatile.Write(ref _files, list.ToArray());
        }

        Changed?.Invoke();
    }

    /// <summary>Removes a file; returns false when it wasn't listed.</summary>
    public bool Remove(string path)
    {
        lock (_gate)
        {
            var current = _files;
            var i = IndexOf(current, path);
            if (i < 0)
                return false;
            var next = new ImageFile[current.Length - 1];
            Array.Copy(current, next, i);
            Array.Copy(current, i + 1, next, i, current.Length - i - 1);
            Volatile.Write(ref _files, next);
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>Removes every file under <paramref name="directory"/> (a deleted or moved-away subfolder).</summary>
    public int RemoveUnder(string directory)
    {
        var prefix = directory.EndsWith(System.IO.Path.DirectorySeparatorChar) ? directory : directory + System.IO.Path.DirectorySeparatorChar;
        int removed;
        lock (_gate)
        {
            var current = _files;
            var next = Array.FindAll(current, f => !f.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            removed = current.Length - next.Length;
            if (removed > 0)
                Volatile.Write(ref _files, next);
        }

        if (removed > 0)
            Changed?.Invoke();
        return removed;
    }

    /// <summary>A rename: the old name goes, the new one (if it is still an image) comes in.</summary>
    public void Rename(string oldPath, ImageFile? renamed)
    {
        Remove(oldPath);
        if (renamed is not null)
            Upsert(renamed);
    }

    /// <summary>Newest first; equal times by path, so the order is the same on every scan.</summary>
    private static int Newer(ImageFile a, ImageFile b)
    {
        var byTime = b.LastWriteUtc.CompareTo(a.LastWriteUtc);
        return byTime != 0 ? byTime : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
    }

    private static int IndexOf(ImageFile[] files, string path)
    {
        for (var i = 0; i < files.Length; i++)
        {
            if (string.Equals(files[i].Path, path, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }
}

/// <summary>What a Folder entry resolves to, from a <see cref="FolderIndex"/> snapshot. Cloud-only files are skipped.</summary>
public static class FolderResolve
{
    /// <summary>The newest file of the given types, or null.</summary>
    public static ImageFile? Newest(IReadOnlyList<ImageFile> files, ImageType types)
    {
        foreach (var f in files)
        {
            if (Usable(f, types))
                return f;
        }

        return null;
    }

    /// <summary>The newest <paramref name="count"/> files of the given types (the Let me pick strip).</summary>
    public static IReadOnlyList<ImageFile> Newest(IReadOnlyList<ImageFile> files, ImageType types, int count)
    {
        var result = new List<ImageFile>(Math.Min(count, files.Count));
        foreach (var f in files)
        {
            if (result.Count == count)
                break;
            if (Usable(f, types))
                result.Add(f);
        }

        return result;
    }

    /// <summary>A random file of the given types, or null.</summary>
    public static ImageFile? Random(IReadOnlyList<ImageFile> files, ImageType types, Random random)
    {
        var count = 0;
        foreach (var f in files)
        {
            if (Usable(f, types))
                count++;
        }

        if (count == 0)
            return null;
        var target = random.Next(count);
        foreach (var f in files)
        {
            if (Usable(f, types) && target-- == 0)
                return f;
        }

        return null;
    }

    private static bool Usable(ImageFile f, ImageType types) => !f.CloudOnly && (f.Type & types) != 0;
}
