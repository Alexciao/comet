using System.Text.Json.Nodes;

namespace Inlay.Core.Settings;

/// <summary>
/// Upgrades Comet 1.0 files to Inlay 1.1. Every step works on the raw JSON, so a file reads the same way whether
/// it comes from the data folder, a backup or an export. Steps leave documents that are already in the new shape
/// alone, and match property names case-insensitively (as the deserializer does), so hand edits survive.
/// </summary>
public static class Migrations
{
    /// <summary>The 1.0 backup kind; still accepted by import.</summary>
    public const string CometBackupKind = "comet-backup";

    /// <summary>The 1.0 shorthands file, renamed to <c>shorthands.json</c> on first run.</summary>
    public const string CometShorthandsFile = "autoreplace.json";

    public const string ShorthandsFileName = "shorthands.json";

    /// <summary><c>settings.json</c> steps for <see cref="JsonStore{T}"/>.</summary>
    public static IReadOnlyList<Func<JsonObject, JsonObject>> Settings { get; } = [Same, SettingsV1ToV2];

    /// <summary><c>shorthands.json</c> steps for <see cref="JsonStore{T}"/>.</summary>
    public static IReadOnlyList<Func<JsonObject, JsonObject>> Shorthands { get; } = [Same, ShorthandsV1ToV2];

    private static JsonObject Same(JsonObject root) => root;

    /// <summary>
    /// 1.0 → 1.1: <c>autoReplace</c> becomes <c>shorthands</c>, and the Quick Insert exclusion list becomes per-app
    /// modes. Excluded apps become <see cref="QuickInsertMode.Off"/>, except the chat apps that have their own emoji
    /// picker, which become <see cref="QuickInsertMode.HideEmoji"/> (in 1.0 they were excluded by default; now their
    /// snippets and images show). WhatsApp, new in that list, is added as Hide emoji too.
    /// </summary>
    public static JsonObject SettingsV1ToV2(JsonObject root)
    {
        if (Find(root, "features") is JsonObject features)
            Rename(features, "autoReplace", "shorthands");
        Rename(root, "autoReplaceExcludedApps", "shorthandsExcludedApps");

        if (Take(root, "pickerExcludedApps") is JsonArray excluded && Find(root, "quickInsertApps") is null)
        {
            var apps = new JsonArray();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in excluded)
            {
                if (item is JsonValue v && v.TryGetValue<string>(out var app) && !string.IsNullOrWhiteSpace(app) && seen.Add(app.Trim()))
                    apps.Add((JsonNode)Mode(app.Trim(), IsChatApp(app.Trim()) ? QuickInsertMode.HideEmoji : QuickInsertMode.Off));
            }

            foreach (var app in AppSettings.ChatApps.Where(a => a.StartsWith("whatsapp", StringComparison.Ordinal)))
            {
                if (seen.Add(app))
                    apps.Add((JsonNode)Mode(app, QuickInsertMode.HideEmoji));
            }

            root["quickInsertApps"] = apps;
        }

        return root;
    }

    private static JsonObject Mode(string app, QuickInsertMode mode) => new() { ["app"] = app, ["mode"] = mode.ToString() };

    private static bool IsChatApp(string app) => AppSettings.ChatApps.Contains(app, StringComparer.OrdinalIgnoreCase);

    /// <summary>1.0 → 1.1: entries' <c>abbreviation</c>/<c>replacement</c> become <c>shorthand</c>/<c>expansion</c>.</summary>
    public static JsonObject ShorthandsV1ToV2(JsonObject root)
    {
        if (Find(root, "entries") is JsonArray entries)
            RenameEntryFields(entries);
        return root;
    }

    private static void RenameEntryFields(JsonArray entries)
    {
        foreach (var entry in entries)
        {
            if (entry is not JsonObject e)
                continue;
            Rename(e, "abbreviation", "shorthand");
            Rename(e, "replacement", "expansion");
        }
    }

    /// <summary>A 1.0 backup (<c>comet-backup</c>, version 1) in the 1.1 shape; a 1.1 backup is returned as is.</summary>
    public static JsonObject BackupV1ToV2(JsonObject root)
    {
        if (Find(root, "kind") is JsonValue kind && kind.TryGetValue<string>(out var k) && k == CometBackupKind)
            root[Key(root, "kind")] = BackupFile.BackupKind;
        if (Version(root) >= 2)
            return root;
        if (Find(root, "settings") is JsonObject settings)
            SettingsV1ToV2(settings);
        if (Take(root, "autoReplace") is JsonArray entries)
        {
            RenameEntryFields(entries);
            root["shorthands"] = entries;
        }

        root[Key(root, "schemaVersion")] = BackupFile.CurrentVersion;
        return root;
    }

    /// <summary>A shorthands export from 1.0 (<c>abbreviation</c>/<c>replacement</c>) in the 1.1 shape.</summary>
    public static JsonObject ShorthandsExport(JsonObject root) => Version(root) >= 2 ? root : ShorthandsV1ToV2(root);

    /// <summary>
    /// First run after Comet 1.0: <c>autoreplace.json</c> becomes <c>shorthands.json</c> (its fields are upgraded
    /// when it is loaded). The 1.0 file is kept as <c>autoreplace.1.0.json</c>. Runs once: nothing happens when
    /// <c>shorthands.json</c> exists. Returns true when a file was moved.
    /// </summary>
    public static bool RenameCometFiles(string dataDirectory)
    {
        var old = Path.Combine(dataDirectory, CometShorthandsFile);
        var current = Path.Combine(dataDirectory, ShorthandsFileName);
        if (!File.Exists(old) || File.Exists(current))
            return false;
        File.Copy(old, Path.Combine(dataDirectory, "autoreplace.1.0.json"), overwrite: true);
        File.Move(old, current);
        return true;
    }

    private static int Version(JsonObject root) =>
        Find(root, "schemaVersion") is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;

    /// <summary>The property named <paramref name="name"/>, ignoring case.</summary>
    private static JsonNode? Find(JsonObject o, string name) =>
        o.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>The actual key for <paramref name="name"/> (its case in the file), or the name itself.</summary>
    private static string Key(JsonObject o, string name) =>
        o.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Key ?? name;

    private static JsonNode? Take(JsonObject o, string name)
    {
        var key = o.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Key;
        if (key is null)
            return null;
        o.Remove(key, out var value);
        return value;
    }

    private static void Rename(JsonObject o, string from, string to)
    {
        if (Find(o, to) is not null)
            return;
        if (Take(o, from) is { } value)
            o[to] = value;
    }
}
