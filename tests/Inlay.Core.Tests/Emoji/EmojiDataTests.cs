using Inlay.Core.Emoji;
using Inlay.Core.Tests.Search;

namespace Inlay.Core.Tests.Emoji;

public sealed class EmojiDataTests
{
    [Fact]
    public void Embedded_data_loads_with_a_sane_shape()
    {
        using var data = EmojiData.LoadEmbedded();
        Assert.Equal(EmojiData.SupportedFormatVersion, data.FormatVersion);
        Assert.True(data.DataVersion > 2026000000);
        Assert.InRange(data.Count, 1800, 4000);
        var thumbs = data.IndexOf("👍");
        Assert.True(thumbs >= 0);
        Assert.Equal("thumbsup", data.Shortcode(thumbs, 0).ToString());
        Assert.True(data.HasSkinTones(thumbs));
    }

    [Fact]
    public void Round_trip_preserves_header_and_records()
    {
        var bytes = EmojiBlobWriter.Write([SearchEngineTests.Entry("😀", "grinning face", ["grinning"], ["smile"])], 2026101601);
        Assert.True(EmojiData.TryReadHeader(bytes, out var format, out var version));
        Assert.Equal(EmojiData.SupportedFormatVersion, format);
        Assert.Equal(2026101601, version);
        using var data = EmojiData.FromBytes(bytes);
        Assert.Equal("grinning face", data.Name(0).ToString());
    }

    [Fact]
    public void Truncated_or_garbled_blobs_fail_to_load()
    {
        var bytes = EmojiBlobWriter.Write([SearchEngineTests.Entry("😀", "grinning face", ["grinning"])], 1);
        Assert.Throws<InvalidDataException>(() => EmojiData.FromBytes(bytes.AsSpan(0, bytes.Length / 2)));
        Assert.Throws<InvalidDataException>(() => EmojiData.FromBytes(new byte[64]));

        var wrongFormat = (byte[])bytes.Clone();
        wrongFormat[4] = 99;
        Assert.Throws<InvalidDataException>(() => EmojiData.FromBytes(wrongFormat));

        // Corrupt a section offset so a record points outside the string pool.
        var garbled = (byte[])bytes.Clone();
        BitConverter.GetBytes(int.MaxValue / 2).CopyTo(garbled, 16);
        Assert.Throws<InvalidDataException>(() => EmojiData.FromBytes(garbled));
    }

    [Fact]
    public void Random_corruption_never_crashes_the_reader()
    {
        var bytes = EmojiBlobWriter.Write([SearchEngineTests.Entry("😀", "grinning face", ["grinning"], ["smile", "happy"])], 1);
        var rng = new Random(42);
        for (var i = 0; i < 2000; i++)
        {
            var copy = (byte[])bytes.Clone();
            for (var k = 0; k < 4; k++)
                copy[rng.Next(copy.Length)] = (byte)rng.Next(256);
            try
            {
                using var data = EmojiData.FromBytes(copy);
                // Loaded: every accessor must stay in bounds.
                for (var e = 0; e < data.Count; e++)
                {
                    _ = data.Emoji(e);
                    _ = data.Name(e);
                    for (var s = 0; s < data.ShortcodeCount(e); s++)
                        _ = data.Shortcode(e, s);
                }
            }
            catch (InvalidDataException)
            {
                // Rejected: fine.
            }
        }
    }
}
