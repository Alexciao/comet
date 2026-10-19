using Inlay.Core.Text;
using Inlay.Platform;

namespace Inlay.App.UI;

/// <summary>
/// The installed version's release notes: its section of CHANGELOG.md, which the build embeds whole as the
/// resource <see cref="ResourceName"/>. Read once, on first use.
/// </summary>
internal static class ReleaseNotes
{
    public const string ResourceName = "ReleaseNotes.md";

    private static readonly Lazy<string?> s_installed = new(Load);

    /// <summary>This version's notes (Markdown), or null when the changelog has none (local builds are 0.0.0).</summary>
    public static string? Installed => s_installed.Value;

    private static string? Load()
    {
        using var stream = typeof(ReleaseNotes).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
            return null;
        using var reader = new StreamReader(stream);
        var notes = Changelog.Section(reader.ReadToEnd(), AppPaths.Version);
        return string.IsNullOrWhiteSpace(notes) ? null : notes;
    }
}
