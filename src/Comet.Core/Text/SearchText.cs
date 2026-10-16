using System.Globalization;
using System.Text;

namespace Comet.Core.Text;

/// <summary>
/// Folds text into the search alphabet: lowercase, diacritics removed, separators dropped.
/// <c>thumbs_up</c>, <c>Thumbs-Up</c> and <c>thumbsup</c> all fold to <c>thumbsup</c>.
/// A leading <c>+</c> or <c>-</c> directly followed by a digit is kept so <c>:+1:</c> and <c>:-1:</c> stay distinct.
/// </summary>
public static class SearchText
{
    public const int MaxLength = 64;

    // Fold table for U+0000..U+024F (Basic Latin through Latin Extended-B). Entries are 0..2 chars.
    private static readonly string[] s_fold = BuildFoldTable();

    private static string[] BuildFoldTable()
    {
        var table = new string[0x250];
        for (var i = 0; i < table.Length; i++)
        {
            var c = (char)i;
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                table[i] = c.ToString();
            }
            else if (c is >= 'A' and <= 'Z')
            {
                table[i] = ((char)(c + 32)).ToString();
            }
            else if (i < 0xC0 || !char.IsLetter(c))
            {
                table[i] = string.Empty;
            }
            else
            {
                table[i] = c switch
                {
                    'ß' => "ss",
                    'Æ' or 'æ' => "ae",
                    'Œ' or 'œ' => "oe",
                    'Ø' or 'ø' => "o",
                    'Đ' or 'đ' or 'Ð' or 'ð' => "d",
                    'Ł' or 'ł' => "l",
                    'Þ' or 'þ' => "th",
                    'ı' => "i",
                    _ => StripMarks(c),
                };
            }
        }

        return table;
    }

    private static string StripMarks(char c)
    {
        var sb = new StringBuilder(2);
        foreach (var d in c.ToString().Normalize(NormalizationForm.FormD))
        {
            if (d is >= 'a' and <= 'z')
                sb.Append(d);
            else if (d is >= 'A' and <= 'Z')
                sb.Append((char)(d + 32));
        }

        return sb.ToString();
    }

    /// <summary>Folds <paramref name="input"/> into <paramref name="output"/>; returns the folded length (truncated to the output size).</summary>
    public static int Fold(ReadOnlySpan<char> input, Span<char> output)
    {
        var n = 0;
        for (var i = 0; i < input.Length && n < output.Length; i++)
        {
            var c = input[i];
            if ((c == '+' || c == '-') && n == 0 && i + 1 < input.Length && char.IsAsciiDigit(input[i + 1]))
            {
                output[n++] = c;
                continue;
            }

            if (c < 0x250)
            {
                var f = s_fold[c];
                for (var j = 0; j < f.Length && n < output.Length; j++)
                    output[n++] = f[j];
            }
            else if (char.IsLetterOrDigit(c))
            {
                output[n++] = char.ToLowerInvariant(c);
            }
        }

        return n;
    }

    public static string Fold(string input)
    {
        Span<char> buffer = stackalloc char[256];
        var n = Fold(input.AsSpan(), buffer);
        return new string(buffer[..n]);
    }

    /// <summary>True for characters that separate words (anything that folds to nothing).</summary>
    public static bool IsSeparator(char c) => c < 0x250 ? s_fold[c].Length == 0 : !char.IsLetterOrDigit(c);

    /// <summary>
    /// Returns the folded suffixes of <paramref name="text"/> that start at each word, e.g.
    /// "face with tears of joy" → facewithtearsofjoy, withtearsofjoy, tearsofjoy, ofjoy, joy.
    /// Prefix-matching these covers both "prefix of any word" and multi-word queries.
    /// </summary>
    public static List<string> WordSuffixes(string text)
    {
        var result = new List<string>();
        var starts = new List<int>();
        var inWord = false;
        for (var i = 0; i < text.Length; i++)
        {
            var sep = IsSeparator(text[i]) && !(i == 0 && (text[i] == '+' || text[i] == '-'));
            if (!sep && !inWord)
                starts.Add(i);
            inWord = !sep;
        }

        foreach (var s in starts)
        {
            var f = Fold(text[s..]);
            if (f.Length > 0 && !result.Contains(f))
                result.Add(f);
        }

        return result;
    }

    /// <summary>Number of user-perceived characters (grapheme clusters) in <paramref name="text"/>.</summary>
    public static int GraphemeCount(ReadOnlySpan<char> text)
    {
        var count = 0;
        while (!text.IsEmpty)
        {
            var len = StringInfo.GetNextTextElementLength(text);
            if (len <= 0)
                break;
            count++;
            text = text[len..];
        }

        return count;
    }
}
