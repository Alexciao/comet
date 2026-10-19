namespace Inlay.Core.Settings;

/// <summary>Repairs what a hand edit of the data files may have broken, so the rest of Inlay can trust the lists.</summary>
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
    public static List<ShorthandDefinition> Shorthands(List<ShorthandDefinition>? list)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var clean = new List<ShorthandDefinition>();
        foreach (var r in list ?? [])
        {
            if (r is null || string.IsNullOrWhiteSpace(r.Shorthand))
                continue;
            r.Expansion ??= "";
            if (string.IsNullOrEmpty(r.Id) || !ids.Add(r.Id))
                ids.Add(r.Id = Guid.NewGuid().ToString("N"));
            clean.Add(r);
        }

        return clean;
    }

    /// <summary>
    /// Drops null entries, images without a library file, folders and collections without a folder, and named
    /// kinds without a name; clamps the pick count; gives repeated or missing ids a new one.
    /// </summary>
    public static List<ImageEntry> Images(List<ImageEntry>? list)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var clean = new List<ImageEntry>();
        foreach (var e in list ?? [])
        {
            if (e is null || !Enum.IsDefined(e.Kind))
                continue;
            if (e.Kind == ImageEntryKind.Image ? string.IsNullOrWhiteSpace(e.File) : string.IsNullOrWhiteSpace(e.Folder))
                continue;
            if (e.Kind != ImageEntryKind.Collection && string.IsNullOrWhiteSpace(e.Name))
                continue;
            e.Name = (e.Name ?? "").Trim();
            e.Keywords ??= "";
            e.Prefix = (e.Prefix ?? "").Trim();
            e.FileTypes = [.. (e.FileTypes ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim().TrimStart('.').ToLowerInvariant()).Distinct()];
            e.PickCount = Math.Clamp(e.PickCount, ImageEntry.MinPickCount, ImageEntry.MaxPickCount);
            if (!Enum.IsDefined(e.Mode))
                e.Mode = FolderMode.Newest;
            if (!Enum.IsDefined(e.InsertAs))
                e.InsertAs = ImageInsertAs.Image;
            if (e.File is { } file)
                e.File = Path.GetFileName(file); // library files only: never a path out of the library
            if (string.IsNullOrEmpty(e.Id) || !ids.Add(e.Id))
                ids.Add(e.Id = Guid.NewGuid().ToString("N"));
            clean.Add(e);
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
