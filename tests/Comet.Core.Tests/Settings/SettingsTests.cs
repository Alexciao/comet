using System.Text.Json.Nodes;
using Comet.Core.Settings;

namespace Comet.Core.Tests.Settings;

public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "comet-tests-" + Guid.NewGuid().ToString("N"));

    public SettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private JsonStore<AppSettings> Store(IReadOnlyList<Func<JsonObject, JsonObject>>? migrations = null, int version = AppSettings.CurrentVersion) =>
        new(Path.Combine(_dir, "settings.json"), CometJsonContext.Default.AppSettings, version, migrations);

    [Fact]
    public void Defaults_have_every_feature_on_and_empty_lists()
    {
        var s = new AppSettings();
        Assert.True(s.Features.Emoji && s.Features.Snippets && s.Features.AutoReplace);
        Assert.Contains("slack.exe", s.PickerExcludedApps);
        Assert.Empty(new SnippetsFile().Snippets);
        Assert.Empty(new AutoReplaceFile().Entries);
    }

    [Fact]
    public void Missing_file_loads_defaults()
    {
        var r = Store().Load();
        Assert.Equal(LoadStatus.Missing, r.Status);
        Assert.Equal(":", r.Value.TriggerCharacter);
    }

    [Fact]
    public void Save_then_load_round_trips_and_writes_a_versioned_human_readable_file()
    {
        var store = Store();
        var s = new AppSettings { MinimumCharacters = 3, SkinTone = 2 };
        s.Features.Snippets = false;
        store.Save(s);

        var text = File.ReadAllText(store.Path);
        Assert.Contains("\"schemaVersion\": 1", text, StringComparison.Ordinal);
        Assert.Contains("\"minimumCharacters\": 3", text, StringComparison.Ordinal);

        var r = Store().Load();
        Assert.Equal(LoadStatus.Loaded, r.Status);
        Assert.Equal(3, r.Value.MinimumCharacters);
        Assert.False(r.Value.Features.Snippets);
    }

    [Fact]
    public void Second_save_keeps_the_previous_file_as_backup()
    {
        var store = Store();
        store.Save(new AppSettings { MinimumCharacters = 3 });
        store.Save(new AppSettings { MinimumCharacters = 4 });
        Assert.Contains("\"minimumCharacters\": 3", File.ReadAllText(store.BackupPath), StringComparison.Ordinal);
        Assert.False(File.Exists(store.Path + ".tmp"));
    }

    [Fact]
    public void Corrupt_file_is_copied_aside_and_the_backup_is_loaded()
    {
        var store = Store();
        store.Save(new AppSettings { MinimumCharacters = 3 });
        store.Save(new AppSettings { MinimumCharacters = 4 });
        File.WriteAllText(store.Path, "{ this is not json");

        var r = Store().Load();
        Assert.Equal(LoadStatus.RecoveredFromBackup, r.Status);
        Assert.Equal(3, r.Value.MinimumCharacters);
        Assert.NotNull(r.CorruptCopyPath);
        Assert.Equal("{ this is not json", File.ReadAllText(r.CorruptCopyPath!));
    }

    [Fact]
    public void Corrupt_file_without_usable_backup_loads_defaults()
    {
        var store = Store();
        File.WriteAllText(store.Path, "[1, 2, 3]");
        var r = store.Load();
        Assert.Equal(LoadStatus.Corrupt, r.Status);
        Assert.Equal(2, r.Value.MinimumCharacters);
        Assert.True(File.Exists(r.CorruptCopyPath));

        File.WriteAllText(store.Path, "{\"minimumCharacters\": \"three\"}"); // wrong type
        Assert.NotEqual(LoadStatus.Loaded, Store().Load().Status);
    }

    [Fact]
    public void Unversioned_hand_written_file_is_migrated_and_stamped()
    {
        var store = Store();
        File.WriteAllText(store.Path, "// written by hand\n{ \"minimumCharacters\": 4, }");
        var r = store.Load();
        Assert.Equal(LoadStatus.Migrated, r.Status);
        Assert.Equal(4, r.Value.MinimumCharacters);
        Assert.Contains("\"schemaVersion\": 1", File.ReadAllText(store.Path), StringComparison.Ordinal);
    }

    [Fact]
    public void Migrations_run_in_order_from_the_file_version()
    {
        var store = Store(version: 3, migrations:
        [
            o => o, // 0 -> 1
            o => { o["minChars"] = 4; return o; }, // 1 -> 2: introduces a field under an old name
            o => { o["minimumCharacters"] = o["minChars"]!.GetValue<int>() + 1; o.Remove("minChars"); return o; }, // 2 -> 3: rename
        ]);
        File.WriteAllText(store.Path, "{ \"schemaVersion\": 1 }");
        var r = store.Load();
        Assert.Equal(LoadStatus.Migrated, r.Status);
        Assert.Equal(5, r.Value.MinimumCharacters);
        Assert.Contains("\"schemaVersion\": 3", File.ReadAllText(store.Path), StringComparison.Ordinal);
    }

    [Fact]
    public void Hand_edits_are_detected_but_our_own_saves_are_not()
    {
        var store = Store();
        store.Save(new AppSettings());
        Assert.False(store.ChangedOnDisk());
        File.WriteAllText(store.Path, File.ReadAllText(store.Path).Replace("\"skinTone\": 0", "\"skinTone\": 3", StringComparison.Ordinal));
        Assert.True(store.ChangedOnDisk());
        Assert.Equal(3, store.Load().Value.SkinTone);
        Assert.False(store.ChangedOnDisk());
    }

    [Fact]
    public void Normalize_clamps_broken_hand_edits()
    {
        var s = new AppSettings { MinimumCharacters = 99, SkinTone = -3, TriggerCharacter = "a", PickerExcludedApps = [" Slack.EXE ", "", "slack.exe"] }.Normalize();
        Assert.Equal(AppSettings.MaxMinimumCharacters, s.MinimumCharacters);
        Assert.Equal(0, s.SkinTone);
        Assert.Equal(":", s.TriggerCharacter);
        Assert.Equal(["slack.exe"], s.PickerExcludedApps);
    }

    [Fact]
    public void Frecency_decays_with_a_half_life_and_accumulates()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var f = new Frecency();
        f.Record("😂", now);
        f.Record("😂", now);
        Assert.Equal(2, f.Score("😂", now), 3);
        Assert.Equal(1, f.Score("😂", now + Frecency.HalfLife), 3);
        Assert.Equal(0, f.Score("🔥", now));

        var reloaded = new Frecency(f.ToFile(now));
        Assert.Equal(2, reloaded.Score("😂", now), 3);
        Assert.Empty(new Frecency(f.ToFile(now + (Frecency.HalfLife * 10))).All(now)); // forgotten when negligible
    }

    [Fact]
    public void Snippet_validation()
    {
        Func<string, bool> emoji = n => n == "smile";
        Assert.True(EntryValidation.HasErrors(EntryValidation.ValidateSnippet("", "x", [], emoji)));
        Assert.True(EntryValidation.HasErrors(EntryValidation.ValidateSnippet("my site", "x", [], emoji)));
        Assert.True(EntryValidation.HasErrors(EntryValidation.ValidateSnippet("Website", "x", ["website"], emoji)));
        Assert.True(EntryValidation.HasErrors(EntryValidation.ValidateSnippet("site", "", [], emoji)));

        var collision = EntryValidation.ValidateSnippet("smile", "x", [], emoji);
        Assert.False(EntryValidation.HasErrors(collision));
        Assert.Contains(collision, m => m.Severity == Severity.Warning && m.Text.Contains(":smile:", StringComparison.Ordinal));

        var placeholder = EntryValidation.ValidateSnippet("sig", "Sent {dat}", [], emoji);
        Assert.False(EntryValidation.HasErrors(placeholder));
        Assert.Contains(placeholder, m => m.Text.Contains("{dat}", StringComparison.Ordinal));

        Assert.Empty(EntryValidation.ValidateSnippet("website", "https://example.com/ {date}", ["email"], emoji));
    }

    [Fact]
    public void Auto_replace_validation()
    {
        Assert.True(EntryValidation.HasErrors(EntryValidation.ValidateAutoReplace("", "x", [])));
        Assert.True(EntryValidation.HasErrors(EntryValidation.ValidateAutoReplace("c m", "x", [])));
        Assert.True(EntryValidation.HasErrors(EntryValidation.ValidateAutoReplace("e.g", "x", [])));
        Assert.True(EntryValidation.HasErrors(EntryValidation.ValidateAutoReplace("CMQ", "x", ["cmq"])));
        Assert.True(EntryValidation.HasErrors(EntryValidation.ValidateAutoReplace("cmq", "", [])));

        foreach (var common in new[] { "the", "a", "il", "che" })
        {
            var m = EntryValidation.ValidateAutoReplace(common, "x", []);
            Assert.False(EntryValidation.HasErrors(m));
            Assert.Contains(m, x => x.Severity == Severity.Warning);
        }

        Assert.Empty(EntryValidation.ValidateAutoReplace("cmq", "comunque", ["pk"]));
    }
}
