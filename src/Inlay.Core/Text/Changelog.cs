namespace Inlay.Core.Text;

/// <summary>Reads CHANGELOG.md in the Keep a Changelog format (<c>## [1.1.0] - 2026-10-20</c> per version).</summary>
public static class Changelog
{
    /// <summary>
    /// The body of one version's section ("1.1.0", "v1.1", or "Unreleased"), without its heading, the link
    /// reference lines and the blank lines around it; null when the changelog has no such section.
    /// </summary>
    public static string? Section(string changelog, string version)
    {
        var lines = changelog.ReplaceLineEndings("\n").Split('\n');
        var start = -1;
        var end = lines.Length;
        for (var i = 0; i < lines.Length; i++)
        {
            if (SectionName(lines[i]) is not { } name)
                continue;
            if (start >= 0)
            {
                end = i;
                break;
            }

            if (SameVersion(name, version))
                start = i + 1;
        }

        if (start < 0)
            return null;
        var body = lines[start..end].Where(l => !Markdown.LinkDefinition().IsMatch(l)).ToList();
        while (body.Count > 0 && string.IsNullOrWhiteSpace(body[^1]))
            body.RemoveAt(body.Count - 1);
        return string.Join('\n', body.SkipWhile(string.IsNullOrWhiteSpace));
    }

    /// <summary>"1.1.0" for <c>## [1.1.0] - 2026-10-20</c> (or <c>## 1.1.0</c>); "" for any other level-1 or 2 heading, which also ends a section.</summary>
    private static string? SectionName(string line)
    {
        if (!line.StartsWith('#'))
            return null;
        var hashes = line.TakeWhile(c => c == '#').Count();
        if (hashes > 2 || hashes == line.Length || line[hashes] != ' ')
            return null;
        var title = line[hashes..].Trim();
        if (hashes == 1)
            return "";
        if (title.StartsWith('[') && title.IndexOf(']', StringComparison.Ordinal) is var close and > 0)
            return title[1..close].Trim();
        var space = title.IndexOfAny([' ', '\t']);
        return space < 0 ? title : title[..space];
    }

    private static bool SameVersion(string a, string b)
    {
        a = a.TrimStart('v', 'V');
        b = b.TrimStart('v', 'V');
        if (Version.TryParse(a, out var x) && Version.TryParse(b, out var y))
            return Normalize(x) == Normalize(y);
        return a.Length > 0 && a.Equals(b, StringComparison.OrdinalIgnoreCase);
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}
