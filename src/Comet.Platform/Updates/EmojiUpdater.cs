using System.Net;
using System.Net.Http.Headers;
using Comet.Core.Emoji;
using Comet.Core.Settings;
using Windows.Networking.Connectivity;

namespace Comet.Platform.Updates;

/// <summary>What one update check did.</summary>
public enum UpdateOutcome
{
    /// <summary>The server said 304 Not Modified (ETag matched).</summary>
    NotModified,
    /// <summary>A manifest was read but its data isn't newer (or not for this app).</summary>
    UpToDate,
    /// <summary>A new pack was verified and written; <see cref="UpdateResult.Data"/> holds it.</summary>
    Updated,
    /// <summary>A pack was downloaded but failed verification and was discarded.</summary>
    Rejected,
    /// <summary>Metered or no connection: nothing was sent.</summary>
    Skipped,
    /// <summary>Network or disk error.</summary>
    Failed,
}

public sealed record UpdateResult(UpdateOutcome Outcome, string Detail, EmojiData? Data = null);

/// <summary>
/// Silent emoji data updates: a conditional GET of a small manifest, and only when it announces newer data, a
/// download of the pack, verified (size, SHA-256, format, loads with the regular reader, no big count drop) and
/// written atomically next to a backup. No UI, no retries: a failed check simply waits for the next one.
/// </summary>
public sealed class EmojiUpdater
{
    public const string PackFileName = "emoji-data.bin";
    public const string ManifestFileName = "manifest.json";

    /// <summary>
    /// Where packs are published: the fixed <c>emoji-data</c> release of the repository (see BLOCKERS.md).
    /// <c>COMET_EMOJI_UPDATE_URL</c> overrides it (tests serve packs from a local HTTP server).
    /// </summary>
    public const string DefaultEndpoint = "https://github.com/Alexciao/comet/releases/download/emoji-data/";

    private readonly HttpClient _http;
    private readonly Uri _base;
    private readonly string _directory;
    private readonly Version _appVersion;

    public EmojiUpdater(HttpClient http, Uri endpoint, string dataDirectory, Version appVersion)
    {
        _http = http;
        _base = endpoint.AbsoluteUri.EndsWith('/') ? endpoint : new Uri(endpoint.AbsoluteUri + "/");
        _directory = dataDirectory;
        _appVersion = appVersion;
    }

    public static Uri Endpoint =>
        new(Environment.GetEnvironmentVariable("COMET_EMOJI_UPDATE_URL") is { Length: > 0 } url ? url : DefaultEndpoint);

    public string PackPath => Path.Combine(_directory, PackFileName);

    /// <summary>
    /// One check. Updates <paramref name="state"/> (ETag, last checked) for the caller to save; never throws for
    /// network or data problems. <paramref name="networkUsable"/> is consulted first (metered or offline: skip).
    /// </summary>
    public async Task<UpdateResult> CheckAsync(EmojiUpdateState state, int currentDataVersion, int currentEmojiCount, Func<bool> networkUsable, CancellationToken cancel = default)
    {
        if (!networkUsable())
            return new UpdateResult(UpdateOutcome.Skipped, "metered or offline connection");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_base, ManifestFileName));
            if (state.ETag is { Length: > 0 } etag && EntityTagHeaderValue.TryParse(etag, out var tag))
                request.Headers.IfNoneMatch.Add(tag);
            using var response = await _http.SendAsync(request, cancel).ConfigureAwait(false);
            state.LastChecked = DateTimeOffset.UtcNow;
            if (response.StatusCode == HttpStatusCode.NotModified)
                return new UpdateResult(UpdateOutcome.NotModified, "not modified");
            if (!response.IsSuccessStatusCode)
                return new UpdateResult(UpdateOutcome.Failed, $"manifest: HTTP {(int)response.StatusCode}");

            var manifest = EmojiPackManifest.Parse(await response.Content.ReadAsByteArrayAsync(cancel).ConfigureAwait(false));
            if (manifest is null)
                return new UpdateResult(UpdateOutcome.Failed, "manifest unreadable");

            var verdict = EmojiPack.CheckManifest(manifest, currentDataVersion, _appVersion);
            if (verdict != PackVerdict.Accept)
            {
                // Remember the ETag only once nothing more can come of this manifest for this app.
                state.ETag = response.Headers.ETag?.ToString();
                return new UpdateResult(UpdateOutcome.UpToDate, $"manifest {manifest.DataVersion}: {verdict}");
            }

            var pack = await _http.GetByteArrayAsync(new Uri(_base, PackFileName), cancel).ConfigureAwait(false);
            verdict = EmojiPack.CheckPack(pack, manifest, currentEmojiCount);
            if (verdict != PackVerdict.Accept)
                return new UpdateResult(UpdateOutcome.Rejected, $"pack {manifest.DataVersion}: {verdict}");

            WriteAtomically(pack);
            var data = EmojiData.FromBytes(pack);
            state.ETag = response.Headers.ETag?.ToString();
            return new UpdateResult(UpdateOutcome.Updated, $"pack {manifest.DataVersion}: {data.Count} emoji", data);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            state.LastChecked = DateTimeOffset.UtcNow;
            return new UpdateResult(UpdateOutcome.Failed, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>Temp file, then the old pack becomes the backup and the new one takes its place.</summary>
    private void WriteAtomically(byte[] pack)
    {
        Directory.CreateDirectory(_directory);
        var temp = PackPath + ".tmp";
        File.WriteAllBytes(temp, pack);
        if (File.Exists(PackPath))
            File.Replace(temp, PackPath, PackPath + ".bak", ignoreMetadataErrors: true);
        else
            File.Move(temp, PackPath);
    }

    /// <summary>
    /// The dataset to start with: a downloaded pack when it is valid and newer than the embedded data, otherwise the
    /// embedded data. A corrupt download is deleted quietly (one log line).
    /// </summary>
    public static EmojiData LoadStartupData(string dataDirectory)
    {
        var embedded = EmojiData.LoadEmbedded();
        var path = Path.Combine(dataDirectory, PackFileName);
        if (!File.Exists(path))
            return embedded;

        try
        {
            var chosen = EmojiPack.ChooseDownloaded(File.ReadAllBytes(path), embedded.DataVersion, out var corrupt);
            if (corrupt)
            {
                Log.Info("emoji data: the downloaded pack is corrupt; deleted, using the built-in data");
                File.Delete(path);
            }

            if (chosen is null)
                return embedded;
            embedded.Dispose();
            return chosen;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Info($"emoji data: could not read the downloaded pack ({ex.GetType().Name}); using the built-in data");
            return embedded;
        }
    }

    /// <summary>True when the internet connection is up and not metered (or roaming, or over its data limit).</summary>
    public static bool NetworkUsable()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null || profile.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.InternetAccess)
                return false;
            var cost = profile.GetConnectionCost();
            return cost.NetworkCostType is NetworkCostType.Unrestricted or NetworkCostType.Unknown
                && !cost.Roaming && !cost.OverDataLimit && !cost.ApproachingDataLimit;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return false;
        }
    }
}
