using System.Diagnostics;
using Inlay.Platform.Native;

namespace Inlay.Platform.Input;

internal sealed partial class TextInjector
{
    /// <summary>After an image paste the app read: further formats follow within milliseconds, so this is a margin.</summary>
    internal const int ImageRestoreAfterReadMs = 500;

    /// <summary>
    /// After an image paste nothing read before Ctrl went up: the app may still read it (some paste asynchronously),
    /// so the clipboard stays this long before the user's comes back.
    /// </summary>
    internal const int ImageRestoreUnreadMs = 2000;

    internal const int MaxImageRestoreMs = 5000;

    /// <summary>Per app (process name): the latest read seen after Ctrl+V, in ms. Injector thread only.</summary>
    private readonly Dictionary<string, int> _imageReadMs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Pastes an image: the formats for its Insert as and method go on the clipboard with delayed rendering (nothing
    /// is read or decoded until the app asks), the token is erased and Ctrl+V sent. WM_RENDERFORMAT tells when and
    /// what the app read; the user's clipboard comes back once insertions pause, after a per-app delay learned from
    /// those reads (some apps read a pasted image well after Ctrl+V).
    /// </summary>
    private uint PasteImage(InsertionJob job, ImageInsertion image)
    {
        var formats = ImagePasteMethod.Formats(image);
        var withFile = formats.Contains(ImageFormat.File);
        var data = new ImagePasteData(image.Path, withFile ? ImagePasteData.Stage(image.Path, image.DisplayName) : null);
        _clipboardToRestore ??= SaveClipboard();
        if (_clipboardToRestore is null || !OpenClipboardRetry())
        {
            // The token stays as typed; only the key that ended it goes through.
            Log.Warn("clipboard busy; the image was not inserted");
            image.Result = new([], -1);
            return job.After is { } a ? _batch.Clear().ReleaseModifiers().Boundary(a).RestoreModifiers().Send() : 0;
        }

        var ids = formats.Select(ClipboardFormat).ToArray();
        PendingRender render;
        try
        {
            User32.EmptyClipboard();
            _render = render = new PendingRender(image, ids, f => data.Render(FormatOf(f)));
            foreach (var id in ids)
                User32.SetClipboardData(id, 0);
            if (withFile)
                SetData(_dropEffectFormat, [1, 0, 0, 0]); // DROPEFFECT_COPY: pasting in Explorer copies, never moves the file
            MarkPrivate();
        }
        finally
        {
            User32.CloseClipboard();
        }

        var accepted = _batch.Clear().ReleaseModifiers().Backspaces(job.DeleteCount).PressCtrlV().Send();
        render.Pasted();

        // As for text, Ctrl stays down until the app reads (or a second passes).
        PumpUntil(() => render.Read, TimeSpan.FromMilliseconds(1000));
        accepted += _batch.Clear().ReleaseCtrl().Send();
        _batch.Clear();
        if (job.After is { } after)
            _batch.Boundary(after);
        accepted += _batch.RestoreModifiers().Send();

        image.Result = new([.. render.Reads.Select(r => ImagePasteMethod.Name(FormatOf(r.Format)))], render.Read ? render.Reads[0].Ms : -1);
        _restoreDelayMs = Math.Max(_restoreDelayMs, ImageRestoreDelay(image.Process, render.Read));
        if (!render.Read)
            Log.Warn($"image paste: {image.Process} read nothing within a second ({image.Method}, {image.InsertAs})");
        return accepted;
    }

    /// <summary>How long the pasted image stays on the clipboard once insertions pause.</summary>
    private int ImageRestoreDelay(string process, bool read)
    {
        var fallback = read ? ImageRestoreAfterReadMs : ImageRestoreUnreadMs;
        return _imageReadMs.TryGetValue(process, out var seen)
            ? Math.Clamp(Math.Max(seen + ImageRestoreAfterReadMs, fallback), ClipboardRestoreDelayMs, MaxImageRestoreMs)
            : fallback;
    }

    /// <summary>Remembers how late the app read, for the next paste's restore delay, and logs the reads (the measurement).</summary>
    private void LearnReadTime(ImageInsertion image, PendingRender render)
    {
        if (render.Reads.Count == 0)
        {
            Log.Info($"image paste: {image.Process} never read the image");
            return;
        }

        var last = render.Reads[^1].Ms;
        _imageReadMs[image.Process] = Math.Max(last, _imageReadMs.GetValueOrDefault(image.Process));
        Log.Info($"image paste: {image.Process} read {string.Join(", ", render.Reads.Select(r => $"{ImagePasteMethod.Name(FormatOf(r.Format))} at {r.Ms} ms"))}");
    }

    private uint ClipboardFormat(ImageFormat format) => format switch
    {
        ImageFormat.File => ImageApi.CF_HDROP,
        ImageFormat.Png => _pngFormat,
        _ => ImageApi.CF_DIBV5,
    };

    private ImageFormat FormatOf(uint format) =>
        format == ImageApi.CF_HDROP ? ImageFormat.File : format == _pngFormat ? ImageFormat.Png : ImageFormat.DibV5;

    /// <summary>What Inlay put on the clipboard with delayed rendering, and the reads the app made (ms after Ctrl+V).</summary>
    private sealed class PendingRender(ImageInsertion? image, uint[] formats, Func<uint, byte[]?> render)
    {
        private long _pasted = Stopwatch.GetTimestamp();

        public ImageInsertion? Image => image;

        public uint[] Formats => formats;

        public List<(uint Format, int Ms)> Reads { get; } = [];

        public bool Read => Reads.Count > 0;

        public void Pasted() => _pasted = Stopwatch.GetTimestamp();

        public byte[]? Render(uint format)
        {
            Reads.Add((format, (int)Stopwatch.GetElapsedTime(_pasted).TotalMilliseconds));
            return render(format);
        }
    }
}
