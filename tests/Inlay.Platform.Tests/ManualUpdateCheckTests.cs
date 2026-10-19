using Inlay.Core.Emoji;
using Inlay.Core.Settings;
using Inlay.Core.Updates;
using Inlay.Platform.Updates;

namespace Inlay.Platform.Tests;

public sealed class ManualUpdateCheckTests
{
    private static Task<(UpdateOutcome, AppRelease?)> App(UpdateOutcome outcome, string? newer = null) =>
        Task.FromResult((outcome, newer is null ? null : new AppRelease(new Version(newer), newer, new Uri("https://github.com/Alexciao/inlay/releases/tag/v" + newer))));

    private static Task<(UpdateResult, int)?> Emoji(UpdateOutcome outcome, int version) =>
        Task.FromResult<(UpdateResult, int)?>((new UpdateResult(outcome, ""), version));

    [Fact]
    public async Task A_manual_check_runs_both_checks_with_both_automatic_toggles_off()
    {
        var settings = new AppSettings { NotifyAppUpdates = false, UpdateEmojiAutomatically = false };
        Assert.False(AppUpdatePolicy.Enabled(settings, paused: false));
        Assert.False(EmojiUpdatePolicy.Enabled(settings, paused: false));

        var ran = 0;
        var result = await ManualUpdateCheck.RunAsync(
            () => { Interlocked.Increment(ref ran); return App(UpdateOutcome.UpToDate, "1.1.1"); },
            () => { Interlocked.Increment(ref ran); return Emoji(UpdateOutcome.NotModified, 2026101601); });

        Assert.Equal(2, ran);
        Assert.Equal(["Inlay 1.1.1 is available", "Emoji data is up to date (version 2026101601)"], result.Lines);
        Assert.Equal("1.1.1", result.Available!.DisplayVersion);
    }

    [Fact]
    public async Task A_failed_check_shows_one_message_and_never_throws()
    {
        var offline = await ManualUpdateCheck.RunAsync(
            () => App(UpdateOutcome.Failed),
            () => throw new HttpRequestException("offline"));
        Assert.Equal([ManualUpdateCheck.FailedMessage], offline.Lines);
        Assert.Null(offline.Available);

        var partial = await ManualUpdateCheck.RunAsync(() => App(UpdateOutcome.UpToDate), () => Emoji(UpdateOutcome.Updated, 7));
        Assert.Equal(["Inlay is up to date", "Emoji data updated to version 7"], partial.Lines);
    }
}
