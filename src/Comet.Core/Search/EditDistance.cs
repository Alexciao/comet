namespace Comet.Core.Search;

/// <summary>Bounded string distances used for typo tolerance. Allocation-free.</summary>
internal static class EditDistance
{
    private const int MaxQuery = 32;

    /// <summary>
    /// Optimal-string-alignment distance (insert, delete, substitute, adjacent transpose) between
    /// <paramref name="query"/> and the closest prefix of <paramref name="term"/>, so a query typed
    /// part-way ("thnumbs") still matches "thumbsup". Returns <c>max + 1</c> as soon as the bound is exceeded.
    /// </summary>
    public static int PrefixDistance(ReadOnlySpan<char> query, ReadOnlySpan<char> term, int max)
    {
        var n = query.Length;
        if (n == 0)
            return 0;
        if (n > MaxQuery)
            return max + 1;

        // Rows are indexed by term position j (0..m), columns by query position i (0..n).
        // We only need the last column per row, and the minimum of that over all rows.
        var m = Math.Min(term.Length, n + max);
        Span<int> prev2 = stackalloc int[MaxQuery + 1];
        Span<int> prev = stackalloc int[MaxQuery + 1];
        Span<int> cur = stackalloc int[MaxQuery + 1];
        for (var i = 0; i <= n; i++)
            prev[i] = i;

        var best = prev[n];
        for (var j = 1; j <= m; j++)
        {
            cur[0] = j;
            var rowMin = cur[0];
            var tj = term[j - 1];
            for (var i = 1; i <= n; i++)
            {
                var cost = query[i - 1] == tj ? 0 : 1;
                var v = Math.Min(Math.Min(prev[i] + 1, cur[i - 1] + 1), prev[i - 1] + cost);
                if (i > 1 && j > 1 && query[i - 1] == term[j - 2] && query[i - 2] == tj)
                    v = Math.Min(v, prev2[i - 2] + 1);
                cur[i] = v;
                if (v < rowMin)
                    rowMin = v;
            }

            if (cur[n] < best)
                best = cur[n];
            if (rowMin > max)
                break; // every alignment through this row already costs more than allowed

            var t = prev2;
            prev2 = prev;
            prev = cur;
            cur = t;
        }

        return best <= max ? best : max + 1;
    }

    /// <summary>
    /// True when the first <c>query.Length</c> characters of <paramref name="term"/> are a permutation of
    /// <paramref name="query"/> with the same first letter ("hreat" → "heart"): scrambles that cost
    /// two edits but read as one slip of the fingers.
    /// </summary>
    public static bool IsScrambledPrefix(ReadOnlySpan<char> query, ReadOnlySpan<char> term)
    {
        if (term.Length < query.Length || query.Length < 4 || query[0] != term[0])
            return false;

        Span<int> counts = stackalloc int[37];
        for (var i = 0; i < query.Length; i++)
        {
            var a = Bucket(query[i]);
            var b = Bucket(term[i]);
            if (a < 0 || b < 0)
                return false;
            counts[a]++;
            counts[b]--;
        }

        foreach (var c in counts)
        {
            if (c != 0)
                return false;
        }

        return true;
    }

    private static int Bucket(char c) => c switch
    {
        >= 'a' and <= 'z' => c - 'a',
        >= '0' and <= '9' => 26 + (c - '0'),
        _ => -1,
    };
}
