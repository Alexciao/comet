using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Comet.App.UI;

/// <summary>
/// Pre-rendered emoji bitmaps for the popup. Measured with WinUI 3: a color emoji in a TextBlock costs ~1 ms per
/// frame to draw even when its glyph is cached, and 10–25 ms the first time; eight rows of them blew the 16 ms
/// budget. An Image of a pre-rendered bitmap costs next to nothing, so each emoji is rasterized once (at the
/// popup's scale, from an offscreen stage in the popup itself) and reused. Popular emoji are rendered ahead of
/// time at idle priority; a miss shows the TextBlock for that update and queues the bitmap.
/// </summary>
internal sealed class EmojiBitmapCache
{
    private const int Capacity = 600;

    private readonly Panel _stage;
    private readonly DispatcherQueue _dispatcher;
    private readonly Dictionary<string, (ImageSource Source, long Used)> _bitmaps = new(StringComparer.Ordinal);
    private readonly Queue<string> _pending = new();
    private readonly HashSet<string> _queued = new(StringComparer.Ordinal);
    private double _scale;
    private bool _working;
    private long _clock;
    private long _batchStarted;
    private bool _warmedUp;

    public EmojiBitmapCache(Panel stage, DispatcherQueue dispatcher)
    {
        _stage = stage;
        _dispatcher = dispatcher;
    }

    public int Count => _bitmaps.Count;

    /// <summary>The bitmap for <paramref name="emoji"/> at <paramref name="scale"/>, or null (then it is queued).</summary>
    public ImageSource? Get(string emoji, double scale)
    {
        if (_scale == 0)
        {
            _scale = scale;
        }
        else if (Math.Abs(scale - _scale) > 0.001)
        {
            // Monitor DPI changed: bitmaps for the old scale would be blurry.
            _scale = scale;
            _bitmaps.Clear();
        }

        if (_bitmaps.TryGetValue(emoji, out var entry))
        {
            _bitmaps[emoji] = (entry.Source, ++_clock);
            return entry.Source;
        }

        Request(emoji, urgent: true);
        return null;
    }

    /// <summary>Queues emoji to render when the UI thread is idle (startup warm-up, frecent picks).</summary>
    public void Prefetch(IEnumerable<string> emoji)
    {
        foreach (var e in emoji)
            Request(e, urgent: false);
    }

    private void Request(string emoji, bool urgent)
    {
        if (_bitmaps.ContainsKey(emoji) || !_queued.Add(emoji))
            return;
        _pending.Enqueue(emoji);
        if (!_working)
        {
            _working = true;
            _batchStarted = Environment.TickCount64;
            _dispatcher.TryEnqueue(urgent ? DispatcherQueuePriority.Normal : DispatcherQueuePriority.Low, RenderNext);
        }
    }

    private async void RenderNext()
    {
        if (!_pending.TryDequeue(out var emoji))
        {
            _working = false;
            // The startup warm-up once at Info (tests and PERF.md wait for it); later top-ups only in Debug builds.
            var message = $"emoji bitmaps ready: {_bitmaps.Count} in {Environment.TickCount64 - _batchStarted} ms";
            if (_warmedUp)
                Comet.Platform.Log.Debug(message);
            else
                Comet.Platform.Log.Info(message);
            _warmedUp = true;
            return;
        }

        try
        {
            var text = new TextBlock
            {
                Text = emoji,
                FontFamily = new FontFamily("Segoe UI Emoji"),
                FontSize = PopupRow.EmojiFontSize,
                IsColorFontEnabled = true,
                IsTextScaleFactorEnabled = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var frame = new Grid { Width = PopupRow.EmojiBox, Height = PopupRow.EmojiBox, Children = { text } };
            Canvas.SetLeft(frame, -4 * PopupRow.EmojiBox); // outside the visible area; rendering doesn't need it on screen
            _stage.Children.Add(frame);
            var bitmap = new RenderTargetBitmap();
            try
            {
                await bitmap.RenderAsync(frame);
            }
            finally
            {
                _stage.Children.Remove(frame); // also when rendering fails (device lost): never leave it on stage
            }
            if (_scale == 0)
                _scale = _stage.XamlRoot?.RasterizationScale ?? 1;
            if (_bitmaps.Count >= Capacity)
                Evict();
            _bitmaps[emoji] = (bitmap, ++_clock);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Rendering can fail while the window is being torn down; the TextBlock fallback still works.
        }
        finally
        {
            _queued.Remove(emoji);
        }

        // One emoji per dispatcher turn keeps the UI thread responsive between renders.
        _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, RenderNext);
    }

    private void Evict()
    {
        var oldest = _bitmaps.MinBy(kv => kv.Value.Used).Key;
        _bitmaps.Remove(oldest);
    }
}
