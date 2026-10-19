using Comet.Core.Settings;

namespace Comet.Core.Tests.Settings;

/// <summary>Regression tests for the storage findings in AUDIT.md (S1–S11).</summary>
public sealed class StorageAuditTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "comet-audit-" + Guid.NewGuid().ToString("N"));

    public StorageAuditTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var f in Directory.GetFiles(_dir))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_dir, recursive: true);
    }

    private JsonStore<SnippetsFile> Store() => new(Path.Combine(_dir, "snippets.json"), CometJsonContext.Default.SnippetsFile, SnippetsFile.CurrentVersion);

    private static SnippetsFile With(params string[] names) => new() { Snippets = [.. names.Select(n => new SnippetDefinition { Name = n, Text = "t" })] };

    [Fact]
    public void S1_a_locked_file_is_unreadable_not_empty()
    {
        var store = Store();
        store.Save(With("sig"));
        using (new FileStream(store.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var r = store.Load();
            Assert.Equal(LoadStatus.Unreadable, r.Status);
            Assert.False(r.HasContent);
        }

        Assert.Equal("sig", Assert.Single(store.Load().Value.Snippets).Name);
    }

    [Fact]
    public void S3_a_migration_that_cannot_be_saved_does_not_throw()
    {
        var store = Store();
        File.WriteAllText(store.Path, """{ "snippets": [ { "name": "sig", "text": "x" } ] }"""); // no version: migrated on load
        File.SetAttributes(store.Path, FileAttributes.ReadOnly);
        var r = store.Load();
        Assert.Equal(LoadStatus.Migrated, r.Status);
        Assert.Equal("sig", Assert.Single(r.Value.Snippets).Name);
    }

    [Fact]
    public async Task S4_a_write_and_a_change_check_never_interleave()
    {
        // ChangedOnDisk and Write share one lock: run them against each other and the store must never see its
        // own content as a hand edit.
        var store = Store();
        store.Save(With("a"));
        var stop = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        var falseAlarms = 0;
        var writer = Task.Run(
            () =>
            {
                for (var i = 0; DateTime.UtcNow < stop; i++)
                    store.Save(With("a" + i));
            },
            TestContext.Current.CancellationToken);
        while (!writer.IsCompleted)
        {
            if (store.ChangedOnDisk())
                falseAlarms++;
        }

        await writer;
        Assert.Equal(0, falseAlarms);
    }

    [Fact]
    public void S5_a_failed_write_leaves_the_hash_matching_the_disk()
    {
        var store = Store();
        store.Save(With("old"));
        File.SetAttributes(store.Path, FileAttributes.ReadOnly);
        Assert.ThrowsAny<Exception>(() => store.Save(With("new")));
        Assert.False(store.ChangedOnDisk()); // so no reload "restores" the old file over the user's change
    }

    [Fact]
    public void S6_recovery_keeps_the_good_backup_and_a_reload_never_repairs()
    {
        var store = Store();
        store.Save(With("first"));
        store.Save(With("second")); // .bak now holds "first"
        File.WriteAllText(store.Path, "{ broken");

        var reload = store.Load(repair: false);
        Assert.Equal(LoadStatus.Corrupt, reload.Status);
        Assert.False(reload.HasContent);
        Assert.Equal("{ broken", File.ReadAllText(store.Path)); // a reload leaves the user's file alone

        var startup = store.Load();
        Assert.Equal(LoadStatus.RecoveredFromBackup, startup.Status);
        Assert.Equal("first", Assert.Single(startup.Value.Snippets).Name);
        Assert.Contains("first", File.ReadAllText(store.BackupPath), StringComparison.Ordinal); // still a good backup
        Assert.NotNull(startup.CorruptCopyPath);
        Assert.Equal("{ broken", File.ReadAllText(startup.CorruptCopyPath!));
    }

    [Fact]
    public void S6_saving_over_a_corrupt_file_does_not_make_it_the_backup()
    {
        var store = Store();
        store.Save(With("good"));
        store.Save(With("good2"));
        File.WriteAllText(store.Path, "{ broken");
        store.Load(repair: false);
        store.Save(With("fixed"));
        Assert.Contains("good", File.ReadAllText(store.BackupPath), StringComparison.Ordinal);
    }

    [Fact]
    public void S7_a_file_from_a_newer_version_is_kept_before_this_version_rewrites_it()
    {
        var store = Store();
        const string newer = """{ "schemaVersion": 7, "snippets": [ { "name": "sig", "text": "x", "colour": "red" } ], "folders": [] }""";
        File.WriteAllText(store.Path, newer);
        var r = store.Load();
        Assert.Equal(LoadStatus.FromNewerVersion, r.Status);
        Assert.Equal("sig", Assert.Single(r.Value.Snippets).Name);
        Assert.Equal(newer, File.ReadAllText(r.CorruptCopyPath!));
    }

    [Fact]
    public void S2_keys_differing_only_in_case_do_not_crash()
    {
        var settings = new AppSettings { InsertionMethods = new(StringComparer.Ordinal) { ["Notepad.exe"] = "clipboard", ["notepad.exe"] = "typing" } }.Normalize();
        Assert.Single(settings.InsertionMethods);
        Assert.Equal("typing", settings.InsertionMethods["NOTEPAD.EXE"]);
    }

    [Fact]
    public void S11_hand_edited_lists_are_cleaned()
    {
        var json = """{ "snippets": [ null, { "name": null, "text": "x" }, { "id": "a", "name": "one", "text": null }, { "id": "a", "name": "two", "text": "y" } ] }""";
        var file = System.Text.Json.JsonSerializer.Deserialize(json, CometJsonContext.Default.SnippetsFile)!;
        var clean = Cleanup.Snippets(file.Snippets);
        Assert.Equal(["one", "two"], clean.Select(s => s.Name));
        Assert.Equal("", clean[0].Text);
        Assert.NotEqual(clean[0].Id, clean[1].Id);
    }

    [Fact]
    public void S11_property_names_are_matched_without_case()
    {
        var s = System.Text.Json.JsonSerializer.Deserialize("""{ "StartAtLogin": false, "SKINTONE": 2 }""", CometJsonContext.Default.AppSettings)!;
        Assert.False(s.StartAtLogin);
        Assert.Equal(2, s.SkinTone);
    }

    [Fact]
    public void S11_an_emoji_trigger_falls_back_to_the_colon()
    {
        Assert.Equal(":", new AppSettings { TriggerCharacter = "😀" }.Normalize().TriggerCharacter);
        Assert.Equal(";", new AppSettings { TriggerCharacter = ";" }.Normalize().TriggerCharacter);
    }

    [Fact]
    public void S11_imports_skip_entries_the_dialogs_would_reject()
    {
        var mine = new List<SnippetDefinition>();
        var json = """{ "snippets": [ null, { "name": "two words", "text": "x" }, { "name": "ok", "text": "y" } ] }""";
        Assert.Equal((1, 0), ImportExport.ImportSnippets(json, mine));
        Assert.Equal("ok", Assert.Single(mine).Name);

        var entries = new List<AutoReplaceDefinition>();
        Assert.Equal((1, 0), ImportExport.ImportAutoReplace("""{ "entries": [ null, { "abbreviation": "a b", "replacement": "x" }, { "abbreviation": "btw", "replacement": "by the way" } ] }""", entries));
    }

    [Fact]
    public void Unreadable_and_newer_files_report_why_in_their_status()
    {
        var r = new LoadResult<SnippetsFile>(new SnippetsFile(), LoadStatus.Unreadable, null);
        Assert.False(r.HasContent);
        Assert.True(new LoadResult<SnippetsFile>(new SnippetsFile(), LoadStatus.FromNewerVersion, null).HasContent);

    }
}
