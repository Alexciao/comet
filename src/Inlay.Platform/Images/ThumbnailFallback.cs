namespace Inlay.Platform.Images;

/// <summary>What a thumbnail request ended up as.</summary>
internal enum ThumbnailKind
{
    /// <summary>The shell made one (<see cref="ThumbnailOutcome.Pixels"/>).</summary>
    Shell,

    /// <summary>The shell had none; the caller decodes the file at thumbnail size.</summary>
    Decode,

    /// <summary>A cloud-only placeholder without a cached thumbnail: a neutral box, never a download.</summary>
    Placeholder,

    /// <summary>Missing, unreadable or too big to decode: the generic image glyph.</summary>
    Glyph,
}

internal sealed record ThumbnailOutcome(ThumbnailKind Kind, ThumbnailPixels? Pixels = null)
{
    public static ThumbnailOutcome Glyph { get; } = new(ThumbnailKind.Glyph);
}

/// <summary>When Inlay decodes a picture itself because the shell's thumbnail cache had nothing.</summary>
internal static class ThumbnailFallback
{
    /// <summary>
    /// Files above this size get the glyph instead of a decode. Decoding scales down while reading, but a PNG or a GIF
    /// still has to be read through; 32 MiB covers photos and screenshots and keeps a stray huge scan from stalling the row.
    /// </summary>
    public const long MaxDecodeBytes = 32L * 1024 * 1024;

    public static ThumbnailKind Decide(bool shellProduced, bool exists, bool cloudOnly, long length)
    {
        if (shellProduced)
            return ThumbnailKind.Shell;
        if (!exists)
            return ThumbnailKind.Glyph;
        if (cloudOnly)
            return ThumbnailKind.Placeholder;
        return length > MaxDecodeBytes ? ThumbnailKind.Glyph : ThumbnailKind.Decode;
    }
}
