namespace Comet.Core.Settings;

/// <summary>Repairs what a hand edit of the data files may have broken, so the rest of Comet can trust the lists.</summary>
public static class Cleanup
{
    /// <summary>Drops null entries and entries without a name; gives repeated or missing ids a new one.</summary>
    public static List<SnippetDefinition> Snippets(List<SnippetDefinition>? list)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var clean = new List<SnippetDefinition>();
        foreach (var s in list ?? [])
        {
            if (s is null || string.IsNullOrWhiteSpace(s.Name))
                continue;
            s.Text ??= "";
            if (string.IsNullOrEmpty(s.Id) || !ids.Add(s.Id))
                ids.Add(s.Id = Guid.NewGuid().ToString("N"));
            clean.Add(s);
        }

        return clean;
    }

    /// <summary>Drops null entries and entries without an abbreviation; gives repeated or missing ids a new one.</summary>
    public static List<AutoReplaceDefinition> Replacements(List<AutoReplaceDefinition>? list)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var clean = new List<AutoReplaceDefinition>();
        foreach (var r in list ?? [])
        {
            if (r is null || string.IsNullOrWhiteSpace(r.Abbreviation))
                continue;
            r.Replacement ??= "";
            if (string.IsNullOrEmpty(r.Id) || !ids.Add(r.Id))
                ids.Add(r.Id = Guid.NewGuid().ToString("N"));
            clean.Add(r);
        }

        return clean;
    }

    /// <summary>A case-insensitive copy; keys differing only in case keep the last value (copying them in one go throws).</summary>
    public static Dictionary<string, string> CaseInsensitive(Dictionary<string, string>? source)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in source ?? [])
        {
            if (key is not null && value is not null)
                result[key] = value;
        }

        return result;
    }
}
