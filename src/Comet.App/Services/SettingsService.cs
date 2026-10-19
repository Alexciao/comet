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
/// Owns <c>settings.json</c>, <c>snippets.json</c>, <c>autoreplace.json</c>, <c>frecency.json</c> and
/// <c>ui-state.json</c>. Lives on the UI thread. Changes apply at once (<see cref="Changed"/>); the files are
/// written about 300 ms later, atomically, on a background thread, so Settings never waits for the disk.
/// Hand edits are picked up through a file watcher (event-driven, no polling) and read in the background too.
/// </summary>
internal sealed class SettingsService : IDisposable
{
    private static readonly TimeSpan s_reloadDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan s_saveDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan s_frecencySaveDelay = TimeSpan.FromSeconds(5);

    private readonly DispatcherQueue _dispatcher;
    private readonly JsonStore<AppSettings> _settingsStore;
    private readonly JsonStore<SnippetsFile> _snippetsStore;
    private readonly JsonStore<AutoReplaceFile> _autoReplaceStore;
    private readonly JsonStore<FrecencyFile> _frecencyStore;
    private readonly JsonStore<UiState> _uiStateStore;
    private readonly FileSystemWatcher? _watcher;
    private readonly DispatcherQueueTimer _reloadTimer;
    private readonly DispatcherQueueTimer _saveTimer;
    private readonly DispatcherQueueTimer _frecencyTimer;
    private readonly Lock _frecencyGate = new();
    private readonly Lock _writerGate = new();
    private SettingsParts _dirty;
    private bool _uiStateDirty;
    private bool _reloading;
    private Task _writer = Task.CompletedTask;

    public SettingsService(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        var dir = AppPaths.DataDirectory;
        Directory.CreateDirectory(dir);
        _settingsStore = new(Path.Combine(dir, "settings.json"), CometJsonContext.Default.AppSettings, AppSettings.CurrentVersion);
        _snippetsStore = new(Path.Combine(dir, "snippets.json"), CometJsonContext.Default.SnippetsFile, SnippetsFile.CurrentVersion);
        _autoReplaceStore = new(Path.Combine(dir, "autoreplace.json"), CometJsonContext.Default.AutoReplaceFile, AutoReplaceFile.CurrentVersion);
        _frecencyStore = new(Path.Combine(dir, "frecency.json"), CometJsonContext.Default.FrecencyFile, FrecencyFile.CurrentVersion);
        _uiStateStore = new(Path.Combine(dir, "ui-state.json"), CometJsonContext.Default.UiState, UiState.CurrentVersion);

        _reloadTimer = dispatcher.CreateTimer();
        _reloadTimer.Interval = s_reloadDelay;
        _reloadTimer.IsRepeating = false;
        _reloadTimer.Tick += (_, _) => ReloadChangedFiles();

        _saveTimer = dispatcher.CreateTimer();
        _saveTimer.Interval = s_saveDelay;
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => WriteDirty();

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

    /// <summary>Window placement and known app paths; change it, then call <see cref="SaveUiState"/>.</summary>
    public UiState UiState { get; private set; } = new();

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
        UiState = _uiStateStore.Load().Value;
        UiState.AppPaths = new Dictionary<string, string>(UiState.AppPaths ?? [], StringComparer.OrdinalIgnoreCase);
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
        MarkDirty(SettingsParts.Settings);
        Changed?.Invoke(SettingsParts.Settings);
    }

    public void SaveSnippets()
    {
        MarkDirty(SettingsParts.Snippets);
        Changed?.Invoke(SettingsParts.Snippets);
    }

    public void SaveAutoReplace()
    {
        MarkDirty(SettingsParts.AutoReplace);
        Changed?.Invoke(SettingsParts.AutoReplace);
    }

    /// <summary>Replaces everything with a backup's contents (Settings › General › Import settings).</summary>
    public void Restore(BackupFile backup)
    {
        Settings = (backup.Settings ?? new AppSettings()).Normalize();
        Snippets = backup.Snippets ?? [];
        AutoReplace = backup.AutoReplace ?? [];
        MarkDirty(SettingsParts.All);
        Changed?.Invoke(SettingsParts.All);
    }

    public void SaveUiState()
    {
        _uiStateDirty = true;
        RestartSaveTimer();
    }

    private void MarkDirty(SettingsParts parts)
    {
        _dirty |= parts;
        RestartSaveTimer();
    }

    private void RestartSaveTimer()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Snapshots the changed files on the UI thread (cheap) and hands them to the background writer.</summary>
    private void WriteDirty()
    {
        if (_dirty.HasFlag(SettingsParts.Settings))
            Enqueue(_settingsStore.Write, _settingsStore.Serialize(Settings));
        if (_dirty.HasFlag(SettingsParts.Snippets))
            Enqueue(_snippetsStore.Write, _snippetsStore.Serialize(new SnippetsFile { Snippets = Snippets }));
        if (_dirty.HasFlag(SettingsParts.AutoReplace))
            Enqueue(_autoReplaceStore.Write, _autoReplaceStore.Serialize(new AutoReplaceFile { Entries = AutoReplace }));
        if (_uiStateDirty)
            Enqueue(_uiStateStore.Write, _uiStateStore.Serialize(UiState));
        _dirty = SettingsParts.None;
        _uiStateDirty = false;
    }

    /// <summary>Runs file writes one after another, in order, off the UI thread.</summary>
    private void Enqueue(Action<string> write, string json)
    {
        lock (_writerGate)
            _writer = _writer.ContinueWith(_ => Save(() => write(json)), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
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

    /// <summary>Writes anything pending and waits for the writer (Comet is quitting).</summary>
    public void Flush()
    {
        _saveTimer.Stop();
        WriteDirty();
        Task writer;
        lock (_writerGate)
            writer = _writer;
        if (!writer.Wait(TimeSpan.FromSeconds(5)))
            Log.Warn("settings writer did not finish within 5 s");
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

    /// <summary>True when there is anything to forget (Settings › Emoji › Clear recently used).</summary>
    public bool HasFrecency
    {
        get
        {
            lock (_frecencyGate)
                return Frecency.All(DateTimeOffset.Now).Any();
        }
    }

    public void SaveFrecency()
    {
        FrecencyFile file;
        lock (_frecencyGate)
            file = Frecency.ToFile(DateTimeOffset.Now);
        Enqueue(_frecencyStore.Write, _frecencyStore.Serialize(file));
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

    /// <summary>Reads the files that changed on disk in the background, then applies them on the UI thread.</summary>
    private void ReloadChangedFiles()
    {
        if (_reloading)
        {
            _reloadTimer.Start(); // one reload at a time; look again once this one is done
            return;
        }

        _reloading = true;
        _ = Task.Run(() =>
        {
            LoadResult<AppSettings>? settings = _settingsStore.ChangedOnDisk() ? _settingsStore.Load() : null;
            LoadResult<SnippetsFile>? snippets = _snippetsStore.ChangedOnDisk() ? _snippetsStore.Load() : null;
            LoadResult<AutoReplaceFile>? autoReplace = _autoReplaceStore.ChangedOnDisk() ? _autoReplaceStore.Load() : null;
            _dispatcher.TryEnqueue(() => ApplyReload(settings, snippets, autoReplace));
        });
    }

    private void ApplyReload(LoadResult<AppSettings>? settings, LoadResult<SnippetsFile>? snippets, LoadResult<AutoReplaceFile>? autoReplace)
    {
        _reloading = false;
        var parts = SettingsParts.None;
        if (settings is { } s)
        {
            Settings = Track(s, "settings.json").Normalize();
            parts |= SettingsParts.Settings;
        }

        if (snippets is { } sn)
        {
            Snippets = Track(sn, "snippets.json").Snippets ?? [];
            parts |= SettingsParts.Snippets;
        }

        if (autoReplace is { } ar)
        {
            AutoReplace = Track(ar, "autoreplace.json").Entries ?? [];
            parts |= SettingsParts.AutoReplace;
        }

        // A hand edit wins over a change of ours that hasn't been written yet.
        _dirty &= ~parts;
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

        Flush();
    }
}
