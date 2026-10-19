using System.Text;
using Comet.Core.Emoji;
using Comet.Core.Search;
using Comet.Core.Settings;
using Comet.Core.Updates;

namespace Comet.Core.Tests.Emoji;

public sealed class EmojiPackTests
{
    private static readonly Version s_app = new(1, 0, 0);

    /// <summary>A pack with <paramref name="count"/> emoji: the first one is 🔥 "fire", the rest are fillers.</summary>
    internal static byte[] Pack(int dataVersion, int count = 100, string extra = "")
    {
        var entries = new List<EmojiSourceEntry>
        {
            new("🔥", "fire", ["fire"], ["flame"], [], null, 1, 0, 0, false, true),
        };
        if (extra.Length > 0)
            entries.Add(new EmojiSourceEntry("🪿", extra, [extra], [], [], null, 2, 1, 0, false, true));
        for (var i = entries.Count; i < count; i++)
            entries.Add(new EmojiSourceEntry(char.ConvertFromUtf32(0x1F600 + i), $"filler {i}", [$"filler_{i}"], [], [], null, 10 + i, i, 0, false, true));
        return EmojiBlobWriter.Write(entries, dataVersion);
    }

    private static EmojiPackManifest ManifestFor(byte[] pack, string minApp = "1.0.0") => EmojiPackManifest.Describe(pack, minApp);

    [Fact]
    public void Manifest_round_trips_through_json()
    {
        var pack = Pack(2026101700);
        var manifest = ManifestFor(pack);
        Assert.Equal(manifest, EmojiPackManifest.Parse(manifest.ToJson()));
        Assert.Equal(EmojiData.SupportedFormatVersion, manifest.FormatVersion);
        Assert.Equal(2026101700, manifest.DataVersion);
        Assert.Equal(pack.Length, manifest.Size);
        Assert.Equal(100, manifest.EmojiCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"formatVersion":1,"dataVersion":5,"minAppVersion":"1.0.0","size":10,"sha256":"abc","emojiCount":1}""")]
    [InlineData("""{"formatVersion":1,"dataVersion":5,"size":10,"sha256":"0000000000000000000000000000000000000000000000000000000000000000","emojiCount":1}""")]
    public void Malformed_manifests_are_rejected(string json) => Assert.Null(EmojiPackManifest.Parse(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void Unknown_manifest_fields_are_ignored()
    {
        var json = """{"formatVersion":1,"dataVersion":5,"minAppVersion":"1.0.0","size":10,"sha256":"0000000000000000000000000000000000000000000000000000000000000000","emojiCount":1,"notes":"x"}""";
        Assert.NotNull(EmojiPackManifest.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Only_newer_data_is_worth_downloading()
    {
        var manifest = ManifestFor(Pack(200));
        Assert.Equal(PackVerdict.Accept, EmojiPack.CheckManifest(manifest, 100, s_app));
        Assert.Equal(PackVerdict.NotNewer, EmojiPack.CheckManifest(manifest, 200, s_app));
        Assert.Equal(PackVerdict.NotNewer, EmojiPack.CheckManifest(manifest, 300, s_app));
    }

    [Fact]
    public void A_format_too_new_or_a_newer_app_requirement_is_skipped()
    {
        var manifest = ManifestFor(Pack(200));
        Assert.Equal(PackVerdict.FormatTooNew, EmojiPack.CheckManifest(manifest with { FormatVersion = EmojiData.SupportedFormatVersion + 1 }, 100, s_app));
        Assert.Equal(PackVerdict.AppTooOld, EmojiPack.CheckManifest(manifest with { MinAppVersion = "1.1.0" }, 100, s_app));
        Assert.Equal(PackVerdict.AppTooOld, EmojiPack.CheckManifest(manifest with { MinAppVersion = "banana" }, 100, s_app));
        Assert.Equal(PackVerdict.Accept, EmojiPack.CheckManifest(manifest with { MinAppVersion = "0.9" }, 100, s_app));
    }

    [Fact]
    public void A_valid_pack_is_accepted()
    {
        var pack = Pack(200);
        Assert.Equal(PackVerdict.Accept, EmojiPack.CheckPack(pack, ManifestFor(pack), 100));
    }

    [Fact]
    public void A_hash_mismatch_is_rejected()
    {
        var pack = Pack(200);
        var manifest = ManifestFor(pack);
        pack[^1] ^= 0xFF;
        Assert.Equal(PackVerdict.HashMismatch, EmojiPack.CheckPack(pack, manifest, 100));
    }

    [Fact]
    public void A_size_mismatch_is_rejected()
    {
        var pack = Pack(200);
        Assert.Equal(PackVerdict.SizeMismatch, EmojiPack.CheckPack(pack.AsSpan(0, pack.Length - 1), ManifestFor(pack), 100));
    }

    [Fact]
    public void A_pack_whose_header_contradicts_the_manifest_is_rejected()
    {
        var pack = Pack(200);
        var other = ManifestFor(Pack(300));
        Assert.Equal(PackVerdict.ManifestMismatch, EmojiPack.CheckPack(pack, other with { Size = pack.Length, Sha256 = EmojiPack.Hash(pack) }, 100));
    }

    [Fact]
    public void A_pack_in_a_format_too_new_is_rejected()
    {
        var pack = Pack(200);
        BitConverter.TryWriteBytes(pack.AsSpan(4), EmojiData.SupportedFormatVersion + 1);
        Assert.Equal(PackVerdict.FormatTooNew, EmojiPack.CheckPack(pack, ManifestFor(Pack(200)) with { Size = pack.Length, Sha256 = EmojiPack.Hash(pack) }, 100));
    }

    [Fact]
    public void A_pack_that_does_not_load_is_rejected_even_with_a_matching_hash()
    {
        var pack = Pack(200);
        pack.AsSpan(EmojiBlob.HeaderFixedSize, 8).Fill(0xFF); // first section offset/count point outside the blob
        var manifest = new EmojiPackManifest(EmojiData.SupportedFormatVersion, 200, "1.0.0", pack.Length, EmojiPack.Hash(pack), 100);
        Assert.Equal(PackVerdict.Unreadable, EmojiPack.CheckPack(pack, manifest, 100));
    }

    [Theory]
    [InlineData(96, PackVerdict.Accept)]
    [InlineData(95, PackVerdict.Accept)]
    [InlineData(94, PackVerdict.CountDrop)]
    [InlineData(50, PackVerdict.CountDrop)]
    public void Losing_more_than_five_percent_of_the_emoji_is_rejected(int newCount, PackVerdict expected)
    {
        var pack = Pack(200, newCount);
        Assert.Equal(expected, EmojiPack.CheckPack(pack, ManifestFor(pack), 100));
    }

    [Fact]
    public void At_startup_a_newer_valid_download_wins()
    {
        using var chosen = EmojiPack.ChooseDownloaded(Pack(300), embeddedDataVersion: 200, out var corrupt);
        Assert.NotNull(chosen);
        Assert.Equal(300, chosen.DataVersion);
        Assert.False(corrupt);
    }

    [Fact]
    public void At_startup_an_older_download_loses_to_the_embedded_data_without_being_called_corrupt()
    {
        Assert.Null(EmojiPack.ChooseDownloaded(Pack(100), embeddedDataVersion: 200, out var corrupt));
        Assert.False(corrupt);
        Assert.Null(EmojiPack.ChooseDownloaded(Pack(200), embeddedDataVersion: 200, out corrupt));
        Assert.False(corrupt);
    }

    [Fact]
    public void At_startup_a_corrupt_download_falls_back_to_the_embedded_data_and_is_flagged_for_deletion()
    {
        Assert.Null(EmojiPack.ChooseDownloaded("garbage"u8, 200, out var corrupt));
        Assert.True(corrupt);

        var truncated = Pack(300);
        Assert.Null(EmojiPack.ChooseDownloaded(truncated.AsSpan(0, truncated.Length / 2), 200, out corrupt));
        Assert.True(corrupt);
    }

    [Fact]
    public void Search_over_a_new_pack_finds_its_new_emoji()
    {
        using var data = EmojiData.FromBytes(Pack(300, extra: "goose"));
        var engine = new SearchEngine(data, new SnippetIndex([]));
        var exact = engine.FindExact("goose");
        Assert.NotNull(exact);
        Assert.Equal("🪿", data.Emoji(exact.Value.Index).ToString());
    }

    // ---------------------------------------------------------------- schedule

    private static readonly DateTimeOffset s_now = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Updates_need_emoji_on_not_paused_and_the_toggle_on()
    {
        var s = new AppSettings();
        Assert.True(EmojiUpdatePolicy.Enabled(s, paused: false));
        Assert.False(EmojiUpdatePolicy.Enabled(s, paused: true));
        s.UpdateEmojiAutomatically = false;
        Assert.False(EmojiUpdatePolicy.Enabled(s, paused: false));
        s.UpdateEmojiAutomatically = true;
        s.Features.Emoji = false;
        Assert.False(EmojiUpdatePolicy.Enabled(s, paused: false));
    }

    [Fact]
    public void The_first_check_waits_two_minutes_after_start()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), UpdateSchedule.NextCheckIn(s_now, lastChecked: null, sinceStart: TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromSeconds(90), UpdateSchedule.NextCheckIn(s_now, null, TimeSpan.FromSeconds(30)));
        Assert.Equal(TimeSpan.Zero, UpdateSchedule.NextCheckIn(s_now, null, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Checks_happen_at_most_once_every_seven_days()
    {
        var hour = TimeSpan.FromHours(1);
        Assert.Equal(TimeSpan.FromDays(7) - hour, UpdateSchedule.NextCheckIn(s_now, s_now - hour, TimeSpan.FromDays(1)));
        Assert.Equal(TimeSpan.Zero, UpdateSchedule.NextCheckIn(s_now, s_now - TimeSpan.FromDays(8), TimeSpan.FromDays(1)));
        // Overdue at login: still not before the startup delay.
        Assert.Equal(TimeSpan.FromMinutes(2), UpdateSchedule.NextCheckIn(s_now, s_now - TimeSpan.FromDays(30), TimeSpan.Zero));
    }

    [Fact]
    public void A_last_check_in_the_future_does_not_postpone_beyond_one_interval()
    {
        Assert.Equal(TimeSpan.FromDays(7), UpdateSchedule.NextCheckIn(s_now, s_now + TimeSpan.FromDays(365), TimeSpan.FromDays(1)));
    }
}
