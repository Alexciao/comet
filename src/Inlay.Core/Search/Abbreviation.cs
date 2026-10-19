using Inlay.Core.Text;

namespace Inlay.Core.Search;

/// <summary>
/// Abbreviation matching ("tup" → thumbs_up, "hrt" → heart, "wcm" → white_check_mark).
/// The query's letters must appear in order, starting at the first letter of the text, and every
/// stretch of skipped letters must be vowels only, unless the next matched letter starts a word.
/// That is how people shorten words (drop vowels, take word initials) and it rejects the noise
/// a plain subsequence test produces ("smiel" inside "smiling face with smiling eyes").
/// </summary>
internal static class Abbreviation
{
    private const int MaxText = 96;

    /// <param name="query">Folded query.</param>
    /// <param name="text">Raw display text (shortcode or name), separators included.</param>
    /// <param name="skipped">Number of letters skipped by the best alignment (lower is a tighter match).</param>
    public static bool TryMatch(ReadOnlySpan<char> query, ReadOnlySpan<char> text, out int skipped)
    {
        skipped = 0;
        if (query.Length == 0 || text.IsEmpty || text.Length > MaxText || SearchText.FoldChar(text[0]) != query[0])
            return false;

        // best[qi] for position ti, computed right to left: fewest skipped letters to match query[qi..] when
        // query[qi] is matched at text[ti]. Two rolling arrays over ti keep this allocation-free.
        Span<char> folded = stackalloc char[text.Length];
        Span<bool> wordStart = stackalloc bool[text.Length];
        var prevSeparator = true;
        for (var i = 0; i < text.Length; i++)
        {
            folded[i] = SearchText.FoldChar(text[i]);
            wordStart[i] = folded[i] != '\0' && prevSeparator;
            prevSeparator = folded[i] == '\0';
        }

        var result = Best(query, folded, wordStart, 0, 0);
        if (result < 0)
            return false;
        skipped = result;
        return true;
    }

    // Depth-first with the vowel rule pruning almost every branch; texts are short (≤ 96).
    private static int Best(ReadOnlySpan<char> q, ReadOnlySpan<char> t, ReadOnlySpan<bool> wordStart, int qi, int ti)
    {
        // q[qi] is matched at t[ti].
        if (qi == q.Length - 1)
            return 0;

        var best = -1;
        var skippedLetters = 0;
        var onlyVowels = true;
        for (var k = ti + 1; k < t.Length; k++)
        {
            var c = t[k];
            if (c != '\0' && c == q[qi + 1] && (onlyVowels || wordStart[k]))
            {
                var rest = Best(q, t, wordStart, qi + 1, k);
                if (rest >= 0 && (best < 0 || skippedLetters + rest < best))
                    best = skippedLetters + rest;
                if (best == 0)
                    break;
            }

            if (c != '\0')
            {
                skippedLetters++;
                if (c is not ('a' or 'e' or 'i' or 'o' or 'u'))
                    onlyVowels = false;
            }
        }

        return best;
    }
}
