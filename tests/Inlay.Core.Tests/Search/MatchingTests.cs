using Inlay.Core.Search;
using Inlay.Core.Text;

namespace Inlay.Core.Tests.Search;

public sealed class MatchingTests
{
    [Theory]
    [InlineData("thumbs_up", "thumbsup")]
    [InlineData("Thumbs-Up", "thumbsup")]
    [InlineData("thumbsup", "thumbsup")]
    [InlineData("Piñata", "pinata")]
    [InlineData("Côte d’Ivoire", "cotedivoire")]
    [InlineData("Straße", "strasse")]
    [InlineData("+1", "+1")]
    [InlineData("-1", "-1")]
    [InlineData("a-1", "a1")]
    [InlineData("", "")]
    public void Fold_is_separator_case_and_diacritic_insensitive(string input, string expected) =>
        Assert.Equal(expected, SearchText.Fold(input));

    [Fact]
    public void Word_suffixes_cover_every_word_start()
    {
        Assert.Equal(["facewithtearsofjoy", "withtearsofjoy", "tearsofjoy", "ofjoy", "joy"], SearchText.WordSuffixes("face with tears of joy"));
        Assert.Equal(["+1"], SearchText.WordSuffixes("+1"));
    }

    [Theory]
    [InlineData("smiel", "smile", 1)]       // adjacent transposition
    [InlineData("thnumbs", "thumbsup", 1)]  // insertion, matched against a prefix
    [InlineData("cofee", "coffee", 1)]      // substitution against prefix "coffe"
    [InlineData("smile", "smileyface", 0)]  // plain prefix
    [InlineData("sunglases", "sunglasses", 1)]
    [InlineData("abcd", "wxyz", 2)]         // exceeds the bound: max + 1
    public void Prefix_distance_is_bounded_osa_against_the_closest_prefix(string query, string term, int expected) =>
        Assert.Equal(expected, EditDistance.PrefixDistance(query, term, 1));

    [Fact]
    public void Prefix_distance_allows_two_edits_when_asked()
    {
        Assert.Equal(1, EditDistance.PrefixDistance("laughign", "laughing", 2));
        Assert.Equal(2, EditDistance.PrefixDistance("bxrthdya", "birthday", 2)); // substitution + transposition
        Assert.Equal(3, EditDistance.PrefixDistance("bxrthdyaa", "birthday", 2));
    }

    [Theory]
    [InlineData("hreat", "heart", true)]
    [InlineData("hreat", "heartbeat", true)]
    [InlineData("rheat", "heart", false)] // first letter must stay
    [InlineData("hre", "her", false)]     // too short to be a scramble
    public void Scrambled_prefix_keeps_the_first_letter(string query, string term, bool expected) =>
        Assert.Equal(expected, EditDistance.IsScrambledPrefix(query, term));

    [Theory]
    [InlineData("tup", "thumbs_up", true)]
    [InlineData("hrt", "heart", true)]
    [InlineData("wcm", "white_check_mark", true)]
    [InlineData("rckt", "rocket", true)]
    [InlineData("sws", "sweat_smile", true)]
    [InlineData("smiel", "smiling face with smiling eyes", false)] // skips consonants mid-word
    [InlineData("hrt", "the heart", false)]                       // must start at the first letter
    [InlineData("xyz", "heart", false)]
    public void Abbreviation_drops_vowels_or_takes_word_initials(string query, string text, bool expected) =>
        Assert.Equal(expected, Abbreviation.TryMatch(query, text, out _));

    [Fact]
    public void Abbreviation_prefers_the_tighter_alignment()
    {
        Assert.True(Abbreviation.TryMatch("hrt", "heart", out var tight));
        Assert.True(Abbreviation.TryMatch("hrt", "heavy_rain_tomorrow", out var loose));
        Assert.True(tight < loose);
    }
}
