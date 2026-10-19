using System.Text.Json;

namespace Comet.Core.Settings;

/// <summary>Import/export of the snippet and auto-replace lists (same JSON format as the data files) and of full backups.</summary>
public static class ImportExport
{
    /// <summary>Settings, snippets and replacements in one file (Settings › General › Backup).</summary>
    public static string ExportBackup(AppSettings settings, IEnumerable<SnippetDefinition> snippets, IEnumerable<AutoReplaceDefinition> entries) =>
        JsonSerializer.Serialize(new BackupFile { Kind = BackupFile.BackupKind, Settings = settings, Snippets = [.. snippets], AutoReplace = [.. entries] }, CometJsonContext.Default.BackupFile);

    /// <summary>
    /// Reads a backup. Missing parts come back empty (or as defaults for the settings); values a hand edit broke
    /// are clamped; entries without a name or abbreviation are dropped.
    /// </summary>
    /// <exception cref="FormatException">The file isn't a Comet backup.</exception>
    public static BackupFile ImportBackup(string json)
    {
        var file = Parse(json, CometJsonContext.Default.BackupFile);
        if (!string.Equals(file.Kind, BackupFile.BackupKind, StringComparison.Ordinal))
            throw new FormatException("The file isn't a Comet backup.");
        if (file.SchemaVersion > BackupFile.CurrentVersion)
            throw new FormatException("The backup was made by a newer version of Comet.");
        file.Settings = (file.Settings ?? new AppSettings()).Normalize();
        file.Snippets = [.. (file.Snippets ?? []).Where(s => !string.IsNullOrWhiteSpace(s?.Name)).Select(s => new SnippetDefinition { Name = s.Name.Trim(), Text = s.Text ?? "" }).DistinctBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
        file.AutoReplace = [.. (file.AutoReplace ?? []).Where(r => !string.IsNullOrWhiteSpace(r?.Abbreviation))
            .Select(r => new AutoReplaceDefinition { Abbreviation = r.Abbreviation.Trim(), Replacement = r.Replacement ?? "", MatchCase = r.MatchCase, Enabled = r.Enabled }).DistinctBy(r => r.Abbreviation, StringComparer.OrdinalIgnoreCase)];
        return file;
    }

    public static string ExportSnippets(IEnumerable<SnippetDefinition> snippets) =>
        JsonSerializer.Serialize(new SnippetsFile { Snippets = [.. snippets] }, CometJsonContext.Default.SnippetsFile);

    public static string ExportAutoReplace(IEnumerable<AutoReplaceDefinition> entries) =>
        JsonSerializer.Serialize(new AutoReplaceFile { Entries = [.. entries] }, CometJsonContext.Default.AutoReplaceFile);

    /// <summary>
    /// Merges imported snippets into <paramref name="existing"/>: an imported snippet replaces one with the same
    /// name (case-insensitive); the rest are added. Returns how many were added and replaced.
    /// </summary>
    /// <exception cref="FormatException">The file isn't a snippet export.</exception>
    public static (int Added, int Replaced) ImportSnippets(string json, List<SnippetDefinition> existing)
    {
        var file = Parse(json, CometJsonContext.Default.SnippetsFile);
        int added = 0, replaced = 0;
        foreach (var s in file.Snippets ?? [])
        {
            if (string.IsNullOrWhiteSpace(s.Name))
                continue;
            var name = s.Name.Trim();
            var item = new SnippetDefinition { Name = name, Text = s.Text ?? "" };
            var index = existing.FindIndex(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                item.Id = existing[index].Id;
                existing[index] = item;
                replaced++;
            }
            else
            {
                existing.Add(item);
                added++;
            }
        }

        return (added, replaced);
    }

    /// <inheritdoc cref="ImportSnippets"/>
    public static (int Added, int Replaced) ImportAutoReplace(string json, List<AutoReplaceDefinition> existing)
    {
        var file = Parse(json, CometJsonContext.Default.AutoReplaceFile);
        int added = 0, replaced = 0;
        foreach (var r in file.Entries ?? [])
        {
            if (string.IsNullOrWhiteSpace(r.Abbreviation))
                continue;
            var abbreviation = r.Abbreviation.Trim();
            var item = new AutoReplaceDefinition { Abbreviation = abbreviation, Replacement = r.Replacement ?? "", MatchCase = r.MatchCase, Enabled = r.Enabled };
            var index = existing.FindIndex(e => string.Equals(e.Abbreviation, abbreviation, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                item.Id = existing[index].Id;
                existing[index] = item;
                replaced++;
            }
            else
            {
                existing.Add(item);
                added++;
            }
        }

        return (added, replaced);
    }

    private static T Parse<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        try
        {
            return JsonSerializer.Deserialize(json, type) ?? throw new FormatException("The file is empty.");
        }
        catch (JsonException ex)
        {
            throw new FormatException("The file isn't a Comet export.", ex);
        }
    }
}
