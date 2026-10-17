using Comet.App.Services;
using Comet.App.UI;
using Comet.Core.Emoji;
using Comet.Core.Search;
using Comet.Platform;
using Comet.Platform.Engine;
using Comet.Platform.Native;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Comet.App;

/// <summary>
/// Owns the running app: settings, the input engine (hook thread), the popup, the tray icon and the Settings
/// window. Runs on the UI thread; turns settings changes into engine snapshots and system events into resets.
/// </summary>
internal sealed class AppHost : IDisposable
{
    private readonly Application _app;
    private readonly LaunchOptions _options;
    private readonly DispatcherQueue _dispatcher;
    private readonly EngineDataFactory _dataFactory = new();
    private SettingsService _settings = null!;
    private InputEngine _engine = null!;
    private PopupWindow _popup = null!;
    private MessageWindow _messages = null!;
    private TrayIcon _tray = null!;
    private SettingsWindow? _settingsWindow;
    private bool _paused;
    private int _rebuildVersion;
    private bool _shutDown;
    private SearchEngine? _shortcodeChecker;
    private EmojiUpdateService _updates = null!;

    public AppHost(Application app, LaunchOptions options)
    {
        _app = app;
        _options = options;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
    }

    internal SettingsService Settings => _settings;

    internal InputEngine Engine => _engine;

    internal EngineDataFactory DataFactory => _dataFactory;

    internal bool Paused => _paused;

    /// <summary>Raised on the UI thread when Comet is paused or resumed (tray or Settings).</summary>
    internal event Action<bool>? PausedChanged;

    public void Start()
    {
        _settings = new SettingsService(_dispatcher);
        _updates = new EmojiUpdateService(_dispatcher, _dataFactory, () => _settings.Settings, () => _paused, OnEmojiDataReplaced);

        _popup = new PopupWindow();
        _messages = new MessageWindow(Program.MessageWindowClass, OnMessage);
        _popup.Attach(_messages);
        _popup.WarmUp();

        _engine = new InputEngine(_popup, _options.AcceptInjectedInput);
        _engine.Picked += _settings.RecordPick;
        _engine.Start(Config(), BuildData());
        _settings.Changed += OnSettingsChanged;
        PrefetchEmoji();

        _tray = new TrayIcon(_messages.Handle);
        _tray.OpenSettings += OpenSettings;
        _tray.PauseChanged += SetPaused;
        _tray.Quit += Shutdown;
        _tray.Show();

        WtsApi32.WTSRegisterSessionNotification(_messages.Handle, WtsApi32.NOTIFY_FOR_THIS_SESSION);
        StartupRegistration.Apply(_settings.Settings.StartAtLogin);

        _updates.Schedule();
        Log.Info($"started: hook {(_engine.HookInstalled ? "installed" : "not needed")}, emoji {(_dataFactory.CurrentEmoji is null ? "not loaded" : "loaded")}");
        if (!_options.Background)
            OpenSettings();
    }

    private EngineConfig Config() => EngineConfig.From(_settings.Settings, _paused);

    private EngineData BuildData() =>
        _dataFactory.Build(_settings.Settings, [.. _settings.Snippets], [.. _settings.AutoReplace], _settings.FrecencyScores());

    private void OnSettingsChanged(SettingsParts parts)
    {
        _engine.SetConfig(Config());
        if (parts.HasFlag(SettingsParts.Settings))
            StartupRegistration.Apply(_settings.Settings.StartAtLogin);
        RebuildData(prefetch: parts.HasFlag(SettingsParts.Settings)); // emoji may have been turned on, or the skin tone changed
        if (parts.HasFlag(SettingsParts.Settings))
            _updates.Schedule();
    }

    /// <summary>A new emoji pack or font check result: rebuild the search data in the background, at low priority.</summary>
    private void OnEmojiDataReplaced()
    {
        _shortcodeChecker = null;
        RebuildData(prefetch: true, lowPriority: true);
    }

    /// <summary>
    /// Warms the popup's emoji bitmaps at idle priority: the user's frecent emoji first, then the most popular
    /// ones, so the emoji people actually see are ready before their first popup.
    /// </summary>
    private void PrefetchEmoji()
    {
        if (!_settings.Settings.Features.Emoji)
            return;
        if (_dataFactory.CurrentEmoji is not { } data)
            return;
        var tone = _settings.Settings.SkinTone;
        var frecent = _settings.FrecencyScores()
            .OrderByDescending(f => f.Score)
            .Select(f => _dataFactory.EmojiIndexOf(f.Key))
            .Where(i => i >= 0);
        var popular = Enumerable.Range(0, data.Count)
            .Where(i => data.Popularity(i) <= PrefetchPopularRank && data.RenderedOnReferenceFont(i))
            .OrderBy(data.Popularity);
        _popup.Prefetch(frecent.Concat(popular).Distinct().Select(i => data.Emoji(i, tone).ToString()));
    }

    private const int PrefetchPopularRank = 500;

    /// <summary>Rebuilds search data off the UI thread; only the newest build is handed to the engine.</summary>
    internal void RebuildData(bool prefetch = false, bool lowPriority = false)
    {
        var version = ++_rebuildVersion;
        var settings = _settings.Settings;
        var snippets = _settings.Snippets.ToList();
        var autoReplace = _settings.AutoReplace.ToList();
        var frecency = _settings.FrecencyScores();
        var emojiWasLoaded = _dataFactory.CurrentEmoji is not null;
        _ = Task.Run(() =>
        {
            var thread = Thread.CurrentThread;
            var priority = thread.Priority;
            if (lowPriority)
                thread.Priority = ThreadPriority.Lowest;
            EngineData data;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                data = _dataFactory.Build(settings, snippets, autoReplace, frecency);
            }
            finally
            {
                thread.Priority = priority;
            }

            if (lowPriority)
                Log.Info($"search data rebuilt in the background in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms");

            if (version != Volatile.Read(ref _rebuildVersion))
                return;
            _engine.SetData(data);
            if (prefetch && data.Emoji is not null)
                _dispatcher.TryEnqueue(PrefetchEmoji);
            if (emojiWasLoaded && data.Emoji is null)
            {
                // Emoji was just turned off: let the dataset and its index go now rather than whenever.
                GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
            }
        });
    }

    internal void SetPaused(bool paused)
    {
        if (paused == _paused)
            return;
        _paused = paused;
        _tray.Paused = paused;
        _engine.SetConfig(Config());
        _updates.Schedule();
        Log.Info(paused ? "paused" : "resumed");
        PausedChanged?.Invoke(paused);
    }

    internal void TogglePause() => SetPaused(!_paused);

    /// <summary>True when <paramref name="name"/> is also an emoji shortcode (warned about in the snippet dialog).</summary>
    internal bool IsEmojiShortcode(string name)
    {
        _shortcodeChecker ??= new SearchEngine(_dataFactory.CurrentEmoji ?? EmojiData.LoadEmbedded(), null);
        return _shortcodeChecker.FindExact(name) is not null;
    }

    internal void ResetFrecency()
    {
        _settings.ResetFrecency();
        RebuildData();
    }

    /// <summary>"Emoji data: version X, last checked (date)" for the General page.</summary>
    internal string EmojiDataDescription() =>
        $"Emoji data: version {EmojiDataVersion()}, last checked {UI.Pages.GeneralPage.FormatDate(_updates.LastChecked)}";

    /// <summary>The data version as a date (yyyyMMddNN → 2026.10.16, or 2026.10.16.2 for a second build that day).</summary>
    internal string EmojiDataVersion()
    {
        var v = (_dataFactory.CurrentEmoji ?? EmojiData.LoadEmbedded()).DataVersion;
        var (date, build) = (v / 100, v % 100);
        var text = $"{date / 10000}.{date / 100 % 100:00}.{date % 100:00}";
        return build == 0 ? text : $"{text}.{build}";
    }

    internal DateTimeOffset? EmojiDataLastChecked => _updates.LastChecked;

    internal void OpenSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(this);
        }

        _settingsWindow.BringToFront();
    }

    private bool OnMessage(uint message, nuint wParam, nint lParam)
    {
        if (_popup.HandleMessage(message) || _tray.HandleMessage(message, wParam, lParam))
            return true;

        switch (message)
        {
            case Program.WmOpenSettings:
                OpenSettings();
                return true;
            case Program.WmQuit:
                Shutdown();
                return true;
            case User32.WM_POWERBROADCAST when (uint)wParam is PowrProf.PBT_APMRESUMEAUTOMATIC or PowrProf.PBT_APMRESUMESUSPEND:
                Log.Info("resumed from sleep");
                AfterWake();
                return false;
            case User32.WM_WTSSESSION_CHANGE when (int)wParam == WtsApi32.WTS_SESSION_UNLOCK:
                Log.Info("session unlocked");
                AfterWake();
                return true;
            case User32.WM_DISPLAYCHANGE:
            case User32.WM_DPICHANGED:
                _engine.Cancel();
                _tray.Update();
                return false;
            case User32.WM_SETTINGCHANGE:
                // Taskbar light/dark switch: swap the tray glyph.
                _tray.Update();
                return false;
        }

        return false;
    }

    /// <summary>After sleep or unlock: forget any session and make sure Windows didn't drop the hook.</summary>
    private void AfterWake()
    {
        _engine.Cancel();
        _engine.KickWatchdog(force: true);
        _tray.Update();
        _updates.Schedule(); // timers don't count time asleep the same way: recompute from the wall clock
    }

    /// <summary>Clean shutdown: unhook first so no key can be swallowed, then remove the tray icon and save.</summary>
    public void Shutdown()
    {
        if (_shutDown)
            return;
        _shutDown = true;
        Log.Info(_engine.IdleKeyLatency.Describe());
        Log.Info(_engine.ActiveKeyLatency.Describe());
        _popup.LogLatency();
        Dispose();
        _app.Exit();
    }

    public void Dispose()
    {
        _engine?.Dispose();
        _updates?.Dispose();
        _tray?.Dispose();
        _settingsWindow?.CloseForGood();
        _settings?.Dispose();
        WtsApi32.WTSUnRegisterSessionNotification(_messages.Handle);
        _messages?.Dispose();
    }
}
