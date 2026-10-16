using System.Text.Json;

namespace Comet.Core.Settings;

/// <summary>Import/export of the snippet and auto-replace lists (same JSON format as the data files).</summary>
public static class ImportExport
{
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
