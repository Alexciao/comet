using System.Text.Json;
using Inlay.Core.Settings;

namespace Inlay.Core.Updates;

/// <summary>The parts of GitHub's "latest release" answer that the update notification needs.</summary>
public sealed record AppRelease(Version Version, string DisplayVersion, Uri Page)
{
    /// <summary>
    /// Reads <c>GET /repos/{owner}/{repo}/releases/latest</c>. Returns null for anything that isn't a published,
    /// non-prerelease release with a <c>v1.2.3</c>-style tag and an https page on github.com.
    /// </summary>
    public static AppRelease? Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            if (Bool(root, "draft") || Bool(root, "prerelease"))
                return null;
            if (!root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String)
                return null;
            if (!root.TryGetProperty("html_url", out var url) || url.ValueKind != JsonValueKind.String)
                return null;
            if (TryParseTag(tag.GetString()!, out var version, out var display)
                && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var page)
                && page.Scheme == Uri.UriSchemeHttps
                && page.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
                return new AppRelease(version, display, page);
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>"v1.3.0" or "1.3" → 1.3.0. Pre-release suffixes ("1.3.0-beta") are not offered.</summary>
    public static bool TryParseTag(string tag, out Version version, out string display)
    {
        var text = tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
        version = new Version(0, 0, 0);
        display = text;
        if (!Version.TryParse(text, out var parsed) || parsed.Major < 0)
            return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
        display = version.ToString(3);
        return true;
    }

    /// <summary>True when this release is newer than the running version.</summary>
    public bool IsNewerThan(Version current) =>
        Version > new Version(current.Major, current.Minor, Math.Max(current.Build, 0));

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>When the app may check for a newer Inlay (the timing is <see cref="UpdateSchedule"/>).</summary>
public static class AppUpdatePolicy
{
    public static bool Enabled(AppSettings settings, bool paused) => settings.NotifyAppUpdates && !paused;
}
