using System.Text.RegularExpressions;

namespace Inlay.Core.Tests;

/// <summary>
/// Patterns that work in Debug but crash the Native AOT build. The Settings pages once cast theme resources looked up
/// in code (<c>(Brush)Application.Current.Resources["…"]</c>); in the AOT build the cast throws, so every Add, Edit
/// and validation message crashed. Theme resources belong in XAML (<c>{ThemeResource …}</c>).
/// </summary>
public sealed partial class SourceGuardTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Inlay.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Inlay.sln not found above the test folder.");
    }

    [Fact]
    public void App_code_never_looks_theme_resources_up_by_key()
    {
        var app = Path.Combine(RepoRoot(), "src", "Inlay.App");
        var offenders = Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: Path.GetRelativePath(app, f), Line: i + 1, Text: line)))
            .Where(l => ResourceLookup().IsMatch(l.Text))
            .Select(l => $"{l.File}:{l.Line}: {l.Text.Trim()}")
            .ToList();
        Assert.True(offenders.Count == 0, "Use {ThemeResource} in XAML instead:\n" + string.Join('\n', offenders));
    }

    [GeneratedRegex(@"Resources\s*\[")]
    private static partial Regex ResourceLookup();

    /// <summary>
    /// Inlay talks to the network in exactly two places, the weekly emoji data check and the weekly app update check,
    /// both through <c>UpdateHttp</c> (README: "the only network requests are two optional weekly update checks").
    /// Any other HTTP client, socket or web API in the app's source fails this test.
    /// </summary>
    [Fact]
    public void Only_the_two_update_checks_touch_the_network()
    {
        string[] allowed =
        [
            Path.Combine("Inlay.Platform", "Updates", "UpdateHttp.cs"),      // the one client and the conditional GET
            Path.Combine("Inlay.Platform", "Updates", "EmojiUpdater.cs"),    // receives that client
            Path.Combine("Inlay.Platform", "Updates", "AppUpdateChecker.cs"), // receives that client
        ];
        var offenders = SourceLines()
            .Where(l => NetworkApi().IsMatch(l.Text) && !allowed.Contains(l.File))
            .Select(l => $"{l.File}:{l.Line}: {l.Text.Trim()}")
            .ToList();
        Assert.True(offenders.Count == 0, "Network access outside the two update checks:\n" + string.Join('\n', offenders));

        var creations = SourceLines().Where(l => l.Text.Contains("new HttpClient", StringComparison.Ordinal)).ToList();
        Assert.True(creations.Count == 1 && creations[0].File == allowed[0], "HttpClient must only be created in UpdateHttp:\n" + string.Join('\n', creations.Select(c => $"{c.File}:{c.Line}")));
    }

    /// <summary>The addresses Inlay knows are the four in AppPaths (repository, license, the two update endpoints).</summary>
    [Fact]
    public void Web_addresses_live_only_in_AppPaths()
    {
        var offenders = SourceLines()
            .Where(l => Url().IsMatch(l.Text) && l.File != Path.Combine("Inlay.Platform", "AppPaths.cs") && !l.Text.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Select(l => $"{l.File}:{l.Line}: {l.Text.Trim()}")
            .ToList();
        Assert.True(offenders.Count == 0, "Web addresses outside AppPaths:\n" + string.Join('\n', offenders));
    }

    private static IEnumerable<(string File, int Line, string Text)> SourceLines()
    {
        var src = Path.Combine(RepoRoot(), "src");
        return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: Path.GetRelativePath(src, f), Line: i + 1, Text: line)));
    }

    [GeneratedRegex(@"\b(HttpClient|HttpMessageHandler|SocketsHttpHandler|WebRequest|WebClient|HttpListener|WebSocket|TcpClient|UdpClient|Socket|Windows\.Web\.Http|Windows\.Networking\.Sockets|BackgroundDownloader)\b")]
    private static partial Regex NetworkApi();

    [GeneratedRegex(@"\b(https?|wss?|ftp)://", RegexOptions.IgnoreCase)]
    private static partial Regex Url();
}
