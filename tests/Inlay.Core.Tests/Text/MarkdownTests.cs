using Inlay.Core.Text;

namespace Inlay.Core.Tests.Text;

public sealed class MarkdownTests
{
    private static string Plain(MarkdownBlock block) => string.Concat(block.Runs.Select(r => r.Text));

    private static MarkdownRun[] Runs(params MarkdownRun[] runs) => runs;

    private static MarkdownRun Single(string inline) => Assert.Single(Markdown.ParseInline(inline));

    [Fact]
    public void Headings_paragraphs_and_lists_become_blocks()
    {
        var blocks = Markdown.Parse("""
            ## What's new

            Quick Insert opens
            at the caret.

            ### Fixed
            - One
            - Two, wrapped
              onto a second line
              - Nested
            1. First
            2) Second
            """);

        Assert.Collection(blocks,
            b => (b.Kind, b.Level, Plain(b)).ShouldBe(MarkdownBlockKind.Heading, 2, "What's new"),
            b => (b.Kind, b.Level, Plain(b)).ShouldBe(MarkdownBlockKind.Paragraph, 0, "Quick Insert opens at the caret."),
            b => (b.Kind, b.Level, Plain(b)).ShouldBe(MarkdownBlockKind.Heading, 3, "Fixed"),
            b => (b.Kind, b.Level, b.Marker + Plain(b)).ShouldBe(MarkdownBlockKind.ListItem, 0, "•One"),
            b => (b.Kind, b.Level, b.Marker + Plain(b)).ShouldBe(MarkdownBlockKind.ListItem, 0, "•Two, wrapped onto a second line"),
            b => (b.Kind, b.Level, b.Marker + Plain(b)).ShouldBe(MarkdownBlockKind.ListItem, 1, "•Nested"),
            b => (b.Kind, b.Level, b.Marker + Plain(b)).ShouldBe(MarkdownBlockKind.ListItem, 0, "1.First"),
            b => (b.Kind, b.Level, b.Marker + Plain(b)).ShouldBe(MarkdownBlockKind.ListItem, 0, "2.Second"));
    }

    [Fact]
    public void Crlf_comments_rules_quotes_and_link_definitions_are_handled()
    {
        var blocks = Markdown.Parse("<!-- generated -->\r\n# Title #\r\n\r\n---\r\n> Quoted\r\n\r\n[1.0.0]: https://github.com/x/y\r\n");

        Assert.Collection(blocks,
            b => (b.Kind, b.Level, Plain(b)).ShouldBe(MarkdownBlockKind.Heading, 1, "Title"),
            b => (b.Kind, b.Level, Plain(b)).ShouldBe(MarkdownBlockKind.Paragraph, 0, "Quoted"));
    }

    [Fact]
    public void Fenced_code_keeps_its_lines()
    {
        var block = Assert.Single(Markdown.Parse("```json\n{ \"a\": 1 }\n  ## not a heading\n```"));
        Assert.Equal(MarkdownBlockKind.Code, block.Kind);
        Assert.Equal(new MarkdownRun("{ \"a\": 1 }\n  ## not a heading", MarkdownStyle.Code), Assert.Single(block.Runs));
    }

    [Fact]
    public void Hashtags_and_bold_lines_are_not_headings_or_bullets()
    {
        Assert.Equal(MarkdownBlockKind.Paragraph, Assert.Single(Markdown.Parse("#hashtag")).Kind);
        Assert.Equal(MarkdownBlockKind.Paragraph, Assert.Single(Markdown.Parse("**Bold** start")).Kind);
    }

    [Fact]
    public void Bold_italic_and_code_are_styled_runs()
    {
        Assert.Equal(Runs(new("A "), new("bold", MarkdownStyle.Bold), new(", "), new("italic", MarkdownStyle.Italic), new(" and "), new("code", MarkdownStyle.Code), new(".")), Markdown.ParseInline("A **bold**, _italic_ and `code`."));
        Assert.Equal(Runs(new MarkdownRun("both", MarkdownStyle.Bold | MarkdownStyle.Italic)), Markdown.ParseInline("***both***"));
        Assert.Equal(Runs(new MarkdownRun("x", MarkdownStyle.Italic), new MarkdownRun("y", MarkdownStyle.Italic | MarkdownStyle.Bold)), Markdown.ParseInline("*x**y***"));
    }

    [Theory]
    [InlineData("snake_case_name")]
    [InlineData("2 * 3 * 4")]
    [InlineData("a ** b")]
    [InlineData("**unclosed")]
    [InlineData("`unclosed")]
    public void Lone_markers_stay_text(string text) => Assert.Equal(new MarkdownRun(text), Single(text));

    [Fact]
    public void Escapes_and_markers_inside_code_are_literal()
    {
        Assert.Equal(new MarkdownRun("*not italic*"), Single(@"\*not italic\*"));
        Assert.Equal(new MarkdownRun("**x**", MarkdownStyle.Code), Single("`**x**`"));
        Assert.Equal(new MarkdownRun("a `tick`", MarkdownStyle.Code), Single("`` a `tick` ``"));
    }

    [Fact]
    public void Links_carry_their_address_and_style()
    {
        var runs = Markdown.ParseInline("See [the **release**](https://github.com/x/y/releases \"Releases\") now");
        var page = new Uri("https://github.com/x/y/releases");
        Assert.Equal(Runs(new("See "), new("the ", Link: page), new("release", MarkdownStyle.Bold, page), new(" now")), runs);
    }

    [Fact]
    public void Bare_and_angled_addresses_become_links_without_trailing_punctuation()
    {
        var compare = new Uri("https://github.com/x/y/compare/v1.0.0...v1.1.0");
        Assert.Equal(Runs(new("Full Changelog", MarkdownStyle.Bold), new(": "), new("https://github.com/x/y/compare/v1.0.0...v1.1.0", Link: compare), new(".")), Markdown.ParseInline("**Full Changelog**: https://github.com/x/y/compare/v1.0.0...v1.1.0."));
        Assert.Equal(Runs(new("("), new("https://example.com/a", Link: new Uri("https://example.com/a")), new(")")), Markdown.ParseInline("(https://example.com/a)"));
        Assert.Equal(Runs(new MarkdownRun("https://example.com", Link: new Uri("https://example.com"))), Markdown.ParseInline("<https://example.com>"));
    }

    [Theory]
    [InlineData("[relative](CHANGELOG.md)", "relative")]
    [InlineData("[script](javascript:alert(1))", "script")]
    [InlineData("![screenshot](https://example.com/a.png)", "screenshot")]
    [InlineData("[not a link]", "[not a link]")]
    [InlineData("a < b > c", "a < b > c")]
    public void Only_http_addresses_link_and_images_show_their_description(string text, string shown) =>
        Assert.Equal(new MarkdownRun(shown), Single(text));
}

internal static class MarkdownAssertions
{
    public static void ShouldBe(this (MarkdownBlockKind Kind, int Level, string Text) actual, MarkdownBlockKind kind, int level, string text) =>
        Assert.Equal((kind, level, text), actual);
}
