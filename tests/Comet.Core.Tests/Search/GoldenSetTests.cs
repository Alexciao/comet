using System.Globalization;
using System.Text;
using Comet.Core.Emoji;
using Comet.Core.Search;

namespace Comet.Core.Tests.Search;

/// <summary>
/// Runs tests/search-golden.tsv against the embedded dataset and fails when the hit rates drop below the targets.
/// </summary>
public sealed class GoldenSetTests
{
    // Targets from the spec; raise them (never lower) when the measured rates improve.
    private const double MinTop1 = 0.85;
    private const double MinTop3 = 0.95;

    [Fact]
    public void Golden_set_meets_hit_rate_targets()
    {
        using var data = EmojiData.LoadEmbedded();
        var engine = new SearchEngine(data, null);
        var report = GoldenSet.Evaluate(engine, data, GoldenSet.Load());

        TestContext.Current.TestOutputHelper?.WriteLine(report.Describe());
        Assert.True(report.Top1Rate >= MinTop1 && report.Top3Rate >= MinTop3, report.Describe());
    }
}

internal sealed record GoldenCase(string Query, string Emoji, int Within, string Note);

internal sealed record GoldenReport(int Total, int Top1Cases, int Top1Hits, int Top3Hits, IReadOnlyList<string> Misses)
{
    /// <summary>Share of the cases marked "1" whose emoji came first.</summary>
    public double Top1Rate => (double)Top1Hits / Top1Cases;

    /// <summary>Share of all cases whose emoji is in the first three.</summary>
    public double Top3Rate => (double)Top3Hits / Total;

    public string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"golden: {Total} cases, top-1 {Top1Rate:P1} of {Top1Cases}, top-3 {Top3Rate:P1}");
        foreach (var m in Misses)
            sb.AppendLine(m);
        return sb.ToString();
    }
}

internal static class GoldenSet
{
    public static IReadOnlyList<GoldenCase> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "search-golden.tsv");
        var cases = new List<GoldenCase>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var f = line.Split('\t');
            cases.Add(new GoldenCase(f[0], f[1], int.Parse(f[2], CultureInfo.InvariantCulture), f.Length > 3 ? f[3] : ""));
        }

        return cases;
    }

    /// <summary>
    /// Cases marked "1" must rank first (top-1 rate); every case must be in the first three (top-3 rate).
    /// Several "3" cases share a query (":love" → ❤️ 😍 🥰), so they can't all be first.
    /// </summary>
    public static GoldenReport Evaluate(SearchEngine engine, EmojiData data, IReadOnlyList<GoldenCase> cases)
    {
        Span<SearchResult> results = stackalloc SearchResult[8];
        int top1Cases = 0, top1 = 0, top3 = 0;
        var misses = new List<string>();
        foreach (var c in cases)
        {
            var n = engine.Search(c.Query, results);
            var rank = -1;
            for (var i = 0; i < n; i++)
            {
                if (results[i].Kind == ResultKind.Emoji && Same(data.Emoji(results[i].Index), c.Emoji))
                {
                    rank = i;
                    break;
                }
            }

            var isTop1 = rank == 0;
            var isTop3 = rank is >= 0 and < 3;
            if (c.Within == 1)
            {
                top1Cases++;
                if (isTop1)
                    top1++;
            }

            if (isTop3)
                top3++;
            if ((c.Within == 1 && !isTop1) || !isTop3)
            {
                var got = new StringBuilder();
                for (var i = 0; i < Math.Min(n, 5); i++)
                    got.Append(data.Emoji(results[i].Index)).Append(' ').Append(data.Shortcode(results[i].Index, results[i].Slot)).Append('/').Append(results[i].Tier).Append("  ");
                misses.Add($"  MISS {c.Query} -> {c.Emoji} (want top {c.Within}, rank {(rank < 0 ? "-" : (rank + 1).ToString(CultureInfo.InvariantCulture))}; {c.Note}): {got}");
            }
        }

        return new GoldenReport(cases.Count, top1Cases, top1, top3, misses);
    }

    private const string VariationSelector16 = "️";

    private static bool Same(ReadOnlySpan<char> a, string b) =>
        a.ToString().Replace(VariationSelector16, "", StringComparison.Ordinal) == b.Replace(VariationSelector16, "", StringComparison.Ordinal);
}
