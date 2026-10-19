using Inlay.Core.Settings;

namespace Inlay.Core.Tests.Settings;

/// <summary>Comet 1.0 files, backups and exports read by Inlay 1.1.</summary>
public sealed class MigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "inlay-tests-" + Guid.NewGuid().ToString("N"));

    public MigrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string PathOf(string name) => Path.Combine(_dir, name);

    /// <summary>settings.json as Comet 1.0 wrote it.</summary>
    private const string CometSettings = """
        {
          "schemaVersion": 1,
          "features": { "emoji": true, "snippets": false, "autoReplace": false },
          "startAtLogin": true,
          "triggerCharacter": ";",
          "minimumCharacters": 3,
          "skinTone": 2,
          "pickerExcludedApps": [ "slack.exe", "code.exe", "Discord.exe" ],
          "autoReplaceExcludedApps": [ "word.exe" ],
          "updateEmojiAutomatically": false,
          "notifyAppUpdates": true,
          "insertionMethods": { "javaw.exe": "clipboard" }
        }
        """;

    [Fact]
    public void Comet_settings_become_per_app_modes_and_shorthands()
    {
        File.WriteAllText(PathOf("settings.json"), CometSettings);
        var store = new JsonStore<AppSettings>(PathOf("settings.json"), InlayJsonContext.Default.AppSettings, AppSettings.CurrentVersion, Migrations.Settings);

        var result = store.Load();
        var s = result.Value.Normalize();

        Assert.Equal(LoadStatus.Migrated, result.Status);
        Assert.False(s.Features.Shorthands);
        Assert.False(s.Features.Snippets);
        Assert.True(s.Features.Images && s.Features.Symbols); // new features start on
        Assert.Equal(["word.exe"], s.ShorthandsExcludedApps);
        Assert.Equal(";", s.TriggerCharacter);
        Assert.Equal(3, s.MinimumCharacters);
        Assert.False(s.UpdateEmojiAutomatically);
        Assert.Equal("clipboard", s.InsertionMethods["javaw.exe"]);
        // Excluded apps: chat apps with their own emoji picker → Hide emoji, anything else → Off.
        Assert.Equal(QuickInsertMode.HideEmoji, s.ModeFor("slack.exe"));
        Assert.Equal(QuickInsertMode.HideEmoji, s.ModeFor("discord.exe"));
        Assert.Equal(QuickInsertMode.Off, s.ModeFor("code.exe"));
        Assert.Equal(QuickInsertMode.HideEmoji, s.ModeFor("whatsapp.exe")); // new in the chat-app list
        // A default chat app the user removed from the 1.0 list stays fully on.
        Assert.Equal(QuickInsertMode.ShowEverything, s.ModeFor("telegram.exe"));
        // Written back in the new shape, with the 1.0 file kept as the backup.
        Assert.Contains("quickInsertApps", File.ReadAllText(PathOf("settings.json")), StringComparison.Ordinal);
        Assert.Contains("pickerExcludedApps", File.ReadAllText(PathOf("settings.json.bak")), StringComparison.Ordinal);
        Assert.Equal(LoadStatus.Loaded, store.Load().Status); // runs once
    }

    [Fact]
    public void Settings_migration_matches_names_case_insensitively_and_keeps_new_lists()
    {
        var root = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse("""
            { "Features": { "AutoReplace": false }, "PickerExcludedApps": ["a.exe"], "quickInsertApps": [] }
            """)!;

        Migrations.SettingsV1ToV2(root);

        Assert.False(root["Features"]!["shorthands"]!.GetValue<bool>());
        Assert.Empty(root["quickInsertApps"]!.AsArray()); // a list in the new shape wins
    }

    [Fact]
    public void Autoreplace_json_becomes_shorthands_json_once_with_a_backup()
    {
        File.WriteAllText(PathOf("autoreplace.json"), """
            { "schemaVersion": 1, "entries": [ { "id": "a1", "abbreviation": "brb", "replacement": "be right back", "matchCase": true, "enabled": false } ] }
            """);

        Assert.True(Migrations.RenameCometFiles(_dir));
        var store = new JsonStore<ShorthandsFile>(PathOf("shorthands.json"), InlayJsonContext.Default.ShorthandsFile, ShorthandsFile.CurrentVersion, Migrations.Shorthands);
        var loaded = store.Load();

        var entry = Assert.Single(loaded.Value.Entries);
        Assert.Equal(("a1", "brb", "be right back", true, false), (entry.Id, entry.Shorthand, entry.Expansion, entry.MatchCase, entry.Enabled));
        Assert.Equal(LoadStatus.Migrated, loaded.Status);
        Assert.True(File.Exists(PathOf("autoreplace.1.0.json")));
        Assert.False(File.Exists(PathOf("autoreplace.json")));
        Assert.Contains("\"shorthand\"", File.ReadAllText(PathOf("shorthands.json")), StringComparison.Ordinal);

        // Runs once: a later autoreplace.json (say, Comet 1.0 started again) doesn't replace the shorthands.
        File.WriteAllText(PathOf("autoreplace.json"), """{ "schemaVersion": 1, "entries": [] }""");
        Assert.False(Migrations.RenameCometFiles(_dir));
        Assert.Single(store.Load().Value.Entries);
    }

    [Fact]
    public void Comet_backup_imports()
    {
        var backup = ImportExport.ImportBackup("""
            {
              "schemaVersion": 1,
              "kind": "comet-backup",
              "settings": { "schemaVersion": 1, "features": { "autoReplace": false }, "pickerExcludedApps": [ "teams.exe", "notepad.exe" ] },
              "snippets": [ { "name": "sig", "text": "Regards" } ],
              "autoReplace": [ { "abbreviation": "cmq", "replacement": "comunque", "matchCase": false, "enabled": true } ]
            }
            """);

        Assert.False(backup.Settings!.Features.Shorthands);
        Assert.Equal(QuickInsertMode.HideEmoji, backup.Settings.ModeFor("teams.exe"));
        Assert.Equal(QuickInsertMode.Off, backup.Settings.ModeFor("notepad.exe"));
        Assert.Equal("Regards", Assert.Single(backup.Snippets!).Text);
        var s = Assert.Single(backup.Shorthands!);
        Assert.Equal(("cmq", "comunque"), (s.Shorthand, s.Expansion));
        Assert.Empty(backup.Images!);
    }

    [Fact]
    public void Comet_shorthands_export_imports()
    {
        var list = new List<ShorthandDefinition>();

        var (added, replaced) = ImportExport.ImportShorthands("""
            { "schemaVersion": 1, "entries": [ { "abbreviation": "brb", "replacement": "be right back" } ] }
            """, list);

        Assert.Equal((1, 0), (added, replaced));
        Assert.Equal(("brb", "be right back"), (list[0].Shorthand, list[0].Expansion));
    }

    [Fact]
    public void Inlay_backup_archive_round_trips_with_the_image_library()
    {
        var library = Path.Combine(_dir, "Images");
        Directory.CreateDirectory(library);
        File.WriteAllBytes(Path.Combine(library, "abc.png"), [1, 2, 3]);
        var images = new[]
        {
            new ImageEntry { Name = "lgtm", Kind = ImageEntryKind.Image, File = "abc.png", Keywords = "approve ship" },
            new ImageEntry { Name = "screenshot", Kind = ImageEntryKind.Folder, Folder = "{Screenshots}", Mode = FolderMode.Newest },
        };
        var json = ImportExport.ExportBackup(new AppSettings { SkinTone = 4 }, [], [new ShorthandDefinition { Shorthand = "brb", Expansion = "be right back" }], images);
        using var archive = new MemoryStream();
        ImportExport.ExportBackupArchive(archive, json, images, library);

        var restored = Path.Combine(_dir, "Restored");
        archive.Position = 0;
        var backup = ImportExport.ImportBackupFile(archive, restored);

        Assert.Equal(4, backup.Settings!.SkinTone);
        Assert.Equal("brb", Assert.Single(backup.Shorthands!).Shorthand);
        Assert.Equal(["lgtm", "screenshot"], backup.Images!.Select(i => i.Name));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(restored, "abc.png")));
    }

    [Fact]
    public void A_json_backup_file_is_read_by_the_archive_importer_too()
    {
        using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("""{ "kind": "comet-backup", "schemaVersion": 1, "snippets": [ { "name": "a", "text": "b" } ] }"""));

        Assert.Single(ImportExport.ImportBackupFile(input, Path.Combine(_dir, "lib")).Snippets!);
    }

    [Theory]
    [InlineData("""{ "kind": "comet-backup", "schemaVersion": 99 }""")]
    [InlineData("not json")]
    [InlineData("PK not really a zip")]
    public void Not_a_backup_is_a_format_error(string content)
    {
        using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        Assert.Throws<FormatException>(() => ImportExport.ImportBackupFile(input, Path.Combine(_dir, "lib")));
    }

    [Fact]
    public void Image_entries_from_a_hand_edit_are_cleaned()
    {
        var clean = Cleanup.Images([
            new ImageEntry { Name = " ok ", Kind = ImageEntryKind.Image, File = @"..\..\evil.png", PickCount = 40, FileTypes = [".GIF", "gif", " "] },
            new ImageEntry { Name = "no-file", Kind = ImageEntryKind.Image },
            new ImageEntry { Name = "", Kind = ImageEntryKind.Folder, Folder = @"C:\x" },
            new ImageEntry { Name = "", Kind = ImageEntryKind.Collection, Folder = @"C:\gifs", Prefix = " gif- " },
            null!,
        ]);

        Assert.Equal(2, clean.Count);
        Assert.Equal(("ok", "evil.png", ImageEntry.MaxPickCount), (clean[0].Name, clean[0].File, clean[0].PickCount));
        Assert.Equal(["gif"], clean[0].FileTypes);
        Assert.Equal("gif-", clean[1].Prefix);
    }
}
