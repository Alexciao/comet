using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Inlay.Platform.Images;

/// <summary>Pixels as 32-bit BGRA, top-down rows, straight (not premultiplied) alpha.</summary>
internal sealed record BgraImage(int Width, int Height, byte[] Pixels)
{
    public bool IsOpaque
    {
        get
        {
            for (var i = 3; i < Pixels.Length; i += 4)
            {
                if (Pixels[i] != 255)
                    return false;
            }

            return true;
        }
    }
}

/// <summary>A small PNG writer (8-bit RGB or RGBA, one IDAT): enough for pasted bitmaps, no codec needed.</summary>
internal static class Png
{
    private static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool IsPng(ReadOnlySpan<byte> data) => data.StartsWith(Signature);

    public static byte[] Encode(BgraImage image)
    {
        var opaque = image.IsOpaque;
        var channels = opaque ? 3 : 4;
        var rowBytes = image.Width * channels;
        var raw = new byte[(rowBytes + 1) * image.Height];
        var src = image.Pixels;
        for (int y = 0, o = 0; y < image.Height; y++)
        {
            raw[o++] = 1; // filter "Sub": each byte minus the same channel of the pixel to its left
            var row = y * image.Width * 4;
            for (var x = 0; x < image.Width; x++)
            {
                var s = row + (x * 4);
                var l = s - 4;
                raw[o++] = (byte)(src[s + 2] - (x > 0 ? src[l + 2] : 0));
                raw[o++] = (byte)(src[s + 1] - (x > 0 ? src[l + 1] : 0));
                raw[o++] = (byte)(src[s] - (x > 0 ? src[l] : 0));
                if (!opaque)
                    raw[o++] = (byte)(src[s + 3] - (x > 0 ? src[l + 3] : 0));
            }
        }

        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(raw);

        using var png = new MemoryStream();
        png.Write(Signature);
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], image.Height);
        ihdr[8] = 8;                          // bit depth
        ihdr[9] = (byte)(opaque ? 2 : 6);     // RGB or RGBA
        WriteChunk(png, "IHDR"u8, ihdr);
        WriteChunk(png, "IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        WriteChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        s.Write(word);
        s.Write(type);
        s.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(word, Crc32.Update(Crc32.Update(0xFFFFFFFF, type), data) ^ 0xFFFFFFFF);
        s.Write(word);
    }

    private static class Crc32
    {
        private static readonly uint[] s_table = BuildTable();

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[n] = c;
            }

            return table;
        }

        public static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var b in data)
                crc = s_table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}

/// <summary>Device-independent bitmaps as the clipboard carries them (<c>CF_DIB</c>, <c>CF_DIBV5</c>).</summary>
internal static class Dib
{
    private const int V5HeaderSize = 124;
    private const int BI_RGB = 0;
    private const int BI_BITFIELDS = 3;
    private const int LCS_sRGB = 0x73524742; // 'sRGB'

    /// <summary>
    /// A <c>CF_DIBV5</c>: BITMAPV5HEADER, 32 bits per pixel, BI_BITFIELDS with the alpha mask, bottom-up rows (the
    /// layout apps read most reliably; Firefox and Chromium write the same).
    /// </summary>
    public static byte[] EncodeV5(BgraImage image)
    {
        var stride = image.Width * 4;
        var data = new byte[V5HeaderSize + (stride * image.Height)];
        var h = data.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(h, V5HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(h[4..], image.Width);
        BinaryPrimitives.WriteInt32LittleEndian(h[8..], image.Height); // positive: bottom-up
        BinaryPrimitives.WriteInt16LittleEndian(h[12..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(h[14..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(h[16..], BI_BITFIELDS);
        BinaryPrimitives.WriteInt32LittleEndian(h[20..], stride * image.Height);
        BinaryPrimitives.WriteUInt32LittleEndian(h[40..], 0x00FF0000); // red
        BinaryPrimitives.WriteUInt32LittleEndian(h[44..], 0x0000FF00); // green
        BinaryPrimitives.WriteUInt32LittleEndian(h[48..], 0x000000FF); // blue
        BinaryPrimitives.WriteUInt32LittleEndian(h[52..], 0xFF000000); // alpha
        BinaryPrimitives.WriteInt32LittleEndian(h[56..], LCS_sRGB);
        BinaryPrimitives.WriteInt32LittleEndian(h[108..], 4); // LCS_GM_IMAGES
        for (var y = 0; y < image.Height; y++)
            image.Pixels.AsSpan(y * stride, stride).CopyTo(h[(V5HeaderSize + ((image.Height - 1 - y) * stride))..]);
        return data;
    }

    /// <summary>
    /// Reads a packed DIB (any BITMAPINFOHEADER version; 24 or 32 bits per pixel, BI_RGB or BI_BITFIELDS), or null.
    /// A 32-bit BI_RGB bitmap whose alpha bytes are all zero is opaque (most apps leave that byte unused).
    /// </summary>
    public static BgraImage? Decode(ReadOnlySpan<byte> dib)
    {
        if (dib.Length < 40)
            return null;
        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
        var width = BinaryPrimitives.ReadInt32LittleEndian(dib[4..]);
        var height = BinaryPrimitives.ReadInt32LittleEndian(dib[8..]);
        int bits = BinaryPrimitives.ReadInt16LittleEndian(dib[14..]);
        var compression = BinaryPrimitives.ReadInt32LittleEndian(dib[16..]);
        var colorsUsed = BinaryPrimitives.ReadInt32LittleEndian(dib[32..]);
        var topDown = height < 0;
        height = Math.Abs(height);
        if (headerSize < 40 || headerSize > dib.Length || width <= 0 || height <= 0 || (long)width * height > MaxPixels
            || bits is not (24 or 32) || compression is not (BI_RGB or BI_BITFIELDS) || colorsUsed is < 0 or > 256)
            return null;

        uint r = 0x00FF0000, g = 0x0000FF00, b = 0x000000FF, a = 0;
        var offset = headerSize;
        if (compression == BI_BITFIELDS)
        {
            if (bits != 32)
                return null;
            var masks = headerSize >= 52 ? dib[40..] : dib[headerSize..];
            if (masks.Length < 12)
                return null;
            r = BinaryPrimitives.ReadUInt32LittleEndian(masks);
            g = BinaryPrimitives.ReadUInt32LittleEndian(masks[4..]);
            b = BinaryPrimitives.ReadUInt32LittleEndian(masks[8..]);
            a = headerSize >= 56 ? BinaryPrimitives.ReadUInt32LittleEndian(dib[52..]) : 0;
            if (headerSize == 40)
                offset += 12;
        }

        offset += colorsUsed * 4;
        var stride = ((width * bits) + 31) / 32 * 4;
        if (offset + ((long)stride * height) > dib.Length)
            return null;

        var pixels = new byte[width * height * 4];
        var anyAlpha = false;
        for (var y = 0; y < height; y++)
        {
            var row = dib.Slice(offset + ((topDown ? y : height - 1 - y) * stride), stride);
            for (var x = 0; x < width; x++)
            {
                var o = ((y * width) + x) * 4;
                if (bits == 24)
                {
                    pixels[o] = row[x * 3];
                    pixels[o + 1] = row[(x * 3) + 1];
                    pixels[o + 2] = row[(x * 3) + 2];
                    pixels[o + 3] = 255;
                    continue;
                }

                var v = BinaryPrimitives.ReadUInt32LittleEndian(row[(x * 4)..]);
                pixels[o] = Channel(v, b);
                pixels[o + 1] = Channel(v, g);
                pixels[o + 2] = Channel(v, r);
                pixels[o + 3] = compression == BI_RGB ? (byte)(v >> 24) : a == 0 ? (byte)255 : Channel(v, a);
                anyAlpha |= pixels[o + 3] != 0;
            }
        }

        if (bits == 32 && !anyAlpha)
        {
            for (var i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255;
        }

        return new BgraImage(width, height, pixels);
    }

    /// <summary>Bigger bitmaps are refused rather than allocated (about 400 MB of pixels).</summary>
    public const long MaxPixels = 100_000_000;

    private static byte Channel(uint value, uint mask)
    {
        if (mask == 0)
            return 0;
        var shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        var max = mask >> shift;
        var v = (value & mask) >> shift;
        return max == 255 ? (byte)v : (byte)(v * 255 / max);
    }
}

/// <summary><c>CF_HDROP</c>: a DROPFILES header and a double-null-terminated list of UTF-16 paths.</summary>
internal static class DropFiles
{
    private const int HeaderSize = 20;

    public static byte[] Encode(IEnumerable<string> paths)
    {
        var list = string.Concat(paths.Select(p => p + "\0")) + "\0";
        var data = new byte[HeaderSize + Encoding.Unicode.GetByteCount(list)];
        BinaryPrimitives.WriteInt32LittleEndian(data, HeaderSize); // pFiles
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), 1); // fWide
        Encoding.Unicode.GetBytes(list, data.AsSpan(HeaderSize));
        return data;
    }

    public static List<string> Decode(ReadOnlySpan<byte> data)
    {
        var files = new List<string>();
        if (data.Length < HeaderSize)
            return files;
        var start = BinaryPrimitives.ReadInt32LittleEndian(data);
        var wide = BinaryPrimitives.ReadInt32LittleEndian(data[16..]) != 0;
        if (start < HeaderSize || start >= data.Length)
            return files;
        var text = wide ? Encoding.Unicode.GetString(data[start..]) : Encoding.Default.GetString(data[start..]);
        foreach (var path in text.Split('\0'))
        {
            if (path.Length == 0)
                break;
            files.Add(path);
        }

        return files;
    }
}
