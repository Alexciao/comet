using Inlay.Platform.Images;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Inlay.App.UI.Settings.Images;

/// <summary>
/// Thumbnails for the Images page's tiles. Pixels come from the Windows thumbnail cache through
/// <see cref="Thumbnails"/> (off the UI thread, never a full decode); bitmaps are made on the UI thread and kept in a
/// small LRU, so tiles scrolled back into view show at once. A file without a thumbnail isn't asked for again until
/// <see cref="ForgetMissing"/>.
/// </summary>
internal sealed class TileThumbnails(DispatcherQueue dispatcher, Thumbnails source)
{
    /// <summary>About two screens of tiles.</summary>
    private const int Capacity = 80;

    private readonly Dictionary<Key, LinkedListNode<(Key Key, ImageSource Bitmap)>> _map = [];
    private readonly LinkedList<(Key Key, ImageSource Bitmap)> _order = new();
    private readonly Dictionary<Key, List<Action<ImageSource>>> _pending = [];
    private readonly HashSet<Key> _missing = [];

    private readonly record struct Key(string Path, DateTime Version, int Size);

    /// <summary>
    /// The thumbnail at <paramref name="sizePx"/> physical pixels, or null while it is being made (then
    /// <paramref name="arrived"/> gets it on the UI thread) or when there is none.
    /// </summary>
    public ImageSource? Get(string path, DateTime version, int sizePx, Action<ImageSource> arrived)
    {
        var key = new Key(path, version, sizePx);
        if (_map.TryGetValue(key, out var node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
            return node.Value.Bitmap;
        }

        if (_missing.Contains(key))
            return null;
        if (source.TryGetCached(path, sizePx, version) is { } cached)
            return Add(key, cached);
        if (_pending.TryGetValue(key, out var waiting))
        {
            waiting.Add(arrived);
            return null;
        }

        _pending[key] = [arrived];
        _ = source.GetAsync(path, sizePx, version).ContinueWith(
            t => dispatcher.TryEnqueue(() => Arrived(key, t.IsCompletedSuccessfully ? t.Result : null)),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return null;
    }

    /// <summary>Files may have thumbnails now (the page is shown again): ask for them again.</summary>
    public void ForgetMissing() => _missing.Clear();

    private void Arrived(Key key, ThumbnailPixels? pixels)
    {
        if (!_pending.Remove(key, out var waiting))
            return;
        if (pixels is null)
        {
            _missing.Add(key);
            return;
        }

        var bitmap = Add(key, pixels);
        foreach (var callback in waiting)
            callback(bitmap);
    }

    private WriteableBitmap Add(Key key, ThumbnailPixels pixels)
    {
        var bitmap = new WriteableBitmap(pixels.Width, pixels.Height);
        using (var stream = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsStream(bitmap.PixelBuffer))
            stream.Write(pixels.Bgra);
        bitmap.Invalidate();
        _map[key] = _order.AddFirst((key, bitmap));
        if (_map.Count > Capacity && _order.Last is { } last)
        {
            _order.RemoveLast();
            _map.Remove(last.Value.Key);
        }

        return bitmap;
    }
}
