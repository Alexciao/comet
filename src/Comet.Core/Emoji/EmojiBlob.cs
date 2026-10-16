using System.Runtime.InteropServices;

namespace Comet.Core.Emoji;

/// <summary>
/// Layout of the compiled emoji dataset (<c>emoji.bin</c>, also the format of downloaded data packs).
/// The blob is a header followed by sections of blittable records, so it is used in place straight
/// from the embedded resource: nothing is parsed or copied at startup, and untouched pages are never faulted in.
/// </summary>
internal static class EmojiBlob
{
    public const uint Magic = 0x31544D43; // "CMT1"

    /// <summary>Layout version. A data pack with a different format version is ignored until the app is updated.</summary>
    public const int FormatVersion = 1;

    // Header: magic, format version, data version, section count, then (offset, count) per section.
    public const int HeaderFixedSize = 16;

    public enum Section
    {
        Chars,       // char: every string, concatenated
        Emoji,       // EmojiRecord
        Shortcodes,  // StringRef: display shortcodes (e.g. "thumbs_up"), grouped per emoji
        Tones,       // StringRef: 5 skin-tone variants per toned emoji
        Terms,       // TermRecord, sorted ordinally by folded text
        Postings,    // uint: Posting.Pack
        Count,
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct StringRef
{
    public int Offset;
    public int Length;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct EmojiRecord
{
    public StringRef Emoji;
    public StringRef Name;
    public int FirstShortcode;
    public int FirstTone;          // -1 when the emoji has no skin-tone variants
    public ushort ShortcodeCount;
    public ushort Popularity;      // 1 = most used; ushort.MaxValue = unranked
    public ushort Order;           // Unicode/CLDR order
    public byte Group;
    public byte Flags;             // EmojiFlags
}

[Flags]
internal enum EmojiFlags : byte
{
    None = 0,
    /// <summary>A gendered or otherwise derived form of a more basic emoji (ranks after the base).</summary>
    Variant = 1,
    /// <summary>The build machine's Segoe UI Emoji drew it as one glyph (default until the app checks the installed font).</summary>
    RenderedOnReferenceFont = 2,
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct TermRecord
{
    public StringRef Text;
    public int PostingStart;
    public int PostingCount;
}

/// <summary>How a term relates to an emoji. Lower values are stronger.</summary>
public enum TermKind : byte
{
    /// <summary>The term is a whole shortcode or alias (folded).</summary>
    Shortcode = 0,
    /// <summary>A curated everyday word ("love", "yay"); the slot is its preference order. Ranks like an alias.</summary>
    Curated = 1,
    /// <summary>The term is a shortcode suffix starting at a later word ("tears" in face_holding_back_tears).</summary>
    ShortcodeWord = 2,
    /// <summary>The term is a CLDR-name suffix starting at a word.</summary>
    NameWord = 3,
    /// <summary>The term is a keyword (CLDR, emojilib or gemoji tag).</summary>
    Keyword = 4,
}

internal static class Posting
{
    // bits 0..15 emoji index, 16..19 kind, 20..27 slot (shortcode slot, or curated preference order).
    public static uint Pack(int emoji, TermKind kind, int slot) =>
        (uint)emoji | ((uint)kind << 16) | ((uint)Math.Min(slot, 255) << 20);

    public static int Emoji(uint p) => (int)(p & 0xFFFF);

    public static TermKind Kind(uint p) => (TermKind)((p >> 16) & 0xF);

    public static int Slot(uint p) => (int)((p >> 20) & 0xFF);
}
