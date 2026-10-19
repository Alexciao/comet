using Inlay.Core.Emoji;
using Inlay.Core.Text;

namespace Inlay.Core.Tests.Emoji;

/// <summary>Built-in codes are lowercase snake_case and unique once folded (the data builder enforces it too).</summary>
public sealed class EmojiCodeTests
{
    [Theory]
    [InlineData("thumbsup", true)]
    [InlineData("arrow_right", true)]
    [InlineData("point_up_2", true)]
    [InlineData("100", true)]
    [InlineData("t-rex", false)]
    [InlineData("Arrow_right", false)]
    [InlineData("arrow__right", false)]
    [InlineData("_arrow", false)]
    [InlineData("arrow_", false)]
    [InlineData("+1", false)]
    [InlineData("", false)]
    [InlineData("piñata", false)]
    public void Snake_case_rule(string code, bool valid) => Assert.Equal(valid, EmojiCode.IsValid(code));

    [Theory]
    [InlineData("t-rex", "t_rex")]
    [InlineData("Non-Potable_Water", "non_potable_water")]
    [InlineData("e-mail", "e_mail")]
    [InlineData("piñata", "pinata")]
    [InlineData("--a  b--", "a_b")]
    [InlineData("+1", "1")]
    public void Upstream_codes_normalize_to_snake_case(string code, string expected) => Assert.Equal(expected, EmojiCode.Normalize(code));

    [Fact]
    public void Every_shipped_code_is_snake_case_and_unique()
    {
        using var data = EmojiData.LoadEmbedded();
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var problems = new List<string>();
        for (var i = 0; i < data.Count; i++)
        {
            for (var slot = 0; slot < data.ShortcodeCount(i); slot++)
            {
                var code = data.Shortcode(i, slot).ToString();
                if (!EmojiCode.IsValid(code))
                    problems.Add($"{data.Emoji(i)} '{code}' is not snake_case");
                else if (!owners.TryAdd(SearchText.Fold(code), $"{data.Emoji(i)} '{code}'"))
                    problems.Add($"{data.Emoji(i)} '{code}' collides with {owners[SearchText.Fold(code)]}");
            }
        }

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }
}
