using System.Net;
using Comet.Core.Settings;
using Comet.Core.Updates;

namespace Comet.Platform.Updates;

/// <summary>
/// The weekly "is there a newer Comet?" check: a conditional GET of GitHub's latest-release API. It only tells;
/// nothing is downloaded or installed. Never throws for network problems.
/// </summary>
public sealed class AppUpdateChecker(HttpClient http, Uri latestReleaseApi, Version currentVersion)
{
    /// <summary><c>COMET_APP_UPDATE_URL</c> overrides the API address (tests use a local HTTP server).</summary>
    public static Uri Endpoint =>
        Environment.GetEnvironmentVariable("COMET_APP_UPDATE_URL") is { Length: > 0 } url ? new(url) : AppPaths.LatestReleaseApi;

    /// <summary>
    /// One check. Updates <paramref name="state"/> (ETag, last checked, newest version seen) for the caller to save,
    /// and returns the newer release, if there is one (also on a 304, from the state).
    /// </summary>
    public async Task<(UpdateOutcome Outcome, AppRelease? Newer)> CheckAsync(AppUpdateState state, Func<bool> networkUsable, CancellationToken cancel = default)
    {
        if (!networkUsable())
            return (UpdateOutcome.Skipped, Known(state));

        try
        {
            var (status, body, etag) = await UpdateHttp.GetAsync(http, latestReleaseApi, state.ETag, cancel).ConfigureAwait(false);
            state.LastChecked = DateTimeOffset.UtcNow;
            if (status == HttpStatusCode.NotModified)
                return (UpdateOutcome.NotModified, Known(state));
            if (body is null)
                return (UpdateOutcome.Failed, Known(state)); // 404 before the first release, rate limits: try next week

            state.ETag = etag;
            var release = AppRelease.Parse(body);
            state.LatestVersion = release?.DisplayVersion;
            state.LatestUrl = release?.Page.AbsoluteUri;
            return (UpdateOutcome.UpToDate, Known(state));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            state.LastChecked = DateTimeOffset.UtcNow;
            return (UpdateOutcome.Failed, Known(state));
        }
    }

    /// <summary>The newest release recorded in the state, when it is newer than this Comet.</summary>
    public AppRelease? Known(AppUpdateState state)
    {
        if (state.LatestVersion is not { } v || state.LatestUrl is not { } u)
            return null;
        if (!AppRelease.TryParseTag(v, out var version, out var display) || !Uri.TryCreate(u, UriKind.Absolute, out var page))
            return null;
        // The Download button opens this page: only ever a GitHub page, even if the state file was edited.
        if (page.Scheme != Uri.UriSchemeHttps || !page.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return null;
        var release = new AppRelease(version, display, page);
        return release.IsNewerThan(currentVersion) ? release : null;
    }
}
