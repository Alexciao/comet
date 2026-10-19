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
    /// <summary>The file exists but couldn't be read (locked by another program): nothing was loaded.</summary>
    Unreadable,
    /// <summary>Written by a newer Comet: read as far as this version understands; a copy of the original was kept.</summary>
    FromNewerVersion,
}

public readonly record struct LoadResult<T>(T Value, LoadStatus Status, string? CorruptCopyPath)
{
    /// <summary>True when <see cref="Value"/> reflects the file (or its backup), not a fallback.</summary>
    public bool HasContent => Status is LoadStatus.Loaded or LoadStatus.Migrated or LoadStatus.RecoveredFromBackup or LoadStatus.FromNewerVersion;
}

/// <summary>
/// A versioned JSON file. Writes are atomic (temp file, flush, replace) and keep the previous version as
/// <c>.bak</c>; reads run migrations on older files and never throw, whatever is on disk.
/// </summary>
/// <typeparam name="T">The file model; must expose an int <c>SchemaVersion</c> property serialized as <c>schemaVersion</c>.</typeparam>
public sealed class JsonStore<T> where T : class, new()
{
    private const string VersionProperty = "schemaVersion";
    private const int ReadAttempts = 5;
    private static readonly TimeSpan s_retryDelay = TimeSpan.FromMilliseconds(50);

    private readonly JsonTypeInfo<T> _typeInfo;
    private readonly int _currentVersion;
    private readonly IReadOnlyList<Func<JsonObject, JsonObject>> _migrations;
    private readonly Lock _gate = new();
    private volatile string? _lastContentHash;

    /// <summary>The file on disk is known to be corrupt: the next write must not make it the backup.</summary>
    private bool _diskCorrupt;

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

    /// <summary>Content hash of the last read or successful write, so the file watcher can ignore our own saves.</summary>
    public string? LastContentHash => _lastContentHash;

    /// <summary>
    /// Reads the file. At startup (<paramref name="repair"/> true) a corrupt file is replaced by its backup, or by
    /// defaults; when reloading a hand edit (<paramref name="repair"/> false) nothing on disk is touched and a
    /// corrupt file comes back as <see cref="LoadStatus.Corrupt"/> for the caller to ignore. Thread-safe; never
    /// observes a write half-way.
    /// </summary>
    public LoadResult<T> Load(bool repair = true)
    {
        lock (_gate)
            return LoadCore(repair);
    }

    private LoadResult<T> LoadCore(bool repair)
    {
        if (!File.Exists(Path))
            return new LoadResult<T>(new T(), LoadStatus.Missing, null);

        if (ReadWithRetries(Path) is not { } json)
            return new LoadResult<T>(new T(), LoadStatus.Unreadable, null); // locked: don't mistake it for empty

        if (TryParse(json, out var value, out var migrated, out var version))
        {
            _lastContentHash = Hash(json);
            _diskCorrupt = false;
            if (version > _currentVersion)
            {
                // A downgrade: keep the newer file as it was, so its newer fields aren't lost when this version saves.
                var copy = KeepCopy($"from-v{version.ToString(CultureInfo.InvariantCulture)}");
                return new LoadResult<T>(value, LoadStatus.FromNewerVersion, copy);
            }

            if (migrated && repair)
                TrySave(value);
            return new LoadResult<T>(value, migrated ? LoadStatus.Migrated : LoadStatus.Loaded, null);
        }

        // A reload leaves the file where it is (an editor may still be saving it): no copy, or every autosave makes one.
        var corruptCopy = repair ? KeepCopy("corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)) : null;
        if (!repair)
        {
            _diskCorrupt = true;
            return new LoadResult<T>(new T(), LoadStatus.Corrupt, corruptCopy);
        }

        if (ReadWithRetries(BackupPath) is { } backupJson && TryParse(backupJson, out var backup, out _, out _))
        {
            // Put the backup back in place by copying it, so the good .bak stays a good .bak.
            try
            {
                File.Copy(BackupPath, Path, overwrite: true);
                _lastContentHash = Hash(backupJson);
                _diskCorrupt = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _diskCorrupt = true;
            }

            return new LoadResult<T>(backup, LoadStatus.RecoveredFromBackup, corruptCopy);
        }

        _diskCorrupt = true;
        var defaults = new T();
        TrySave(defaults);
        return new LoadResult<T>(defaults, LoadStatus.Corrupt, corruptCopy);
    }

    /// <summary>The file's text, retrying briefly while another program holds it; null when it stays unreadable.</summary>
    private static string? ReadWithRetries(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= ReadAttempts)
                    return null;
                Thread.Sleep(s_retryDelay);
            }
        }
    }

    private bool TryParse(string json, out T value, out bool migrated, out int version)
    {
        value = new T();
        migrated = false;
        version = 0;
        try
        {
            if (JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is not JsonObject root)
                return false;

            version = root.TryGetPropertyValue(VersionProperty, out var v) && v is JsonValue jv && jv.TryGetValue<int>(out var n) ? n : 0;
            var current = version;
            while (current < _currentVersion)
            {
                if (current >= _migrations.Count)
                    break; // no step registered: the shape is compatible, just stamp the version
                root = _migrations[current](root);
                current++;
                migrated = true;
            }

            if (current < _currentVersion)
                migrated = true;
            root[VersionProperty] = _currentVersion;
            value = root.Deserialize(_typeInfo) ?? new T();
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Keeps a copy of the file next to it (<c>settings.corrupt-….json</c>); null when that fails.</summary>
    private string? KeepCopy(string tag)
    {
        try
        {
            var copy = System.IO.Path.ChangeExtension(Path, $".{tag}.json");
            File.Copy(Path, copy, overwrite: true);
            return copy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void TrySave(T value)
    {
        try
        {
            WriteCore(Serialize(value));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Startup goes on with what was read; the next save tries again.
        }
    }

    /// <summary>Serializes and writes atomically (see <see cref="Write"/>).</summary>
    public void Save(T value) => Write(Serialize(value));

    /// <summary>The file content for <paramref name="value"/>. Cheap; lets callers snapshot on one thread and write on another.</summary>
    public string Serialize(T value) => JsonSerializer.Serialize(value, _typeInfo);

    /// <summary>
    /// Writes atomically: a crash mid-save leaves either the old or the new file, never half of one. Thread-safe;
    /// concurrent writes and reads are serialized. Throws on I/O errors, leaving the file and the hash as they were.
    /// </summary>
    public void Write(string json)
    {
        lock (_gate)
            WriteCore(json);
    }

    private void WriteCore(string json)
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = Path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            fs.Write(Encoding.UTF8.GetBytes(json));
            fs.Flush(flushToDisk: true);
        }

        if (File.Exists(Path) && !_diskCorrupt)
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
            // No file yet, or a corrupt one (a copy was kept): it must not become the backup.
            File.Move(tmp, Path, overwrite: true);
        }

        _lastContentHash = Hash(json);
        _diskCorrupt = false;
    }

    /// <summary>True when the file on disk differs from what we last read or wrote (a hand edit).</summary>
    public bool ChangedOnDisk()
    {
        lock (_gate)
        {
            if (!File.Exists(Path))
                return false;
            return ReadWithRetries(Path) is { } json && Hash(json) != _lastContentHash;
        }
    }

    private static string Hash(string s) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}
