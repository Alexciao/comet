using Inlay.Core.Emoji;
using Inlay.Core.Search;

namespace Inlay.Core.Tests.Search;

public sealed class SymbolSearchTests
{
    private static EmojiSourceEntry Symbol(string symbol, string display, string[] names, string[]? keywords = null, int popularity = 100, bool rendered = true) =>
        new(symbol, display, names, keywords ?? [], [], null, popularity, 0, 10, false, rendered, IsSymbol: true);

    private static EmojiData Sample() => SearchEngineTests.Build(
        SearchEngineTests.Entry("❤️", "red heart", ["heart"], ["love"], popularity: 2),
        SearchEngineTests.Entry("➡️", "right arrow", ["right_arrow"], ["arrow"], popularity: 174) with { SearchAliases = ["arrow_right"] },
        SearchEngineTests.Entry("👍", "thumbs up", ["thumbsup"], ["approve"], popularity: 10) with { SearchAliases = ["+1", "thumbs_up"] },
        Symbol("→", "Rightwards arrow", ["arrow_right", "right"], ["arrow", "next"]),
        Symbol("♥", "Black heart suit", ["heart_suit"], ["heart", "love"], popularity: 400),
        Symbol("⏻", "Power symbol", ["power_symbol"], rendered: false));

    private static string[] Top(SearchEngine engine, string query, int n = 5)
    {
        var results = new SearchResult[n];
        var count = engine.Search(query, results);
        return [.. results.Take(count).Select(r => engine.Emoji!.Emoji(r.Index).ToString())];
    }

    [Fact]
    public void Symbols_round_trip_through_the_blob_after_the_emoji()
    {
        using var data = Sample();
        Assert.Equal(6, data.Count);
        Assert.Equal(3, data.FirstSymbol);
        Assert.Equal(3, data.SymbolCount);
        Assert.False(data.IsSymbol(0));
        Assert.True(data.IsSymbol(3));
        Assert.False(data.IsSymbol(6));
        Assert.Equal("→", data.Emoji(3).ToString());
        Assert.Equal("Rightwards arrow", data.Name(3).ToString());
        Assert.Equal("arrow_right", data.Shortcode(3, 0).ToString());
        Assert.Equal("right", data.Shortcode(3, 1).ToString());
        Assert.Equal("→", data.Emoji(3, tone: 3).ToString());
        Assert.False(data.HasSkinTones(3));
        Assert.False(data.RenderedOnReferenceFont(5));
    }

    [Fact]
    public void Symbols_must_follow_every_emoji()
    {
        Assert.Throws<ArgumentException>(() => EmojiBlobWriter.Write(
            [Symbol("→", "Rightwards arrow", ["arrow_right"]), SearchEngineTests.Entry("❤️", "red heart", ["heart"])], 1));
    }

    [Fact]
    public void A_symbol_ranks_after_an_emoji_in_the_same_tier_unless_the_query_is_its_own_name()
    {
        using var data = Sample();
        var engine = new SearchEngine(data, null);
        Assert.Equal(["→", "➡️"], Top(engine, "arrow_right").Take(2)); // its own name: the symbol first
        Assert.Equal("→", Top(engine, "right")[0]);                  // an alias is its own name too
        Assert.Equal(["➡️", "→"], Top(engine, "arrow_r").Take(2));   // both prefixes: the emoji first
        Assert.Equal(["❤️", "♥"], Top(engine, "heart").Take(2));      // an emoji's exact shortcode beats the symbol's prefix
        Assert.Equal(["❤️", "♥"], Top(engine, "love").Take(2));       // both keywords: the emoji first
    }

    [Fact]
    public void Frecency_can_lift_a_symbol_over_an_emoji_in_its_tier()
    {
        using var data = Sample();
        var engine = new SearchEngine(data, null);
        engine.SetEmojiFrecency(4, 2f); // ♥
        Assert.Equal("♥", Top(engine, "love")[0]);
    }

    [Fact]
    public void Exact_codes_prefer_the_symbol_name_then_the_shortcode_owner_then_hidden_aliases()
    {
        using var data = Sample();
        var engine = new SearchEngine(data, null);
        Assert.Equal(3, engine.FindExact("arrow-right")!.Value.Index);
        Assert.Equal(1, engine.FindExact("right_arrow")!.Value.Index);
        var plusOne = engine.FindExact("+1")!.Value;
        Assert.Equal(2, plusOne.Index);
        Assert.Equal("thumbsup", data.Shortcode(plusOne.Index, plusOne.Slot).ToString()); // the alias is never shown
        Assert.Null(engine.FindExact("power_symbol")); // not drawable
    }

    [Fact]
    public void Symbols_left_out_are_gone_from_the_index()
    {
        using var data = Sample();
        var engine = new SearchEngine(data, null, includeSymbols: false);
        Assert.False(engine.IncludesSymbols);
        Assert.Equal(["➡️"], Top(engine, "arrow_right"));
        Assert.DoesNotContain("♥", Top(engine, "heart"));
        Assert.DoesNotContain("♥", Top(engine, "haert")); // nor through typos
        Assert.Null(engine.FindExact("right"));
        Assert.Equal(1, engine.FindExact("arrow_right")!.Value.Index); // the emoji's hidden alias again
        Assert.False(engine.IsVisible(3));

        // Per-entry setters take indices from the whole dataset (font check, frecency): symbols are ignored.
        engine.SetVisible(3, true);
        engine.SetEmojiFrecency(3, 5f);
        engine.BumpFrecency(ResultKind.Emoji, 3);
        Assert.False(engine.IsVisible(3));
        Assert.Equal(["➡️"], Top(engine, "arrow_right"));
    }

    [Theory]
    [InlineData("arrow_right", "→")]
    [InlineData("arrow-right", "→")]
    [InlineData("arrowright", "→")]
    [InlineData("arrow right", "→")]
    [InlineData("Arrow_Right", "→")]
    [InlineData("right", "→")]
    [InlineData("deg", "°")]
    [InlineData("degree", "°")]
    [InlineData("euro", "€")]
    [InlineData("half", "½")]
    [InlineData("check", "✓")]
    [InlineData("alpha", "α")]
    [InlineData("em_dash", "—")]
    [InlineData("em-dash", "—")]
    [InlineData("heart", "❤️")]
    [InlineData("+1", "👍")]
    public void Shipped_symbols_and_codes_are_found_separator_insensitively(string query, string first)
    {
        using var data = EmojiData.LoadEmbedded();
        var engine = new SearchEngine(data, null);
        Assert.Equal(first, Top(engine, query)[0]);
        Assert.Equal(first, data.Emoji(engine.FindExact(query)!.Value.Index).ToString());
    }

    [Fact]
    public void Shipped_symbols_are_text_and_drawn_by_the_reference_fonts()
    {
        using var data = EmojiData.LoadEmbedded();
        Assert.InRange(data.SymbolCount, 250, 400);
        for (var i = data.FirstSymbol; i < data.Count; i++)
        {
            Assert.True(data.IsSymbol(i));
            Assert.DoesNotContain('️', data.Emoji(i).ToString());
            Assert.True(char.IsUpper(data.Name(i)[0]), data.Name(i).ToString()); // a display name, not a CLDR label
        }

        var degree = data.IndexOf("°");
        Assert.True(data.IsSymbol(degree) && data.RenderedOnReferenceFont(degree));
        Assert.False(data.IsSymbol(data.IndexOf("👍")));
    }
}
