using System.Buffers.Binary;
using System.Globalization;
using Inlay.Core.Images;
using Inlay.Core.Snippets;

namespace Inlay.Core.Tests.Images;

/// <summary>Quick Insert captions: image sizes from file headers, and snippet previews.</summary>
public sealed class ImageHeaderTests
{
    private static byte[] Png(int w, int h)
    {
        var d = new byte[33];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(d, 0);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(8), 13);
        "IHDR"u8.CopyTo(d.AsSpan(12));
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(16), w);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(20), h);
        return d;
    }

    private static byte[] Gif(int w, int h)
    {
        var d = new byte[13];
        "GIF89a"u8.CopyTo(d);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(6), (ushort)w);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(8), (ushort)h);
        return d;
    }

    private static byte[] Bmp(int w, int h)
    {
        var d = new byte[54];
        d[0] = (byte)'B';
        d[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(18), w);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(22), -h); // top-down
        return d;
    }

    /// <summary>A JPEG with an APP1 (EXIF-like) block before the frame header.</summary>
    private static byte[] Jpeg(int w, int h)
    {
        var d = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE1, 0x00, 0x10 };
        d.AddRange(new byte[14]);
        d.AddRange([0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w, 0x03]);
        d.AddRange(new byte[9]);
        return [.. d];
    }

    private static byte[] WebPLossless(int w, int h)
    {
        var d = new byte[30];
        "RIFF"u8.CopyTo(d);
        "WEBP"u8.CopyTo(d.AsSpan(8));
        "VP8L"u8.CopyTo(d.AsSpan(12));
        d[20] = 0x2F;
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(21), (uint)((w - 1) | ((h - 1) << 14)));
        return d;
    }

    public static TheoryData<string> Formats => ["png", "gif", "bmp", "jpeg", "webp"];

    [Theory]
    [MemberData(nameof(Formats))]
    public void Reads_the_size_from_the_header(string format)
    {
        var data = format switch
        {
            "png" => Png(240, 180),
            "gif" => Gif(240, 180),
            "bmp" => Bmp(240, 180),
            "jpeg" => Jpeg(240, 180),
            _ => WebPLossless(240, 180),
        };

        Assert.True(ImageHeader.TryReadSize(data, out var w, out var h));
        Assert.Equal((240, 180), (w, h));
    }

    [Fact]
    public void Unknown_or_truncated_data_has_no_size()
    {
        Assert.False(ImageHeader.TryReadSize("not an image"u8, out _, out _));
        Assert.False(ImageHeader.TryReadSize(Png(240, 180).AsSpan(0, 20), out _, out _));
        Assert.False(ImageHeader.TryReadSize(Png(0, 180), out _, out _));
    }

    [Fact]
    public void Row_caption_is_type_and_size()
    {
        Assert.Equal("GIF, 240 × 180", ImageText.Row(ImageType.Gif, 240, 180, CultureInfo.InvariantCulture));
        Assert.Equal("PNG", ImageText.Row(ImageType.Png, 0, 0, CultureInfo.InvariantCulture)); // size not read yet
    }

    [Fact]
    public void Snippet_preview_resolves_what_is_known_now()
    {
        var now = new DateTime(2026, 10, 19, 17, 45, 0);
        var preview = SnippetTemplate.Preview("Sent {date:yyyy-MM-dd} at {time}{cursor} · {weekday} · {clipboard} {{x}}", now, CultureInfo.InvariantCulture);

        Assert.Equal("Sent 2026-10-19 at 17:45 · Monday · {clipboard} {x}", preview);
    }
}
