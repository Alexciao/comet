using System.Collections.Concurrent;
using Inlay.Platform.Native;

namespace Inlay.Platform.Context;

/// <summary>An icon as premultiplied BGRA pixels, ready for a WriteableBitmap.</summary>
internal sealed record IconPixels(int Size, byte[] Bgra);

/// <summary>
/// Program icons for the Excluded apps lists. Extraction is Win32 work that can take a few milliseconds per file,
/// so call it off the UI thread; results are cached per file and size for the life of the process (a few KB each).
/// </summary>
internal static unsafe class AppIcons
{
    private static readonly ConcurrentDictionary<(string Path, int Size), IconPixels?> s_cache = new();

    /// <summary>The icon of an executable at <paramref name="size"/> pixels, or the generic program icon, or null.</summary>
    public static IconPixels? ForFile(string? path, int size)
    {
        var key = (path ?? "", size);
        if (s_cache.TryGetValue(key, out var cached))
            return cached;
        var pixels = (path is { Length: > 0 } && File.Exists(path) ? Extract(path, size) : null)
            ?? Extract(Path.Combine(Environment.SystemDirectory, "imageres.dll"), size, GenericProgramIconIndex);
        s_cache[key] = pixels;
        return pixels;
    }

    /// <summary>imageres.dll's "application" icon, which Explorer shows for programs without one of their own.</summary>
    private const int GenericProgramIconIndex = 11;

    private static IconPixels? Extract(string file, int size, int index = 0)
    {
        if (IconApi.SHDefExtractIconW(file, index, 0, out var icon, 0, (uint)size) != 0 || icon == 0)
            return null;
        try
        {
            return ToPixels(icon);
        }
        finally
        {
            User32.DestroyIcon(icon);
        }
    }

    private static IconPixels? ToPixels(nint icon)
    {
        if (!IconApi.GetIconInfo(icon, out var info))
            return null;
        try
        {
            var color = Read(info.hbmColor, out var width, out var height);
            if (color is null || width != height)
                return null;
            var hasAlpha = false;
            for (var i = 3; i < color.Length && !hasAlpha; i += 4)
                hasAlpha = color[i] != 0;
            if (!hasAlpha)
            {
                // An old-style icon: transparency lives in the AND mask (white = transparent).
                var mask = Read(info.hbmMask, out _, out _);
                for (var i = 0; i < color.Length; i += 4)
                    color[i + 3] = mask is not null && mask[i] != 0 ? (byte)0 : (byte)255;
            }

            for (var i = 0; i < color.Length; i += 4)
            {
                var a = color[i + 3];
                if (a == 255)
                    continue;
                color[i] = (byte)(color[i] * a / 255);
                color[i + 1] = (byte)(color[i + 1] * a / 255);
                color[i + 2] = (byte)(color[i + 2] * a / 255);
            }

            return new IconPixels(width, color);
        }
        finally
        {
            if (info.hbmColor != 0)
                Gdi32.DeleteObject(info.hbmColor);
            if (info.hbmMask != 0)
                Gdi32.DeleteObject(info.hbmMask);
        }
    }

    /// <summary>A bitmap's pixels as 32-bit top-down BGRA.</summary>
    private static byte[]? Read(nint bitmap, out int width, out int height)
    {
        width = height = 0;
        if (bitmap == 0)
            return null;
        var dc = IconApi.GetDC(0);
        try
        {
            var header = new BITMAPINFOHEADER { biSize = sizeof(BITMAPINFOHEADER) };
            if (Gdi32.GetDIBits(dc, bitmap, 0, 0, null, &header, Gdi32.DIB_RGB_COLORS) == 0)
                return null;
            width = header.biWidth;
            height = Math.Abs(header.biHeight);
            if (width <= 0 || height <= 0 || width > 256 || height > 256)
                return null;
            header = new BITMAPINFOHEADER { biSize = sizeof(BITMAPINFOHEADER), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
            var pixels = new byte[width * height * 4];
            fixed (byte* p = pixels)
            {
                if (Gdi32.GetDIBits(dc, bitmap, 0, (uint)height, p, &header, Gdi32.DIB_RGB_COLORS) == 0)
                    return null;
            }

            return pixels;
        }
        finally
        {
            _ = IconApi.ReleaseDC(0, dc);
        }
    }
}
