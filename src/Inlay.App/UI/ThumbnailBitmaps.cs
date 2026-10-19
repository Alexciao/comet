using Inlay.Platform.Images;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Inlay.App.UI;

/// <summary>What a row or strip slot shows for an image: the picture, the generic glyph, or (neither) a neutral box.</summary>
public readonly record struct ThumbnailView(ImageSource? Image, bool Glyph)
{
    /// <summary>Still loading, or a cloud-only file that isn't decoded: a neutral box of the same size.</summary>
    public static ThumbnailView Placeholder => default;

    /// <summary>No image to show (missing, unreadable, too big): the generic image glyph.</summary>
    public static ThumbnailView NoImage => new(null, true);
}

/// <summary>
/// Image thumbnails as WinUI bitmaps for Quick Insert rows and the pick strip. Pixels come from the Windows thumbnail
/// cache through <see cref="Thumbnails"/> (off the UI thread, never a full decode). When the shell has none (a file
/// nobody has looked at yet), the file is decoded by <see cref="BitmapImage"/> at thumbnail size only
/// (<c>DecodePixelWidth</c>, the first frame of a GIF), which decodes off the UI thread. Results, including "no image",
/// are kept in a small LRU. A miss returns a placeholder at once and calls <c>arrived</c> when the answer is ready, so
/// a row shows a neutral box of the same size and nothing ever waits or moves.
/// </summary>
internal sealed class ThumbnailBitmaps(DispatcherQueue dispatcher, Action arrived)
{
    private const int Capacity = 200;

    /// <summary>A decoded fallback is this many times the box width: UniformToFill crops a landscape image to the box, so it needs the height.</summary>
    private const int DecodeWidthFactor = 2;

    private readonly Dictionary<Key, LinkedListNode<(Key Key, ThumbnailView View)>> _map = [];
    private readonly LinkedList<(Key Key, ThumbnailView View)> _order = new();
    private readonly HashSet<Key> _pending = [];
    private Thumbnails? _watching;

    private readonly record struct Key(string Path, DateTime Version, int Size);

    /// <summary>The thumbnail at <paramref name="sizePx"/> physical pixels, or a placeholder while it is being made.</summary>
    public ThumbnailView Get(Thumbnails source, string path, DateTime version, int sizePx)
    {
        if (!ReferenceEquals(_watching, source))
        {
            _watching = source;
            source.Invalidated += OnInvalidated;
        }

        var key = new Key(path, version, sizePx);
        if (_map.TryGetValue(key, out var node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
            return node.Value.View;
        }

        if (source.TryGetCached(path, sizePx, version) is { } cached)
            return Add(key, new ThumbnailView(ToBitmap(cached), false));

        if (_pending.Add(key))
        {
            _ = source.ResolveAsync(path, sizePx, version).ContinueWith(
                t => dispatcher.TryEnqueue(() => Resolved(key, t.IsCompletedSuccessfully ? t.Result : ThumbnailOutcome.Glyph)),
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }

        return ThumbnailView.Placeholder;
    }

    private void Resolved(Key key, ThumbnailOutcome outcome)
    {
        switch (outcome.Kind)
        {
            case ThumbnailKind.Shell when outcome.Pixels is { } pixels:
                Finish(key, new ThumbnailView(ToBitmap(pixels), false), show: true);
                break;
            case ThumbnailKind.Decode:
                Finish(key, Decode(key), show: true);
                break;
            case ThumbnailKind.Placeholder:
                Finish(key, ThumbnailView.Placeholder, show: false); // already showing the box
                break;
            default:
                Finish(key, ThumbnailView.NoImage, show: true);
                break;
        }
    }

    /// <summary>
    /// The decoded picture, set to load at thumbnail size. A <see cref="BitmapImage"/> only loads once an Image shows it
    /// (its decode then runs off the UI thread), so it is returned at once: the row's neutral box shows until the
    /// picture is there, and a file that can't be decoded turns into the glyph.
    /// </summary>
    private ThumbnailView Decode(Key key)
    {
        var bitmap = new BitmapImage { DecodePixelWidth = key.Size * DecodeWidthFactor, DecodePixelType = DecodePixelType.Physical, AutoPlay = false };
        bitmap.ImageFailed += (_, _) =>
        {
            if (_map.TryGetValue(key, out var node) && ReferenceEquals(node.Value.View.Image, bitmap))
            {
                _order.Remove(node);
                _map.Remove(key);
                Add(key, ThumbnailView.NoImage);
                arrived();
            }
        };
        bitmap.UriSource = new Uri(key.Path);
        return new ThumbnailView(bitmap, false);
    }

    private void Finish(Key key, ThumbnailView view, bool show)
    {
        if (!_pending.Remove(key))
            return; // cleared or invalidated while it was loading
        Add(key, view);
        if (show)
            arrived();
    }

    private static WriteableBitmap ToBitmap(ThumbnailPixels pixels)
    {
        var bitmap = new WriteableBitmap(pixels.Width, pixels.Height);
        using (var stream = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsStream(bitmap.PixelBuffer))
            stream.Write(pixels.Bgra);
        bitmap.Invalidate();
        return bitmap;
    }

    private ThumbnailView Add(Key key, ThumbnailView view)
    {
        _map[key] = _order.AddFirst((key, view));
        if (_map.Count > Capacity && _order.Last is { } last)
        {
            _order.RemoveLast();
            _map.Remove(last.Value.Key);
        }

        return view;
    }

    /// <summary>The file changed on disk (raised on a pool thread): forget what was made for it.</summary>
    private void OnInvalidated(string path) => dispatcher.TryEnqueue(() =>
    {
        foreach (var key in _map.Keys.Where(k => string.Equals(k.Path, path, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _order.Remove(_map[key]);
            _map.Remove(key);
        }

        _pending.RemoveWhere(k => string.Equals(k.Path, path, StringComparison.OrdinalIgnoreCase));
    });

    /// <summary>Lets go of every bitmap (Images was turned off).</summary>
    public void Clear()
    {
        _map.Clear();
        _order.Clear();
        _pending.Clear();
    }
}
