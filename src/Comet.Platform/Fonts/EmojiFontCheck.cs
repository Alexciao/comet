using System.Runtime.InteropServices;
using System.Text;

namespace Comet.Platform.Fonts;

/// <summary>
/// Which emoji the installed Segoe UI Emoji can draw as a single glyph, measured with DirectWrite through raw COM
/// vtables (AOT-safe). An emoji counts as drawable when every code point has a glyph in the font (ZWJ, variation
/// selectors and tag characters aside) and the whole sequence lays out no wider than 1.25 × a single emoji —
/// the same rule the data builder applies to the reference font, so ZWJ sequences and flags the font doesn't
/// ligate are hidden instead of showing up as two or more pictures. Slot numbers come from dwrite.h / dwrite_2.h.
/// </summary>
internal static unsafe partial class EmojiFontCheck
{
    public const string FontFamily = "Segoe UI Emoji";

    private static readonly Guid IidIDWriteFactory = new("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");

    // IDWriteFactory
    private const int SlotGetSystemFontCollection = 3;
    private const int SlotCreateTextFormat = 15;
    private const int SlotCreateTextLayout = 18;

    // IDWriteFontCollection
    private const int SlotGetFontFamily = 4;
    private const int SlotFindFamilyName = 5;

    // IDWriteFontFamily (IDWriteFontList: 3–5)
    private const int SlotGetFirstMatchingFont = 7;

    // IDWriteFont
    private const int SlotHasCharacter = 12;

    // IDWriteTextLayout (IDWriteTextFormat: 3–27, IDWriteTextLayout: 28–66)
    private const int SlotGetMetrics = 60;

    private const int FontWeightNormal = 400;
    private const int FontStretchNormal = 5;
    private const int FontStyleNormal = 0;
    private const float FontSize = 32;
    private const float MaxWidthRatio = 1.25f;

    [StructLayout(LayoutKind.Sequential)]
    private struct TextMetrics
    {
        public float Left, Top, Width, WidthIncludingTrailingWhitespace, Height, LayoutWidth, LayoutHeight;
        public uint MaxBidiReorderingDepth, LineCount;
    }

    [LibraryImport("dwrite.dll")]
    private static partial int DWriteCreateFactory(int factoryType, Guid* iid, nint* factory);

    private static nint* Vtbl(nint obj) => *(nint**)obj;

    private static void Release(nint obj)
    {
        if (obj != 0)
            ((delegate* unmanaged[Stdcall]<nint, uint>)Vtbl(obj)[2])(obj);
    }

    /// <summary>
    /// One flag per sequence: true when the installed font draws it as one emoji. Null when the font or DirectWrite
    /// is unavailable (callers then keep the data's own flags). Runs in ~0.1 ms per emoji; call off the UI thread.
    /// </summary>
    public static bool[]? Check(IReadOnlyList<string> sequences)
    {
        nint factory = 0, collection = 0, family = 0, font = 0, format = 0;
        try
        {
            var iid = IidIDWriteFactory;
            if (DWriteCreateFactory(0 /* shared */, &iid, &factory) < 0 || factory == 0)
                return null;
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int, int>)Vtbl(factory)[SlotGetSystemFontCollection])(factory, &collection, 0) < 0)
                return null;

            uint index;
            int exists;
            fixed (char* name = FontFamily)
            {
                if (((delegate* unmanaged[Stdcall]<nint, char*, uint*, int*, int>)Vtbl(collection)[SlotFindFamilyName])(collection, name, &index, &exists) < 0 || exists == 0)
                    return null;
            }

            if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Vtbl(collection)[SlotGetFontFamily])(collection, index, &family) < 0
                || ((delegate* unmanaged[Stdcall]<nint, int, int, int, nint*, int>)Vtbl(family)[SlotGetFirstMatchingFont])(family, FontWeightNormal, FontStretchNormal, FontStyleNormal, &font) < 0)
            {
                return null;
            }

            fixed (char* name = FontFamily)
            fixed (char* locale = "en-us")
            {
                if (((delegate* unmanaged[Stdcall]<nint, char*, nint, int, int, int, float, char*, nint*, int>)Vtbl(factory)[SlotCreateTextFormat])(
                        factory, name, collection, FontWeightNormal, FontStyleNormal, FontStretchNormal, FontSize, locale, &format) < 0)
                {
                    return null;
                }
            }

            var reference = Width(factory, format, "😀");
            if (reference <= 0)
                return null;

            var result = new bool[sequences.Count];
            for (var i = 0; i < result.Length; i++)
            {
                var s = sequences[i];
                if (!AllCodePointsInFont(font, s))
                    continue;
                var width = Width(factory, format, s);
                result[i] = width <= reference * MaxWidthRatio && (!IsFlag(s) || width < Width(factory, format, s[..2]) * FlagLigatureRatio);
            }

            return result;
        }
        finally
        {
            Release(format);
            Release(font);
            Release(family);
            Release(collection);
            Release(factory);
        }
    }

    private static bool AllCodePointsInFont(nint font, string sequence)
    {
        foreach (var rune in sequence.EnumerateRunes())
        {
            if (IsInvisibleJoiner(rune.Value))
                continue;
            int exists;
            if (((delegate* unmanaged[Stdcall]<nint, uint, int*, int>)Vtbl(font)[SlotHasCharacter])(font, (uint)rune.Value, &exists) < 0 || exists == 0)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Regional indicator letters can be narrow tiles (Segoe UI Emoji), so an unligated pair may be no wider than one
    /// emoji: a flag counts only when the pair is clearly narrower than its two letters side by side.
    /// </summary>
    private const float FlagLigatureRatio = 1.5f;

    private static bool IsFlag(string s) => s.Length == 4 && char.ConvertToUtf32(s, 0) is >= 0x1F1E6 and <= 0x1F1FF;

    /// <summary>ZWJ, variation selectors and tag characters: shaping input, not glyphs of their own.</summary>
    private static bool IsInvisibleJoiner(int cp) => cp is 0x200D or 0xFE0E or 0xFE0F || (cp >= 0xE0020 && cp <= 0xE007F);

    private static float Width(nint factory, nint format, string text)
    {
        nint layout = 0;
        try
        {
            fixed (char* p = text)
            {
                if (((delegate* unmanaged[Stdcall]<nint, char*, uint, nint, float, float, nint*, int>)Vtbl(factory)[SlotCreateTextLayout])(
                        factory, p, (uint)text.Length, format, 100_000f, 100_000f, &layout) < 0)
                {
                    return float.MaxValue;
                }
            }

            TextMetrics metrics;
            return ((delegate* unmanaged[Stdcall]<nint, TextMetrics*, int>)Vtbl(layout)[SlotGetMetrics])(layout, &metrics) < 0
                ? float.MaxValue
                : metrics.Width;
        }
        finally
        {
            Release(layout);
        }
    }

    /// <summary>Identifies the installed font file, so a cached result is reused only for the same font.</summary>
    public static string FontIdentity()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "seguiemj.ttf");
        var file = new FileInfo(path);
        return file.Exists
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{file.Length}-{file.LastWriteTimeUtc.Ticks}")
            : "missing";
    }

    internal static string Describe(IReadOnlyList<string> sequences, bool[] result)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < result.Length; i++)
        {
            if (!result[i])
                sb.Append(sequences[i]).Append(' ');
        }

        return sb.ToString();
    }
}
