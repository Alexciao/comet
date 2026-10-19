using Inlay.Core.Text;

namespace Inlay.Core.Search;

/// <summary>Finds which part of a displayed shortcode or name the query matched, for highlighting.</summary>
public static class Highlighter
{
    /// <summary>
    /// Returns the (start, length) range of <paramref name="display"/> that the folded query covers when it is a
    /// prefix of the whole text or of one of its words; (0, 0) when the match was elsewhere (keyword, typo).
    /// Separators inside the range are included ("thumbs_up" for "thumbsu").
    /// </summary>
    public static (int Start, int Length) Find(ReadOnlySpan<char> display, ReadOnlySpan<char> query)
    {
        Span<char> q = stackalloc char[SearchText.MaxLength];
        var n = SearchText.Fold(query, q);
        if (n == 0)
            return (0, 0);
        q = q[..n];

        var previousSeparator = true;
        for (var start = 0; start < display.Length; start++)
        {
            var isSeparator = SearchText.FoldChar(display[start]) == '\0' && !(start == 0 && display[start] is '+' or '-');
            if (!isSeparator && previousSeparator)
            {
                var end = MatchFrom(display, start, q);
                if (end > 0)
                    return (start, end - start);
            }

            previousSeparator = isSeparator;
        }

        return (0, 0);
    }

    private static int MatchFrom(ReadOnlySpan<char> display, int start, ReadOnlySpan<char> q)
    {
        var qi = 0;
        for (var i = start; i < display.Length; i++)
        {
            var c = display[i];
            var f = (i == start && c is '+' or '-') ? c : SearchText.FoldChar(c);
            if (f == '\0')
                continue; // separators don't consume query characters
            if (f != q[qi])
                return 0;
            if (++qi == q.Length)
                return i + 1;
        }

        return 0;
    }
}
