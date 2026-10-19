using Inlay.Core.Updates;

namespace Inlay.Platform.Updates;

/// <summary>What the "Check for updates" button shows: one line per check, and the newer release when there is one.</summary>
public sealed record ManualCheckResult(IReadOnlyList<string> Lines, AppRelease? Available);

/// <summary>
/// The "Check for updates" button in Settings › General. Runs the app check and the emoji data check together, once,
/// whatever the automatic toggles and the daily schedule say: the callers pass the same checkers the daily checks
/// use, and neither this class nor they look at those settings. Never throws; a check that fails shows
/// <see cref="FailedMessage"/>.
/// </summary>
public static class ManualUpdateCheck
{
    public const string FailedMessage = "Couldn't check for updates. Try again later.";

    /// <param name="app">The app update check.</param>
    /// <param name="emoji">The emoji data check, with the data version in use afterwards; null when there is no emoji data to check (Emoji is off).</param>
    public static async Task<ManualCheckResult> RunAsync(
        Func<Task<(UpdateOutcome Outcome, AppRelease? Newer)>> app,
        Func<Task<(UpdateResult Result, int DataVersion)?>> emoji)
    {
        var appTask = Try(app, (UpdateOutcome.Failed, null));
        var emojiTask = Try(emoji, (new UpdateResult(UpdateOutcome.Failed, "check failed"), 0));
        var (outcome, newer) = await appTask.ConfigureAwait(false);
        var emojiResult = await emojiTask.ConfigureAwait(false);

        var lines = new List<string>();
        var ok = outcome is UpdateOutcome.UpToDate or UpdateOutcome.NotModified;
        lines.Add(!ok ? FailedMessage : newer is null ? "Inlay is up to date" : $"Inlay {newer.DisplayVersion} is available");
        if (emojiResult is var (result, version))
        {
            lines.Add(result.Outcome switch
            {
                UpdateOutcome.Updated => $"Emoji data updated to version {version}",
                UpdateOutcome.UpToDate or UpdateOutcome.NotModified => $"Emoji data is up to date (version {version})",
                _ => FailedMessage,
            });
        }

        // Both failing (offline) is one message, not two.
        return new ManualCheckResult([.. lines.Distinct()], ok ? newer : null);
    }

    private static async Task<T> Try<T>(Func<Task<T>> check, T failed)
    {
        try
        {
            return await check().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return failed;
        }
    }
}
