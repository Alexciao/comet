using Inlay.Core.Emoji;
using Inlay.Core.Search;

namespace Inlay.Core.Tests.Search;

public sealed class SearchEngineTests
{
    internal static EmojiSourceEntry Entry(string emoji, string name, string[] shortcodes, string[]? keywords = null,
        int popularity = 500, bool variant = false, bool rendered = true, (string, int)[]? curated = null, string[]? tones = null) =>
        new(emoji, name, shortcodes, keywords ?? [], curated ?? [], tones, popularity, 0, 0, variant, rendered);

    internal static EmojiData Build(params EmojiSourceEntry[] entries) => EmojiData.FromBytes(EmojiBlobWriter.Write(entries, 2026010100));

    private static string[] Top(SearchEngine engine, string query, int n = 5)
    {
        var results = new SearchResult[n];
        var count = engine.Search(query, results);
        return [.. results.Take(count).Select(r => r.Kind == ResultKind.Snippet
            ? "snippet:" + r.Index
            : engine.Emoji!.Shortcode(r.Index, 0).ToString())];
    }

    private static EmojiData Sample() => Build(
        Entry("😄", "grinning face with smiling eyes", ["smile"], ["happy", "joy"], popularity: 60),
        Entry("😃", "grinning face with big eyes", ["smiley"], ["happy"], popularity: 50),
        Entry("😏", "smirking face", ["smirk"], ["smug"], popularity: 400),
        Entry("👍", "thumbs up", ["thumbsup", "+1", "thumbs_up"], ["approve", "ok"], popularity: 10),
        Entry("❤️", "red heart", ["heart"], ["love"], popularity: 2, curated: [("love", 0)]),
        Entry("💌", "love letter", ["love_letter"], ["mail"], popularity: 900),
        Entry("🤦", "person facepalming", ["facepalm"], ["disbelief"], popularity: 100),
        Entry("🤦‍♂️", "man facepalming", ["man_facepalming"], ["facepalm"], popularity: 100, variant: true),
        Entry("🫩", "face with bags under eyes", ["face_with_bags_under_eyes"], ["tired"], rendered: false));

    [Fact]
    public void Exact_shortcode_ranks_first_then_prefixes()
    {
        using var data = Sample();
        var engine = new SearchEngine(data, null);
        Assert.Equal(["smile", "smiley", "smirk"], Top(engine, "smi").Take(3));
        Assert.Equal("smile", Top(engine, "smile")[0]);
    }

    [Fact]
    public void Curated_words_rank_like_aliases()
    {
        using var data = Sample();
        var engine = new SearchEngine(data, null);
        Assert.Equal("heart", Top(engine, "love")[0]);
        Assert.Equal("love_letter", Top(engine, "love")[1]);
    }

    [Fact]
    public void Base_emoji_ranks_before_its_gendered_variant()
    {
        using var data = Sample();
        var engine = new SearchEngine(data, null);
        var top = Top(engine, "facepalm");
        Assert.Equal("facepalm", top[0]);
        Assert.Contains("man_facepalming", top);
    }

    [Fact]
    public void Separator_insensitive_and_alias_matches_report_the_alias_slot()
    {
        using var data = Sample();
        var engine = new SearchEngine(data, null);
        var results = new SearchResult[3];
        Assert.True(engine.Search("thumbs-up", results) > 0);
        Assert.Equal(MatchTier.Exact, results[0].Tier);
        Assert.True(engine.Search("+1", results) > 0);
        Assert.Equal("+1", data.Shortcode(results[0].Index, results[0].Slot).ToString());
    }

    [Fact]
    public void Typos_never_outrank_a_real_prefix_match()
    {
        using var data = Sample();
        var engine = new SearchEngine(data, null);
        var results = new SearchResult[8];
        var n = engine.Search("smirk", results);
        Assert.Equal(MatchTier.Exact, results[0].Tier);
        for (var i = 1; i < n; i++)
            Assert.True(results[i].Tier >= results[i - 1].Tier);
    }

    [Fact]
    public void Short_queries_get_no_fuzzy_matches()
    {
        using var data = Sample();
        var engine = new SearchEngine(data, null);
        var results = new SearchResult[8];
        var n = engine.Search("hrx", results);
        Assert.Equal(0, n);
    }

    [Fact]
    public void Frecency_lifts_an_emoji_within_its_tier_only()
    {
        using var data = Sample();
        var engine = new SearchEngine(data, null);
        Assert.Equal("smile", Top(engine, "smi")[0]);
        engine.SetEmojiFrecency(2, 3f); // 😏 smirk
        Assert.Equal("smirk", Top(engine, "smi")[0]);
        Assert.Equal("smile", Top(engine, "smile")[0]); // exact still wins over a frecent prefix
        engine.ClearFrecency();
        Assert.Equal("smile", Top(engine, "smi")[0]);
    }

    [Fact]
    public void Hidden_emoji_never_match_and_visibility_can_change()
    {
        using var data = Sample();
        var engine = new SearchEngine(data, null);
        Assert.DoesNotContain("face_with_bags_under_eyes", Top(engine, "face_with"));
        Assert.Null(engine.FindExact("face_with_bags_under_eyes"));
        engine.SetVisible(8, true);
        Assert.Contains("face_with_bags_under_eyes", Top(engine, "face_with"));
    }

    [Fact]
    public void Snippets_rank_above_emoji_at_the_same_name_tier()
    {
        using var data = Sample();
        var engine = new SearchEngine(data, new SnippetIndex([("smiles", "Have a great day"), ("website", "https://example.com/")]));
        Assert.Equal("snippet:0", Top(engine, "smi")[0]);
        Assert.Equal("smile", Top(engine, "smile")[0]); // exact emoji beats a snippet prefix
        Assert.Equal("snippet:1", Top(engine, "web")[0]);
        Assert.Equal("snippet:0", Top(engine, "great")[0]); // text words match like keywords
    }

    [Fact]
    public void FindExact_prefers_snippets_then_shortcodes_and_ignores_keywords()
    {
        using var data = Sample();
        var engine = new SearchEngine(data, new SnippetIndex([("heart", "<3")]));
        Assert.Equal(ResultKind.Snippet, engine.FindExact("heart")!.Value.Kind);
        Assert.Equal(3, engine.FindExact("thumbs_up")!.Value.Index);
        Assert.Null(engine.FindExact("approve"));
        Assert.Null(engine.FindExact("smil"));
    }

    [Fact]
    public void Search_without_emoji_data_returns_only_snippets()
    {
        var engine = new SearchEngine(null, new SnippetIndex([("website", "x")]));
        var results = new SearchResult[4];
        Assert.Equal(1, engine.Search("web", results));
        Assert.Equal(ResultKind.Snippet, results[0].Kind);
        Assert.Equal(0, engine.Search("smile", results));
    }

    [Fact]
    public void Search_does_not_allocate()
    {
        using var data = EmojiData.LoadEmbedded();
        var engine = new SearchEngine(data, new SnippetIndex([("website", "https://example.com/")]));
        var results = new SearchResult[8];
        string[] queries = ["s", "sm", "smi", "smil", "smile", "smiel", "thnumbs", "hrt", "laughign"];
        foreach (var q in queries)
            engine.Search(q, results); // warm up
        var before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var q in queries)
            engine.Search(q, results);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Skin_tones_are_selectable()
    {
        using var data = Build(Entry("👋", "waving hand", ["wave"], tones: ["👋🏻", "👋🏼", "👋🏽", "👋🏾", "👋🏿"]));
        Assert.True(data.HasSkinTones(0));
        Assert.Equal("👋🏽", data.Emoji(0, 3).ToString());
        Assert.Equal("👋", data.Emoji(0, 0).ToString());
    }
}
