using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Comet.Core.Settings;

public enum LoadStatus
{
    /// <summary>Read as-is.</summary>
    Loaded,
    /// <summary>No file yet: defaults.</summary>
    Missing,
    /// <summary>Read and upgraded from an older schema version (saved back in the new format).</summary>
    Migrated,
    /// <summary>The file was corrupt; a copy was kept and the backup was loaded instead.</summary>
    RecoveredFromBackup,
    /// <summary>The file and its backup were unreadable; a copy was kept and defaults were loaded.</summary>
    Corrupt,
}

public readonly record struct LoadResult<T>(T Value, LoadStatus Status, string? CorruptCopyPath);

/// <summary>
/// A versioned JSON file. Writes are atomic (temp file, flush, replace) and keep the previous version as
/// <c>.bak</c>; reads run migrations on older files and never throw on corrupt content.
/// </summary>
/// <typeparam name="T">The file model; must expose an int <c>SchemaVersion</c> property serialized as <c>schemaVersion</c>.</typeparam>
public sealed class JsonStore<T> where T : class, new()
{
    private const string VersionProperty = "schemaVersion";

    private readonly JsonTypeInfo<T> _typeInfo;
    private readonly int _currentVersion;
    private readonly IReadOnlyList<Func<JsonObject, JsonObject>> _migrations;

    /// <param name="migrations">Element <c>i</c> upgrades a version-<c>i</c> document to version <c>i + 1</c>
    /// (version 0 = a hand-written file without a version).</param>
    public JsonStore(string path, JsonTypeInfo<T> typeInfo, int currentVersion, IReadOnlyList<Func<JsonObject, JsonObject>>? migrations = null)
    {
        Path = path;
        _typeInfo = typeInfo;
        _currentVersion = currentVersion;
        _migrations = migrations ?? [];
    }

    public string Path { get; }

    public string BackupPath => Path + ".bak";

    private volatile string? _lastContentHash;
    private readonly Lock _writeGate = new();

    /// <summary>
    /// Content hash of the last read or write, so the file watcher can ignore our own saves. Set before a write
    /// starts, so a watcher event raised mid-write is never mistaken for a hand edit.
    /// </summary>
    public string? LastContentHash => _lastContentHash;

    public LoadResult<T> Load()
    {
        if (!File.Exists(Path))
            return new LoadResult<T>(new T(), LoadStatus.Missing, null);

        string json;
        try
        {
            json = File.ReadAllText(Path, Encoding.UTF8);
        }
        catch (IOException)
        {
            // Locked by an editor mid-save: treat as unchanged rather than corrupt.
            return new LoadResult<T>(new T(), LoadStatus.Missing, null);
        }

        if (TryParse(json, out var value, out var migrated))
        {
            _lastContentHash = Hash(json);
            if (migrated)
                Save(value);
            return new LoadResult<T>(value, migrated ? LoadStatus.Migrated : LoadStatus.Loaded, null);
        }

        var copy = KeepCorruptCopy();
        if (File.Exists(BackupPath) && TryParse(File.ReadAllText(BackupPath, Encoding.UTF8), out var backup, out _))
        {
            Save(backup);
            return new LoadResult<T>(backup, LoadStatus.RecoveredFromBackup, copy);
        }

        var defaults = new T();
        Save(defaults);
        return new LoadResult<T>(defaults, LoadStatus.Corrupt, copy);
    }

    private bool TryParse(string json, out T value, out bool migrated)
    {
        value = new T();
        migrated = false;
        try
        {
            if (JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is not JsonObject root)
                return false;

            var version = root.TryGetPropertyValue(VersionProperty, out var v) && v is JsonValue jv && jv.TryGetValue<int>(out var n) ? n : 0;
            while (version < _currentVersion)
            {
                if (version >= _migrations.Count)
                    break; // no step registered: the shape is compatible, just stamp the version
                root = _migrations[version](root);
                version++;
                migrated = true;
            }

            if (version < _currentVersion)
                migrated = true;
            root[VersionProperty] = Math.Max(version, _currentVersion);
            value = root.Deserialize(_typeInfo) ?? new T();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private string? KeepCorruptCopy()
    {
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var copy = System.IO.Path.ChangeExtension(Path, $".corrupt-{stamp}.json");
            File.Copy(Path, copy, overwrite: true);
            return copy;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Serializes and writes atomically (see <see cref="Write"/>).</summary>
    public void Save(T value) => Write(Serialize(value));

    /// <summary>The file content for <paramref name="value"/>. Cheap; lets callers snapshot on one thread and write on another.</summary>
    public string Serialize(T value) => JsonSerializer.Serialize(value, _typeInfo);

    /// <summary>
    /// Writes atomically: a crash mid-save leaves either the old or the new file, never half of one. Thread-safe;
    /// concurrent writes are serialized.
    /// </summary>
    public void Write(string json)
    {
        lock (_writeGate)
            WriteCore(json);
    }

    private void WriteCore(string json)
    {
        _lastContentHash = Hash(json);
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = Path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            fs.Write(bytes);
            fs.Flush(flushToDisk: true);
        }

        if (File.Exists(Path))
        {
            try
            {
                File.Replace(tmp, Path, BackupPath, ignoreMetadataErrors: true);
            }
            catch (IOException)
            {
                // File.Replace is unsupported on some volumes: fall back to copy-backup + move.
                File.Copy(Path, BackupPath, overwrite: true);
                File.Move(tmp, Path, overwrite: true);
            }
        }
        else
        {
            File.Move(tmp, Path);
        }
    }

    /// <summary>True when the file on disk differs from what we last read or wrote (a hand edit).</summary>
    public bool ChangedOnDisk()
    {
        try
        {
            return File.Exists(Path) && Hash(File.ReadAllText(Path, Encoding.UTF8)) != LastContentHash;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string Hash(string s) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}
