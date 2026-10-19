using Inlay.Core.Settings;

namespace Inlay.Core.Tests.Settings;

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
        var mine = new List<ShorthandDefinition>();
        var json = ImportExport.ExportShorthands([new ShorthandDefinition { Shorthand = "BTW", Expansion = "by the way", MatchCase = true, Enabled = false }]);
        Assert.Equal((1, 0), ImportExport.ImportShorthands(json, mine));
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

    [Fact]
    public void Backup_round_trips_settings_snippets_and_shorthands()
    {
        var settings = new AppSettings { SkinTone = 3, TriggerCharacter = ";", NotifyAppUpdates = false, QuickInsertApps = [new() { App = "code.exe", Mode = QuickInsertMode.Off }] };
        var json = ImportExport.ExportBackup(settings, [new SnippetDefinition { Name = "sig", Text = "Regards" }], [new ShorthandDefinition { Shorthand = "cmq", Expansion = "comunque", Enabled = false }]);

        var backup = ImportExport.ImportBackup(json);

        Assert.Equal(3, backup.Settings!.SkinTone);
        Assert.Equal(";", backup.Settings.TriggerCharacter);
        Assert.False(backup.Settings.NotifyAppUpdates);
        Assert.Equal(QuickInsertMode.Off, Assert.Single(backup.Settings.QuickInsertApps).Mode);
        Assert.Equal("Regards", Assert.Single(backup.Snippets!).Text);
        Assert.False(Assert.Single(backup.Shorthands!).Enabled);
    }

    [Fact]
    public void Backup_import_clamps_broken_values_and_drops_unnamed_or_duplicate_entries()
    {
        const string json = """
            { "kind": "inlay-backup", "settings": { "minimumCharacters": 40 },
              "snippets": [ { "name": " sig ", "text": "a" }, { "name": "SIG", "text": "b" }, { "name": "", "text": "c" } ] }
            """;
        var backup = ImportExport.ImportBackup(json);
        Assert.Equal(AppSettings.MaxMinimumCharacters, backup.Settings!.MinimumCharacters);
        Assert.Equal("sig", Assert.Single(backup.Snippets!).Name);
        Assert.Empty(backup.Shorthands!);
    }

    [Theory]
    [InlineData("""{"snippets":[]}""")] // a snippet export, not a backup
    [InlineData("""{"kind":"inlay-backup","schemaVersion":99}""")] // from a newer Inlay
    [InlineData("nope")]
    public void Backup_import_rejects_other_files(string json) =>
        Assert.Throws<FormatException>(() => ImportExport.ImportBackup(json));
}
