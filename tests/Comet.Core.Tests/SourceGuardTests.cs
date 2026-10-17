using System.Text.RegularExpressions;

namespace Comet.Core.Tests;

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
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Comet.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Comet.sln not found above the test folder.");
    }

    [Fact]
    public void App_code_never_looks_theme_resources_up_by_key()
    {
        var app = Path.Combine(RepoRoot(), "src", "Comet.App");
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
}
