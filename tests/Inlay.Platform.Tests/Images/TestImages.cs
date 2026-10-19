using System.Buffers.Binary;
using Inlay.Platform.Images;

namespace Inlay.Platform.Tests.Images;

/// <summary>A scratch folder with images generated in code (never the user's files), deleted afterwards.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "inlay-platform-tests", "images", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                System.IO.File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // a watcher or the thumbnail cache may still hold it for a moment; the temp folder is cleaned eventually
        }
    }
}

internal static class TestImages
{
    /// <summary>A <paramref name="w"/>×<paramref name="h"/> gradient with the given alpha, as BGRA.</summary>
    public static BgraImage Gradient(int w, int h, byte alpha = 255, byte seed = 0)
    {
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var o = ((y * w) + x) * 4;
                px[o] = (byte)(x * 255 / Math.Max(1, w - 1));
                px[o + 1] = (byte)(y * 255 / Math.Max(1, h - 1));
                px[o + 2] = (byte)(seed + x + y);
                px[o + 3] = alpha;
            }
        }

        return new BgraImage(w, h, px);
    }

    public static string WritePng(string path, int w = 32, int h = 24, byte seed = 0, DateTime? lastWriteUtc = null)
    {
        File.WriteAllBytes(path, Png.Encode(Gradient(w, h, seed: seed)));
        if (lastWriteUtc is { } t)
            File.SetLastWriteTimeUtc(path, t);
        return path;
    }

    /// <summary>A .bmp file: BITMAPFILEHEADER + the DIBV5 Inlay puts on the clipboard.</summary>
    public static string WriteBmp(string path, BgraImage image)
    {
        var dib = Dib.EncodeV5(image);
        var file = new byte[14 + dib.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(2), file.Length);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(10), 14 + 124);
        dib.CopyTo(file, 14);
        File.WriteAllBytes(path, file);
        return path;
    }

    /// <summary>Polls (tests only) until <paramref name="condition"/> holds; file system events arrive asynchronously.</summary>
    public static async Task<bool> Eventually(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                return false;
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        return true;
    }
}
