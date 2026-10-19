using System.Globalization;
using Inlay.Core.Snippets;

namespace Inlay.Core.Tests.Snippets;

public sealed class SnippetTemplateTests
{
    private sealed class Env(string clipboard = "copied") : ISnippetEnvironment
    {
        public DateTime Now { get; } = new(2026, 10, 16, 21, 5, 9); // a Friday

        public CultureInfo Culture { get; } = CultureInfo.GetCultureInfo("it-IT");

        public string ClipboardText() => clipboard;
    }

    private static string Expand(string t, Env? env = null) => SnippetTemplate.Expand(t, env ?? new Env()).Text;

    [Theory]
    [InlineData("{date}", "16/10/2026")]
    [InlineData("{time}", "21:05")]
    [InlineData("{date:dddd d MMMM yyyy}", "venerdì 16 ottobre 2026")]
    [InlineData("{date:yyyy-MM-dd HH:mm}", "2026-10-16 21:05")]
    [InlineData("{date+7d}", "23/10/2026")]
    [InlineData("{date-1d}", "15/10/2026")]
    [InlineData("{date+2w}", "30/10/2026")]
    [InlineData("{date+1m}", "16/11/2026")]
    [InlineData("{date-12m}", "16/10/2025")]
    [InlineData("{date+7d:dd/MM}", "23/10")]
    [InlineData("{weekday}", "venerdì")]
    [InlineData("{clipboard}", "copied")]
    [InlineData("{{", "{")]
    [InlineData("}}", "}")]
    [InlineData("{{date}}", "{date}")]
    [InlineData("Hi {clipboard}, see you {date+1d:dddd}!", "Hi copied, see you sabato!")]
    public void Every_placeholder_expands(string template, string expected) => Assert.Equal(expected, Expand(template));

    [Fact]
    public void Clipboard_without_text_expands_to_nothing() => Assert.Equal("[]", Expand("[{clipboard}]", new Env("")));

    [Fact]
    public void Cursor_marks_where_the_caret_goes()
    {
        var e = SnippetTemplate.Expand("Dear {cursor},\nRegards", new Env());
        Assert.Equal("Dear ,\nRegards", e.Text);
        Assert.Equal(5, e.CursorIndex);
        Assert.Equal(",\nRegards", e.AfterCursor.ToString());
        Assert.Equal(-1, SnippetTemplate.Expand("plain", new Env()).CursorIndex);
    }

    [Theory]
    [InlineData("{nope}", "{nope}")]
    [InlineData("{date+7x}", "{date+7x}")]
    [InlineData("{date+}", "{date+}")]
    [InlineData("{date:}", "{date:}")]
    [InlineData("{dates}", "{dates}")]
    [InlineData("a { b", "a { b")]
    [InlineData("{date {time}", "{date 21:05")]
    [InlineData("x}y", "x}y")]
    public void Unknown_or_malformed_placeholders_are_inserted_literally(string template, string expected) =>
        Assert.Equal(expected, Expand(template));

    [Fact]
    public void Validation_names_each_problem()
    {
        var problems = SnippetTemplate.Validate("{date} {nope} {date+3q} { {cursor}{cursor}");
        Assert.Equal(["{nope}", "{date+3q}", "{", "{cursor}"], problems.Select(p => p.Text));
        Assert.Contains("nope", problems[0].Message, StringComparison.Ordinal);
        Assert.Empty(SnippetTemplate.Validate("{date} {{literal}} {cursor}"));
    }

    [Fact]
    public void Multi_line_text_is_kept()
    {
        Assert.Equal("Via Roma 1\n00100 Roma\nItalia", Expand("Via Roma 1\n00100 Roma\nItalia"));
    }

    [Fact]
    public void Catalog_lists_every_placeholder_and_each_entry_is_valid()
    {
        Assert.True(SnippetTemplate.Catalog.Count >= 10);
        foreach (var (placeholder, description) in SnippetTemplate.Catalog)
        {
            Assert.False(string.IsNullOrWhiteSpace(description));
            Assert.Empty(SnippetTemplate.Validate(placeholder));
        }
    }
}
