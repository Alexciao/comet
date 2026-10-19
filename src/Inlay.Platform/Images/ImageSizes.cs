using Inlay.Core.Images;

namespace Inlay.Platform.Images;

/// <summary>
/// Pixel sizes of image files for Quick Insert's captions, read from the file header off the UI thread and kept in a
/// small LRU keyed by path and last-write time. Cloud-only placeholders are never opened (that would download them).
/// </summary>
internal sealed class ImageSizes
{
    private const int Capacity = 512;

    private readonly Lock _gate = new();
    private readonly Dictionary<(string Path, DateTime Version), LinkedListNode<Entry>> _map = [];
    private readonly LinkedList<Entry> _order = new();
    private readonly HashSet<(string Path, DateTime Version)> _pending = [];

    private readonly record struct Entry((string Path, DateTime Version) Key, int Width, int Height);

    /// <summary>The size when it has been read; (0, 0) when unknown or unreadable.</summary>
    public bool TryGet(string path, DateTime version, out int width, out int height)
    {
        lock (_gate)
        {
            if (_map.TryGetValue((path, version), out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                (width, height) = (node.Value.Width, node.Value.Height);
                return true;
            }
        }

        width = height = 0;
        return false;
    }

    /// <summary>Reads the size in the background (once per file version) and calls <paramref name="done"/> on a pool thread.</summary>
    public void Request(string path, DateTime version, bool cloudOnly, Action done)
    {
        var key = (path, version);
        lock (_gate)
        {
            if (_map.ContainsKey(key) || !_pending.Add(key))
                return;
        }

        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            var (w, h) = cloudOnly ? (0, 0) : Read(path);
            lock (_gate)
            {
                _pending.Remove(key);
                if (!_map.ContainsKey(key))
                {
                    _map[key] = _order.AddFirst(new Entry(key, w, h));
                    if (_map.Count > Capacity && _order.Last is { } last)
                    {
                        _order.RemoveLast();
                        _map.Remove(last.Value.Key);
                    }
                }
            }

            done();
        }, null);
    }

    private static (int Width, int Height) Read(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            var buffer = new byte[(int)Math.Min(file.Length, ImageHeader.BytesNeeded)];
            var read = file.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return ImageHeader.TryReadSize(buffer.AsSpan(0, read), out var w, out var h) ? (w, h) : (0, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (0, 0);
        }
    }
}
