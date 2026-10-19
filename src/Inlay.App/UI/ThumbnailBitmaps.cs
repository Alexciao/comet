using Inlay.Platform.Images;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Inlay.App.UI;

/// <summary>
/// Image thumbnails as WinUI bitmaps for Quick Insert rows and the pick strip. Pixels come from the Windows thumbnail
/// cache through <see cref="Thumbnails"/> (off the UI thread, never a full decode); bitmaps are made on the UI thread
/// and kept in a small LRU. A miss returns null at once and calls <c>arrived</c> when the thumbnail is ready, so a
/// row shows an image icon for a moment and nothing ever waits.
/// </summary>
internal sealed class ThumbnailBitmaps(DispatcherQueue dispatcher, Action arrived)
{
    private const int Capacity = 200;

    private readonly Dictionary<(string Path, DateTime Version, int Size), LinkedListNode<((string, DateTime, int) Key, ImageSource Bitmap)>> _map = [];
    private readonly LinkedList<((string, DateTime, int) Key, ImageSource Bitmap)> _order = new();
    private readonly HashSet<(string, DateTime, int)> _pending = [];

    /// <summary>The thumbnail at <paramref name="sizePx"/> physical pixels, or null while it is being made.</summary>
    public ImageSource? Get(Thumbnails source, string path, DateTime version, int sizePx)
    {
        var key = (path, version, sizePx);
        if (_map.TryGetValue(key, out var node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
            return node.Value.Bitmap;
        }

        if (source.TryGetCached(path, sizePx, version) is { } cached)
            return Add(key, cached);

        if (_pending.Add(key))
        {
            _ = source.GetAsync(path, sizePx, version).ContinueWith(
                t => dispatcher.TryEnqueue(() =>
                {
                    _pending.Remove(key);
                    if (t.IsCompletedSuccessfully && t.Result is { } pixels)
                    {
                        Add(key, pixels);
                        arrived();
                    }
                }),
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }

        return null;
    }

    private WriteableBitmap Add((string, DateTime, int) key, ThumbnailPixels pixels)
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

    /// <summary>Lets go of every bitmap (Images was turned off).</summary>
    public void Clear()
    {
        _map.Clear();
        _order.Clear();
    }
}
