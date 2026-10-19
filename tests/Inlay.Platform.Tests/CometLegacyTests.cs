namespace Inlay.Platform.Tests;

/// <summary>Moving Comet 1.0's data folder to Inlay's on first run.</summary>
public sealed class CometLegacyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inlay-legacy-" + Guid.NewGuid().ToString("N")[..8]);

    private string Comet => Path.Combine(_root, "Comet");

    private string Inlay => Path.Combine(_root, "Inlay");

    public CometLegacyTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void SeedComet()
    {
        Directory.CreateDirectory(Path.Combine(Comet, "logs"));
        File.WriteAllText(Path.Combine(Comet, "settings.json"), """{ "schemaVersion": 1 }""");
        File.WriteAllText(Path.Combine(Comet, "autoreplace.json"), """{ "schemaVersion": 1, "entries": [] }""");
        File.WriteAllBytes(Path.Combine(Comet, "emoji-data.bin"), [.. Enumerable.Range(0, 5000).Select(i => (byte)i)]);
        File.WriteAllText(Path.Combine(Comet, "logs", "comet.log"), "log");
    }

    [Fact]
    public void Moves_the_folder_and_keeps_a_backup_until_the_data_was_read()
    {
        SeedComet();

        var (outcome, error) = CometLegacy.MigrateDataFolder(Comet, Inlay);

        Assert.Equal(CometLegacy.MigrationOutcome.Migrated, outcome);
        Assert.Null(error);
        Assert.Equal("""{ "schemaVersion": 1 }""", File.ReadAllText(Path.Combine(Inlay, "settings.json")));
        Assert.Equal(5000, new FileInfo(Path.Combine(Inlay, "emoji-data.bin")).Length);
        Assert.True(File.Exists(Path.Combine(Inlay, "logs", "comet.log")));
        Assert.False(Directory.Exists(Comet));
        Assert.True(Directory.Exists(Comet + CometLegacy.BackupSuffix));
        Assert.False(Directory.Exists(Inlay + ".migrating"));

        Assert.True(CometLegacy.DeleteBackup(Comet));
        Assert.False(Directory.Exists(Comet + CometLegacy.BackupSuffix));
    }

    [Fact]
    public void Leaves_comet_alone_when_inlay_already_has_data()
    {
        SeedComet();
        Directory.CreateDirectory(Inlay);
        File.WriteAllText(Path.Combine(Inlay, "settings.json"), "{}");

        Assert.Equal(CometLegacy.MigrationOutcome.AlreadyMigrated, CometLegacy.MigrateDataFolder(Comet, Inlay).Outcome);
        Assert.True(File.Exists(Path.Combine(Comet, "settings.json")));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(Inlay, "settings.json")));
    }

    [Fact]
    public void Nothing_to_do_without_comet() =>
        Assert.Equal(CometLegacy.MigrationOutcome.NothingToDo, CometLegacy.MigrateDataFolder(Comet, Inlay).Outcome);

    [Fact]
    public void A_locked_file_fails_cleanly_and_changes_nothing()
    {
        SeedComet();
        using (new FileStream(Path.Combine(Comet, "settings.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var (outcome, error) = CometLegacy.MigrateDataFolder(Comet, Inlay);

            Assert.Equal(CometLegacy.MigrationOutcome.Failed, outcome);
            Assert.NotNull(error);
        }

        Assert.False(Directory.Exists(Inlay));
        Assert.False(Directory.Exists(Inlay + ".migrating"));
        Assert.True(File.Exists(Path.Combine(Comet, "settings.json")));

        // Next start (the file isn't locked any more) it works.
        Assert.Equal(CometLegacy.MigrationOutcome.Migrated, CometLegacy.MigrateDataFolder(Comet, Inlay).Outcome);
    }
}
