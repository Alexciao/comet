using System.Buffers.Binary;

namespace Inlay.Core.Images;

/// <summary>
/// Reads an image's pixel size from the start of its file, without decoding it: the PNG, GIF, BMP and WebP headers
/// carry it in the first 30 bytes, JPEG in its first frame marker (after any EXIF block, so give it the first
/// <see cref="BytesNeeded"/> bytes).
/// </summary>
public static class ImageHeader
{
    /// <summary>Enough of a file for every supported format in practice (JPEG frame markers come after EXIF data).</summary>
    public const int BytesNeeded = 64 * 1024;

    public static bool TryReadSize(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = height = 0;
        if (data.Length >= 24 && data[..8].SequenceEqual(PngSignature) && data.Slice(12, 4).SequenceEqual("IHDR"u8))
            return Set(BinaryPrimitives.ReadInt32BigEndian(data[16..]), BinaryPrimitives.ReadInt32BigEndian(data[20..]), out width, out height);
        if (data.Length >= 10 && (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8)))
            return Set(BinaryPrimitives.ReadUInt16LittleEndian(data[6..]), BinaryPrimitives.ReadUInt16LittleEndian(data[8..]), out width, out height);
        if (data.Length >= 26 && data[0] == 'B' && data[1] == 'M')
            return Set(BinaryPrimitives.ReadInt32LittleEndian(data[18..]), Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(data[22..])), out width, out height);
        if (data.Length >= 30 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8))
            return WebP(data, out width, out height);
        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8)
            return Jpeg(data, out width, out height);
        return false;
    }

    private static ReadOnlySpan<byte> PngSignature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    private static bool WebP(ReadOnlySpan<byte> d, out int width, out int height)
    {
        var chunk = d.Slice(12, 4);
        if (chunk.SequenceEqual("VP8 "u8) && d[23] == 0x9D && d[24] == 0x01 && d[25] == 0x2A)
            return Set(BinaryPrimitives.ReadUInt16LittleEndian(d[26..]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(d[28..]) & 0x3FFF, out width, out height);
        if (chunk.SequenceEqual("VP8L"u8) && d[20] == 0x2F)
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(d[21..]);
            return Set((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1, out width, out height);
        }

        if (chunk.SequenceEqual("VP8X"u8))
            return Set(Read24(d[24..]) + 1, Read24(d[27..]) + 1, out width, out height);
        width = height = 0;
        return false;
    }

    private static int Read24(ReadOnlySpan<byte> d) => d[0] | (d[1] << 8) | (d[2] << 16);

    /// <summary>Walks the JPEG markers to the first start-of-frame (SOF0–SOF15 except DHT, JPG and DAC).</summary>
    private static bool Jpeg(ReadOnlySpan<byte> d, out int width, out int height)
    {
        var i = 2;
        while (i + 9 < d.Length)
        {
            if (d[i] != 0xFF)
                break;
            var marker = d[i + 1];
            if (marker == 0xFF)
            {
                i++; // fill byte
                continue;
            }

            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                i += 2; // no length
                continue;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(d[(i + 2)..]);
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                return Set(BinaryPrimitives.ReadUInt16BigEndian(d[(i + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(d[(i + 5)..]), out width, out height);
            if (marker == 0xDA || length < 2)
                break; // image data starts: no frame header before it
            i += 2 + length;
        }

        width = height = 0;
        return false;
    }

    private static bool Set(int w, int h, out int width, out int height)
    {
        (width, height) = w > 0 && h > 0 ? (w, h) : (0, 0);
        return width > 0;
    }
}
