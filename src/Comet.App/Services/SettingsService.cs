using Comet.Core.Settings;
using Comet.Platform;
using Microsoft.UI.Dispatching;

namespace Comet.App.Services;

/// <summary>Which file changed (for targeted reloads).</summary>
[Flags]
internal enum SettingsParts
{
    None = 0,
    Settings = 1,
    Snippets = 2,
    AutoReplace = 4,
    All = Settings | Snippets | AutoReplace,
}

/// <summary>
/// Owns <c>settings.json</c>, <c>snippets.json</c>, <c>autoreplace.json</c> and <c>frecency.json</c>. Lives on the UI
/// thread. Saves are atomic; hand edits are picked up through a file watcher (event-driven, no polling).
/// </summary>
internal sealed class SettingsService : IDisposable
{
    private static readonly TimeSpan s_reloadDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan s_frecencySaveDelay = TimeSpan.FromSeconds(5);

    private readonly DispatcherQueue _dispatcher;
    private readonly JsonStore<AppSettings> _settingsStore;
    private readonly JsonStore<SnippetsFile> _snippetsStore;
    private readonly JsonStore<AutoReplaceFile> _autoReplaceStore;
    private readonly JsonStore<FrecencyFile> _frecencyStore;
    private readonly FileSystemWatcher? _watcher;
    private readonly DispatcherQueueTimer _reloadTimer;
    private readonly DispatcherQueueTimer _frecencyTimer;
    private readonly Lock _frecencyGate = new();

    public SettingsService(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        var dir = AppPaths.DataDirectory;
        Directory.CreateDirectory(dir);
        _settingsStore = new(Path.Combine(dir, "settings.json"), CometJsonContext.Default.AppSettings, AppSettings.CurrentVersion);
        _snippetsStore = new(Path.Combine(dir, "snippets.json"), CometJsonContext.Default.SnippetsFile, SnippetsFile.CurrentVersion);
        _autoReplaceStore = new(Path.Combine(dir, "autoreplace.json"), CometJsonContext.Default.AutoReplaceFile, AutoReplaceFile.CurrentVersion);
        _frecencyStore = new(Path.Combine(dir, "frecency.json"), CometJsonContext.Default.FrecencyFile, FrecencyFile.CurrentVersion);

        _reloadTimer = dispatcher.CreateTimer();
        _reloadTimer.Interval = s_reloadDelay;
        _reloadTimer.IsRepeating = false;
        _reloadTimer.Tick += (_, _) => ReloadChangedFiles();

        _frecencyTimer = dispatcher.CreateTimer();
        _frecencyTimer.Interval = s_frecencySaveDelay;
        _frecencyTimer.IsRepeating = false;
        _frecencyTimer.Tick += (_, _) => SaveFrecency();

        LoadAll();

        try
        {
            _watcher = new FileSystemWatcher(dir, "*.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Renamed += OnFileEvent;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Log.Error("settings watcher", ex);
        }
    }

    public AppSettings Settings { get; private set; } = new();

    public List<SnippetDefinition> Snippets { get; private set; } = [];

    public List<AutoReplaceDefinition> AutoReplace { get; private set; } = [];

    public Frecency Frecency { get; private set; } = new();

    /// <summary>Files that were corrupt at load, with the copy that was kept (shown as an InfoBar in Settings).</summary>
    public List<(string File, LoadStatus Status, string? CopyPath)> Problems { get; } = [];

    /// <summary>Raised on the UI thread after any change (ours or a hand edit).</summary>
    public event Action<SettingsParts>? Changed;

    public string DataDirectory => AppPaths.DataDirectory;

    private void LoadAll()
    {
        Settings = Track(_settingsStore.Load(), "settings.json").Normalize();
        Snippets = Track(_snippetsStore.Load(), "snippets.json").Snippets ?? [];
        AutoReplace = Track(_autoReplaceStore.Load(), "autoreplace.json").Entries ?? [];
        Frecency = new Frecency(_frecencyStore.Load().Value);
    }

    private T Track<T>(LoadResult<T> result, string file)
    {
        if (result.Status is LoadStatus.Corrupt or LoadStatus.RecoveredFromBackup)
        {
            Problems.Add((file, result.Status, result.CorruptCopyPath));
            Log.Warn($"{file} was unreadable ({result.Status}); copy kept at {result.CorruptCopyPath}");
        }

        return result.Value;
    }

    // ---------------------------------------------------------------- saving

    public void SaveSettings()
    {
        Settings.Normalize();
        Save(() => _settingsStore.Save(Settings));
        Changed?.Invoke(SettingsParts.Settings);
    }

    public void SaveSnippets()
    {
        Save(() => _snippetsStore.Save(new SnippetsFile { Snippets = Snippets }));
        Changed?.Invoke(SettingsParts.Snippets);
    }

    public void SaveAutoReplace()
    {
        Save(() => _autoReplaceStore.Save(new AutoReplaceFile { Entries = AutoReplace }));
        Changed?.Invoke(SettingsParts.AutoReplace);
    }

    private static void Save(Action save)
    {
        try
        {
            save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("save", ex);
        }
    }

    // ---------------------------------------------------------------- frecency

    /// <summary>Records a pick (any thread) and schedules a save a few seconds later.</summary>
    public void RecordPick(string key)
    {
        lock (_frecencyGate)
            Frecency.Record(key, DateTimeOffset.Now);
        _dispatcher.TryEnqueue(() =>
        {
            _frecencyTimer.Stop();
            _frecencyTimer.Start();
        });
    }

    public void ResetFrecency()
    {
        lock (_frecencyGate)
            Frecency.Clear();
        SaveFrecency();
    }

    public void SaveFrecency()
    {
        FrecencyFile file;
        lock (_frecencyGate)
            file = Frecency.ToFile(DateTimeOffset.Now);
        Save(() => _frecencyStore.Save(file));
    }

    /// <summary>Snapshot of frecency scores (thread-safe).</summary>
    public List<(string Key, double Score)> FrecencyScores()
    {
        var now = DateTimeOffset.Now;
        lock (_frecencyGate)
            return [.. Frecency.All(now)];
    }

    // ---------------------------------------------------------------- hand edits

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        // Watcher events arrive on a thread-pool thread, often several per save: coalesce on the UI thread.
        _dispatcher.TryEnqueue(() =>
        {
            _reloadTimer.Stop();
            _reloadTimer.Start();
        });
    }

    private void ReloadChangedFiles()
    {
        var parts = SettingsParts.None;
        if (_settingsStore.ChangedOnDisk())
        {
            Settings = Track(_settingsStore.Load(), "settings.json").Normalize();
            parts |= SettingsParts.Settings;
        }

        if (_snippetsStore.ChangedOnDisk())
        {
            Snippets = Track(_snippetsStore.Load(), "snippets.json").Snippets ?? [];
            parts |= SettingsParts.Snippets;
        }

        if (_autoReplaceStore.ChangedOnDisk())
        {
            AutoReplace = Track(_autoReplaceStore.Load(), "autoreplace.json").Entries ?? [];
            parts |= SettingsParts.AutoReplace;
        }

        if (parts != SettingsParts.None)
        {
            Log.Info($"reloaded hand-edited settings: {parts}");
            Changed?.Invoke(parts);
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _reloadTimer.Stop();
        if (_frecencyTimer.IsRunning)
        {
            _frecencyTimer.Stop();
            SaveFrecency();
        }
    }
}
