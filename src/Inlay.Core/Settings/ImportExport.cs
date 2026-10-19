using System.IO.Compression;
using System.Text.Json;

namespace Inlay.Core.Settings;

/// <summary>Import/export of the snippet and shorthands lists (same JSON format as the data files) and of full backups.</summary>
public static class ImportExport
{
    /// <summary>The backup's JSON (inside the <c>.inlay</c> zip as <see cref="BackupEntryName"/>).</summary>
    public static string ExportBackup(AppSettings settings, IEnumerable<SnippetDefinition> snippets, IEnumerable<ShorthandDefinition> entries, IEnumerable<ImageEntry>? images = null) =>
        JsonSerializer.Serialize(new BackupFile { Kind = BackupFile.BackupKind, Settings = settings, Snippets = [.. snippets], Shorthands = [.. entries], Images = images is null ? [] : [.. images] }, InlayJsonContext.Default.BackupFile);

    /// <summary>Name of the JSON entry in a <c>.inlay</c> zip; the image library sits next to it in <see cref="LibraryFolder"/>.</summary>
    public const string BackupEntryName = "backup.json";

    public const string LibraryFolder = "Images/";

    /// <summary>
    /// Writes a <c>.inlay</c> backup: a zip with <see cref="BackupEntryName"/> and the library files the image
    /// entries use (<paramref name="libraryDirectory"/>/<c>File</c>). Missing library files are skipped.
    /// </summary>
    public static void ExportBackupArchive(Stream output, string backupJson, IEnumerable<ImageEntry> images, string libraryDirectory)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var entry = zip.CreateEntry(BackupEntryName, CompressionLevel.Optimal);
        using (var writer = new StreamWriter(entry.Open(), new System.Text.UTF8Encoding(false)))
            writer.Write(backupJson);
        foreach (var file in images.Where(i => i.Kind == ImageEntryKind.Image && !string.IsNullOrEmpty(i.File)).Select(i => i.File!).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(libraryDirectory, Path.GetFileName(file));
            if (File.Exists(path))
                zip.CreateEntryFromFile(path, LibraryFolder + Path.GetFileName(file), CompressionLevel.NoCompression); // images are already compressed
        }
    }

    /// <summary>
    /// Reads a backup file: a <c>.inlay</c> zip, or a JSON backup (Comet 1.0's export, or a bare 1.1 JSON).
    /// Library files in the zip are copied into <paramref name="libraryDirectory"/> (identical names are identical
    /// content, so existing ones are kept).
    /// </summary>
    /// <exception cref="FormatException">The file isn't an Inlay or Comet backup.</exception>
    public static BackupFile ImportBackupFile(Stream input, string libraryDirectory)
    {
        using var buffer = new MemoryStream();
        input.CopyTo(buffer);
        var bytes = buffer.ToArray();
        if (bytes.Length < 4 || bytes[0] != (byte)'P' || bytes[1] != (byte)'K')
            return ImportBackup(System.Text.Encoding.UTF8.GetString(bytes));

        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var json = zip.GetEntry(BackupEntryName) ?? throw new FormatException("The file isn't an Inlay backup.");
            string text;
            using (var reader = new StreamReader(json.Open()))
                text = reader.ReadToEnd();
            var backup = ImportBackup(text);
            var wanted = new HashSet<string>((backup.Images ?? []).Where(i => i.File is not null).Select(i => i.File!), StringComparer.OrdinalIgnoreCase);
            Directory.CreateDirectory(libraryDirectory);
            foreach (var entry in zip.Entries)
            {
                var name = Path.GetFileName(entry.FullName);
                if (!entry.FullName.StartsWith(LibraryFolder, StringComparison.Ordinal) || name.Length == 0 || !wanted.Contains(name))
                    continue;
                var target = Path.Combine(libraryDirectory, name);
                if (!File.Exists(target))
                    entry.ExtractToFile(target);
            }

            return backup;
        }
        catch (InvalidDataException ex)
        {
            throw new FormatException("The file isn't an Inlay backup.", ex);
        }
    }

    /// <summary>
    /// Reads a backup's JSON (1.1, or Comet 1.0's). Missing parts come back empty (or as defaults for the
    /// settings); values a hand edit broke are clamped; entries without a name or shorthand are dropped.
    /// </summary>
    /// <exception cref="FormatException">The file isn't an Inlay backup.</exception>
    public static BackupFile ImportBackup(string json)
    {
        var root = ParseObject(json);
        var file = root is null ? new BackupFile() : Parse(Migrations.BackupV1ToV2(root).ToJsonString(), InlayJsonContext.Default.BackupFile);
        if (!string.Equals(file.Kind, BackupFile.BackupKind, StringComparison.Ordinal))
            throw new FormatException("The file isn't an Inlay backup.");
        if (file.SchemaVersion > BackupFile.CurrentVersion)
            throw new FormatException("The backup was made by a newer version of Inlay.");
        file.Settings = (file.Settings ?? new AppSettings()).Normalize();
        file.Snippets = [.. (file.Snippets ?? []).Where(s => !string.IsNullOrWhiteSpace(s?.Name)).Select(s => new SnippetDefinition { Name = s.Name.Trim(), Text = s.Text ?? "" }).DistinctBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
        file.Shorthands = [.. (file.Shorthands ?? []).Where(r => !string.IsNullOrWhiteSpace(r?.Shorthand))
            .Select(r => new ShorthandDefinition { Shorthand = r.Shorthand.Trim(), Expansion = r.Expansion ?? "", MatchCase = r.MatchCase, Enabled = r.Enabled }).DistinctBy(r => r.Shorthand, StringComparer.OrdinalIgnoreCase)];
        file.Images = Cleanup.Images(file.Images);
        return file;
    }

    /// <summary>The document as an object, or null when it isn't JSON at all (reported as "not a backup").</summary>
    private static System.Text.Json.Nodes.JsonObject? ParseObject(string json)
    {
        try
        {
            return System.Text.Json.Nodes.JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as System.Text.Json.Nodes.JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string ExportSnippets(IEnumerable<SnippetDefinition> snippets) =>
        JsonSerializer.Serialize(new SnippetsFile { Snippets = [.. snippets] }, InlayJsonContext.Default.SnippetsFile);

    public static string ExportShorthands(IEnumerable<ShorthandDefinition> entries) =>
        JsonSerializer.Serialize(new ShorthandsFile { Entries = [.. entries] }, InlayJsonContext.Default.ShorthandsFile);

    /// <summary>
    /// Merges imported snippets into <paramref name="existing"/>: an imported snippet replaces one with the same
    /// name (case-insensitive); the rest are added. Returns how many were added and replaced.
    /// </summary>
    /// <exception cref="FormatException">The file isn't a snippet export.</exception>
    public static (int Added, int Replaced) ImportSnippets(string json, List<SnippetDefinition> existing)
    {
        var file = Parse(json, InlayJsonContext.Default.SnippetsFile);
        int added = 0, replaced = 0;
        foreach (var s in file.Snippets ?? [])
        {
            // The same rules as the Add snippet dialog: an imported name must be one you could have typed there.
            if (s is null || EntryValidation.HasErrors(EntryValidation.ValidateSnippet(s.Name ?? "", s.Text ?? "", [], null)))
                continue;
            var name = s.Name!.Trim();
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
    public static (int Added, int Replaced) ImportShorthands(string json, List<ShorthandDefinition> existing)
    {
        var root = ParseObject(json) ?? throw new FormatException("The file isn't an Inlay export.");
        var file = Parse(Migrations.ShorthandsExport(root).ToJsonString(), InlayJsonContext.Default.ShorthandsFile);
        int added = 0, replaced = 0;
        foreach (var r in file.Entries ?? [])
        {
            if (r is null || EntryValidation.HasErrors(EntryValidation.ValidateShorthand(r.Shorthand ?? "", r.Expansion ?? "", [])))
                continue;
            var abbreviation = r.Shorthand!.Trim();
            var item = new ShorthandDefinition { Shorthand = abbreviation, Expansion = r.Expansion ?? "", MatchCase = r.MatchCase, Enabled = r.Enabled };
            var index = existing.FindIndex(e => string.Equals(e.Shorthand, abbreviation, StringComparison.OrdinalIgnoreCase));
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
            throw new FormatException("The file isn't an Inlay export.", ex);
        }
    }
}
