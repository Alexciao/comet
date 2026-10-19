using System.Buffers.Binary;
using Inlay.Platform.Images;

namespace Inlay.Platform.Tests.Images;

/// <summary>PNG, DIB and CF_HDROP data as Inlay writes and reads it (no clipboard involved).</summary>
public sealed class ImageFormatsTests
{
    [Theory]
    [InlineData(255)] // opaque: written as RGB
    [InlineData(128)] // translucent: RGBA
    public void Png_written_by_Inlay_decodes_to_the_same_pixels(byte alpha)
    {
        using var dir = new TempFolder();
        var image = TestImages.Gradient(37, 19, alpha);
        var path = dir.File("a.png");
        File.WriteAllBytes(path, Png.Encode(image));
        var decoded = ImageCodec.Decode(path);
        Assert.NotNull(decoded);
        Assert.Equal((37, 19), (decoded.Width, decoded.Height));
        Assert.Equal(image.Pixels, decoded.Pixels);
    }

    [Fact]
    public void Dibv5_round_trips_and_is_bottom_up_with_an_alpha_mask()
    {
        var image = TestImages.Gradient(5, 3, 200);
        var dib = Dib.EncodeV5(image);
        Assert.Equal(124, BinaryPrimitives.ReadInt32LittleEndian(dib));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8)));        // positive height: bottom-up
        Assert.Equal(0xFF000000u, BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(52)));
        Assert.Equal(image.Pixels.AsSpan(2 * 5 * 4, 5 * 4).ToArray(), dib.AsSpan(124, 5 * 4).ToArray()); // first stored row = last image row
        var back = Dib.Decode(dib);
        Assert.NotNull(back);
        Assert.Equal(image.Pixels, back.Pixels);
    }

    [Fact]
    public void A_24_bit_dib_from_another_app_reads_as_opaque()
    {
        // 2×2, BITMAPINFOHEADER, bottom-up, rows padded to 4 bytes.
        var dib = new byte[40 + (8 * 2)];
        BinaryPrimitives.WriteInt32LittleEndian(dib, 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), 2);
        BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(14), 24);
        dib[40] = 0xFF;          // bottom-left pixel: blue
        dib[48 + 5] = 0xFF;      // top-right pixel: red
        var image = Dib.Decode(dib)!;
        Assert.Equal([0, 0, 0, 255, 0, 0, 255, 255], image.Pixels[..8]);    // top row: black, red
        Assert.Equal([255, 0, 0, 255], image.Pixels[8..12]);                 // bottom-left: blue
        Assert.True(image.IsOpaque);
    }

    [Fact]
    public void A_32_bit_bi_rgb_dib_with_no_alpha_is_opaque()
    {
        var dib = Dib.EncodeV5(TestImages.Gradient(4, 4, alpha: 0));
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(16), 0); // BI_RGB: the fourth byte is unused
        Assert.True(Dib.Decode(dib)!.IsOpaque);
    }

    [Fact]
    public void Garbage_is_not_a_dib()
    {
        Assert.Null(Dib.Decode([1, 2, 3]));
        var dib = Dib.EncodeV5(TestImages.Gradient(4, 4));
        Assert.Null(Dib.Decode(dib.AsSpan(0, 130)));
    }

    [Fact]
    public void Hdrop_lists_the_files()
    {
        string[] files = [@"C:\Users\me\Pictures\lgtm.gif", @"D:\päth with spaces\😀.png"];
        Assert.Equal(files, DropFiles.Decode(DropFiles.Encode(files)));
    }
}
