using Comet.Core.Emoji;
using Comet.Core.Text;

namespace Comet.Core.Search;

/// <summary>Match quality, best first. Results are always ordered by tier before anything else.</summary>
public enum MatchTier : byte
{
    /// <summary>The query is a whole shortcode, alias or snippet name.</summary>
    Exact = 0,
    /// <summary>The query starts a shortcode or alias (also: exact keyword of a very common emoji).</summary>
    ShortcodePrefix = 1,
    /// <summary>The query starts a word of the shortcode or CLDR name.</summary>
    NameWordPrefix = 2,
    KeywordExact = 3,
    KeywordPrefix = 4,
    /// <summary>Abbreviation: the query's letters appear in order ("tup" → thumbsup).</summary>
    Subsequence = 5,
    /// <summary>Within 1–2 edits of a prefix ("smiel" → smile).</summary>
    Typo = 6,
    None = 255,
}

public enum ResultKind : byte
{
    Emoji,
    Snippet,
}

/// <param name="Slot">For emoji: the shortcode slot to display (the alias that matched, or 0).</param>
public readonly record struct SearchResult(ResultKind Kind, int Index, MatchTier Tier, int Slot, int Score);

/// <summary>
/// Ranks emoji and snippets for a <c>:query</c>. One instance per dataset; not thread-safe (it reuses
/// scratch buffers so a search allocates nothing). A search over the full set takes well under 1 ms.
/// </summary>
public sealed class SearchEngine
{
    /// <summary>Cost per position in a curated word's list: enough to keep the curated order over popularity.</summary>
    private const int CuratedStep = 50;

    private const int TierStride = 1 << 22;
    private const int MaxResults = 64;
    private const int MaxCost = 300;

    private readonly EmojiData? _emoji;
    private SnippetIndex _snippets;

    // Per-emoji scratch, valid when _stamp[i] == _generation.
    private readonly int[] _stamp;
    private readonly MatchTier[] _tier;
    private readonly int[] _cost;
    private readonly byte[] _slot;
    private readonly int[] _touched;
    private int _touchedCount;
    private int _generation;

    private readonly bool[] _visible;
    private readonly float[] _emojiFrecency;
    private float[] _snippetFrecency;

    // Snippet scratch.
    private MatchTier[] _snippetTier = [];
    private int[] _snippetCost = [];

    public SearchEngine(EmojiData? emoji, SnippetIndex? snippets)
    {
        _emoji = emoji;
        _snippets = snippets ?? SnippetIndex.Empty;
        var n = emoji?.Count ?? 0;
        _stamp = new int[n];
        _tier = new MatchTier[n];
        _cost = new int[n];
        _slot = new byte[n];
        _touched = new int[n];
        _emojiFrecency = new float[n];
        _visible = new bool[n];
        for (var i = 0; i < n; i++)
            _visible[i] = emoji!.RenderedOnReferenceFont(i);
        _snippetFrecency = new float[_snippets.Count];
        ResizeSnippetScratch();
    }

    public EmojiData? Emoji => _emoji;

    public SnippetIndex Snippets => _snippets;

    public void SetSnippets(SnippetIndex snippets, float[]? frecency = null)
    {
        _snippets = snippets;
        _snippetFrecency = frecency is { } f && f.Length == snippets.Count ? f : new float[snippets.Count];
        ResizeSnippetScratch();
    }

    private void ResizeSnippetScratch()
    {
        _snippetTier = new MatchTier[_snippets.Count];
        _snippetCost = new int[_snippets.Count];
    }

    /// <summary>Sets the frecency score (≥ 0; ~1 per recent pick, decaying) used to boost an emoji within its tier.</summary>
    public void SetEmojiFrecency(int index, float score) => _emojiFrecency[index] = score;

    public void SetSnippetFrecency(int index, float score)
    {
        if ((uint)index < (uint)_snippetFrecency.Length)
            _snippetFrecency[index] = score;
    }

    /// <summary>
    /// Shows or hides an emoji (hidden emoji never match). Defaults to whether the build machine's font drew it;
    /// the app replaces that with a check against the installed Segoe UI Emoji.
    /// </summary>
    public void SetVisible(int index, bool visible) => _visible[index] = visible;

    public bool IsVisible(int index) => _visible[index];

    public void ClearFrecency()
    {
        Array.Clear(_emojiFrecency);
        Array.Clear(_snippetFrecency);
    }

    /// <summary>
    /// Finds the whole-shortcode or snippet-name match for <paramref name="query"/> (used by <c>:code:</c>
    /// and space-to-commit). Snippets win over emoji because the user defined them.
    /// </summary>
    public SearchResult? FindExact(ReadOnlySpan<char> query)
    {
        Span<char> q = stackalloc char[SearchText.MaxLength];
        var n = SearchText.Fold(query, q);
        if (n == 0)
            return null;
        q = q[..n];

        var s = _snippets.FindByFoldedName(q);
        if (s >= 0)
            return new SearchResult(ResultKind.Snippet, s, MatchTier.Exact, 0, 0);

        if (_emoji is null)
            return null;
        var terms = _emoji.Terms;
        var t = LowerBound(terms, q);
        if (t < terms.Length && _emoji.Text(terms[t].Text).SequenceEqual(q))
        {
            var postings = _emoji.Postings.Slice(terms[t].PostingStart, terms[t].PostingCount);
            var best = -1;
            for (var k = 0; k < postings.Length; k++)
            {
                if (Posting.Kind(postings[k]) == TermKind.Shortcode && _visible[Posting.Emoji(postings[k])] && (best < 0 || Posting.Slot(postings[k]) < Posting.Slot(postings[best])))
                    best = k;
            }

            if (best >= 0)
                return new SearchResult(ResultKind.Emoji, Posting.Emoji(postings[best]), MatchTier.Exact, Posting.Slot(postings[best]), 0);
        }

        return null;
    }

    /// <summary>Fills <paramref name="results"/> with the best matches, best first. Returns how many were written.</summary>
    public int Search(ReadOnlySpan<char> query, Span<SearchResult> results)
    {
        if (results.IsEmpty)
            return 0;
        if (results.Length > MaxResults)
            results = results[..MaxResults];

        Span<char> qbuf = stackalloc char[SearchText.MaxLength];
        var n = SearchText.Fold(query, qbuf);
        if (n == 0)
            return 0;
        var q = qbuf[..n];

        BeginSearch();
        var strongMatches = 0;
        if (_emoji is not null)
            strongMatches = MatchTerms(q);
        var snippetStrong = MatchSnippets(q);

        // Subsequence and typo matches only fill the list; skip them when real matches already do.
        if (_emoji is not null && strongMatches + snippetStrong < results.Length)
        {
            if (n >= 3)
                MatchSubsequences(q);
            if (n >= 4)
                MatchTypos(q);
        }

        return Collect(results);
    }

    private void BeginSearch()
    {
        _touchedCount = 0;
        if (++_generation == int.MaxValue)
        {
            Array.Clear(_stamp);
            _generation = 1;
        }

        Array.Fill(_snippetTier, MatchTier.None);
    }

    /// <summary>Records a candidate match, keeping the best (tier, cost) per emoji.</summary>
    private void Offer(int emoji, MatchTier tier, int cost, int slot)
    {
        if (!_visible[emoji])
            return;
        if (_stamp[emoji] != _generation)
        {
            _stamp[emoji] = _generation;
            _tier[emoji] = tier;
            _cost[emoji] = cost;
            _slot[emoji] = (byte)slot;
            _touched[_touchedCount++] = emoji;
            return;
        }

        if (tier < _tier[emoji] || (tier == _tier[emoji] && cost < _cost[emoji]))
        {
            _tier[emoji] = tier;
            _cost[emoji] = cost;
            _slot[emoji] = (byte)slot;
        }
    }

    private bool Matched(int emoji) => _stamp[emoji] == _generation;

    // ---- tiers 0..4: binary search the sorted term index for every term starting with the query ----

    private int MatchTerms(ReadOnlySpan<char> q)
    {
        var data = _emoji!;
        var terms = data.Terms;
        var postings = data.Postings;
        for (var t = LowerBound(terms, q); t < terms.Length; t++)
        {
            var text = data.Text(terms[t].Text);
            if (!text.StartsWith(q))
                break;
            var exact = text.Length == q.Length;
            var extra = text.Length - q.Length;
            foreach (var p in postings.Slice(terms[t].PostingStart, terms[t].PostingCount))
            {
                var e = Posting.Emoji(p);
                switch (Posting.Kind(p))
                {
                    case TermKind.Shortcode:
                        Offer(e, exact ? MatchTier.Exact : MatchTier.ShortcodePrefix, extra, Posting.Slot(p));
                        break;
                    case TermKind.Curated:
                        // Curated everyday words behave like aliases, in their curated order.
                        Offer(e, exact ? MatchTier.Exact : MatchTier.ShortcodePrefix, extra + (Posting.Slot(p) * CuratedStep), 0);
                        break;
                    case TermKind.ShortcodeWord:
                        Offer(e, MatchTier.NameWordPrefix, extra, Posting.Slot(p));
                        break;
                    case TermKind.NameWord:
                        Offer(e, MatchTier.NameWordPrefix, extra, 0);
                        break;
                    default:
                        Offer(e, exact ? MatchTier.KeywordExact : MatchTier.KeywordPrefix, extra, 0);
                        break;
                }
            }
        }

        return _touchedCount;
    }

    private int LowerBound(ReadOnlySpan<TermRecord> terms, ReadOnlySpan<char> q)
    {
        int lo = 0, hi = terms.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (_emoji!.Text(terms[mid].Text).SequenceCompareTo(q) < 0)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    // ---- tier 5: abbreviations over folded shortcodes and names ----

    private void MatchSubsequences(ReadOnlySpan<char> q)
    {
        var data = _emoji!;
        for (var e = 0; e < data.Count; e++)
        {
            if (Matched(e) || !_visible[e])
                continue;
            var count = data.ShortcodeCount(e);
            for (var slot = 0; slot < count; slot++)
            {
                var s = data.Shortcode(e, slot);
                if (Abbreviation.TryMatch(q, s, out var skipped))
                    Offer(e, MatchTier.Subsequence, (skipped * 2) + s.Length, slot);
            }

            var name = data.Name(e);
            if (Abbreviation.TryMatch(q, name, out var nameSkipped))
                Offer(e, MatchTier.Subsequence, (nameSkipped * 2) + name.Length, 0);
        }
    }

    // ---- tier 6: typos, run only over terms that survive cheap length / first-letter filters ----

    private void MatchTypos(ReadOnlySpan<char> q)
    {
        var data = _emoji!;
        var max = q.Length >= 8 ? 2 : 1;
        var terms = data.Terms;
        var postings = data.Postings;
        var q0 = q[0];
        var q1 = q[1];
        for (var t = 0; t < terms.Length; t++)
        {
            var text = data.Text(terms[t].Text);
            if (text.Length < q.Length - max)
                continue;
            var t0 = text[0];
            // An edit budget of 1–2 can only disturb the first letter in a few ways; most terms fail here.
            if (t0 != q0 && t0 != q1 && (text.Length < 2 || (text[1] != q0 && text[1] != q1)))
                continue;

            // A scramble that keeps the first letter ("hreat") reads as one slip, so it costs like one edit.
            int distance;
            if (EditDistance.IsScrambledPrefix(q, text))
                distance = 1;
            else if ((distance = EditDistance.PrefixDistance(q, text, max)) > max)
                continue;

            // Fewer edits first, then whole words over long prefixes; a changed first letter is a rarer slip.
            var cost = (distance * 24) + (Math.Max(0, text.Length - q.Length) * 3) + (t0 != q0 ? 12 : 0);
            foreach (var p in postings.Slice(terms[t].PostingStart, terms[t].PostingCount))
            {
                var e = Posting.Emoji(p);
                if (!Matched(e) || _tier[e] == MatchTier.Typo)
                    Offer(e, MatchTier.Typo, cost + TypoKindCost(Posting.Kind(p)), Posting.Kind(p) == TermKind.Shortcode ? Posting.Slot(p) : 0);
            }
        }
    }

    private static int TypoKindCost(TermKind kind) => kind switch
    {
        TermKind.Shortcode => 0,
        TermKind.Curated => 2,
        TermKind.ShortcodeWord or TermKind.NameWord => 8,
        _ => 24, // keywords are looser than names: a typo on one costs like an extra edit
    };

    // ---- snippets: same tiers, brute force over the (small) user list ----

    private int MatchSnippets(ReadOnlySpan<char> q)
    {
        var strong = 0;
        for (var i = 0; i < _snippets.Count; i++)
        {
            ref readonly var s = ref _snippets[i];
            var tier = MatchTier.None;
            var cost = 0;
            if (s.FoldedName.AsSpan().StartsWith(q))
            {
                tier = s.FoldedName.Length == q.Length ? MatchTier.Exact : MatchTier.ShortcodePrefix;
                cost = s.FoldedName.Length - q.Length;
            }
            else if (FindPrefix(s.NameSuffixes, q, out var len))
            {
                tier = MatchTier.NameWordPrefix;
                cost = len - q.Length;
            }
            else if (FindPrefix(s.TextWords, q, out len))
            {
                tier = len == q.Length ? MatchTier.KeywordExact : MatchTier.KeywordPrefix;
                cost = len - q.Length;
            }
            else if (q.Length >= 3 && Abbreviation.TryMatch(q, s.Name, out var skipped))
            {
                tier = MatchTier.Subsequence;
                cost = (skipped * 2) + s.Name.Length;
            }
            else if (q.Length >= 4)
            {
                var max = q.Length >= 8 ? 2 : 1;
                var d = EditDistance.IsScrambledPrefix(q, s.FoldedName) ? 2 : EditDistance.PrefixDistance(q, s.FoldedName, max);
                if (d <= max)
                {
                    tier = MatchTier.Typo;
                    cost = d * 16;
                }
            }

            _snippetTier[i] = tier;
            _snippetCost[i] = cost;
            if (tier <= MatchTier.KeywordPrefix)
                strong++;
        }

        return strong;
    }

    private static bool FindPrefix(string[] words, ReadOnlySpan<char> q, out int length)
    {
        length = int.MaxValue;
        foreach (var w in words)
        {
            if (w.Length < length && w.AsSpan().StartsWith(q))
                length = w.Length;
        }

        return length != int.MaxValue;
    }

    // ---- ranking ----

    /// <summary>
    /// Score within a tier (lower is better): frecency is the strongest signal, then how much of the
    /// matched term is left untyped (shorter names first), popularity, and base emoji before variants.
    /// </summary>
    private int EmojiScore(int e)
    {
        var tier = _tier[e];
        var data = _emoji!;
        var score = (int)tier * TierStride;
        score += 1_000_000;
        score -= FrecencyBoost(_emojiFrecency[e]);
        score += Math.Min(_cost[e], MaxCost) * 3_000;
        score += PopularityPenalty(data.Popularity(e));
        if (data.IsVariant(e))
            score += 40_000;
        return score;
    }

    private int SnippetScore(int i)
    {
        var tier = _snippetTier[i];
        var score = (int)tier * TierStride;
        // Snippets the user named go above emoji at the same name-match tier.
        score += tier <= MatchTier.NameWordPrefix ? 0 : 1_000_000;
        score -= FrecencyBoost(_snippetFrecency[i]);
        score += Math.Min(_snippetCost[i], MaxCost) * 3_000;
        return score;
    }

    private static int FrecencyBoost(float f) => f <= 0 ? 0 : (int)(Math.Min(f, 10f) * 60_000) + 200_000;

    // log-scaled rank: the top emoji differ a lot, the long tail hardly at all.
    private static int PopularityPenalty(int rank) =>
        rank >= ushort.MaxValue ? 130_000 : (int)(Math.Log2(rank + 1) * 10_000);

    private int Collect(Span<SearchResult> results)
    {
        var count = 0;
        for (var k = 0; k < _touchedCount; k++)
        {
            var e = _touched[k];
            Insert(results, ref count, new SearchResult(ResultKind.Emoji, e, _tier[e], _slot[e], EmojiScore(e)));
        }

        for (var i = 0; i < _snippetTier.Length; i++)
        {
            if (_snippetTier[i] != MatchTier.None)
                Insert(results, ref count, new SearchResult(ResultKind.Snippet, i, _snippetTier[i], 0, SnippetScore(i)));
        }

        return count;
    }

    /// <summary>Insertion into a small sorted buffer: cheaper than sorting all candidates.</summary>
    private static void Insert(Span<SearchResult> results, ref int count, SearchResult r)
    {
        if (count == results.Length && !Better(r, results[count - 1]))
            return;
        var i = count < results.Length ? count++ : count - 1;
        while (i > 0 && Better(r, results[i - 1]))
        {
            results[i] = results[i - 1];
            i--;
        }

        results[i] = r;
    }

    private static bool Better(in SearchResult a, in SearchResult b) =>
        a.Score != b.Score ? a.Score < b.Score : a.Kind != b.Kind ? a.Kind == ResultKind.Snippet : a.Index < b.Index;
}
