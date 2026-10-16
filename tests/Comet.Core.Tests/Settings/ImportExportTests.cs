using Comet.Core.Settings;

namespace Comet.Core.Tests.Settings;

public sealed class ImportExportTests
{
    [Fact]
    public void Snippet_export_round_trips_and_import_merges_by_name()
    {
        var mine = new List<SnippetDefinition> { new() { Name = "website", Text = "old" }, new() { Name = "email", Text = "me@example.com" } };
        var websiteId = mine[0].Id;
        var json = ImportExport.ExportSnippets([new SnippetDefinition { Name = "Website", Text = "https://example.com/" }, new SnippetDefinition { Name = "address", Text = "Via Roma 1\nRoma" }]);

        var (added, replaced) = ImportExport.ImportSnippets(json, mine);

        Assert.Equal((1, 1), (added, replaced));
        Assert.Equal(3, mine.Count);
        Assert.Equal("https://example.com/", mine[0].Text);
        Assert.Equal(websiteId, mine[0].Id); // frecency stays attached
        Assert.Equal("Via Roma 1\nRoma", mine[2].Text);
    }

    [Fact]
    public void Auto_replace_import_keeps_flags()
    {
        var mine = new List<AutoReplaceDefinition>();
        var json = ImportExport.ExportAutoReplace([new AutoReplaceDefinition { Abbreviation = "BTW", Replacement = "by the way", MatchCase = true, Enabled = false }]);
        Assert.Equal((1, 0), ImportExport.ImportAutoReplace(json, mine));
        Assert.True(mine[0].MatchCase);
        Assert.False(mine[0].Enabled);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("null")]
    public void Garbage_is_rejected(string json) =>
        Assert.Throws<FormatException>(() => ImportExport.ImportSnippets(json, []));

    [Fact]
    public void Entries_without_names_are_skipped() =>
        Assert.Equal((0, 0), ImportExport.ImportSnippets("""{"snippets":[{"name":"  ","text":"x"}]}""", []));
}
