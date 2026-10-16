using Comet.App.Services;
using Comet.App.UI;
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

    public void Start()
    {
        _settings = new SettingsService(_dispatcher);

        _popup = new PopupWindow();
        _messages = new MessageWindow(Program.MessageWindowClass, OnMessage);
        _popup.Attach(_messages);
        _popup.WarmUp();

        _engine = new InputEngine(_popup, _options.AcceptInjectedInput);
        _engine.Picked += _settings.RecordPick;
        _engine.Start(Config(), BuildData());
        _settings.Changed += OnSettingsChanged;

        _tray = new TrayIcon(_messages.Handle);
        _tray.OpenSettings += OpenSettings;
        _tray.PauseChanged += SetPaused;
        _tray.Quit += Shutdown;
        _tray.Show();

        WtsApi32.WTSRegisterSessionNotification(_messages.Handle, WtsApi32.NOTIFY_FOR_THIS_SESSION);
        StartupRegistration.Apply(_settings.Settings.StartAtLogin);

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
        RebuildData();
    }

    /// <summary>Rebuilds search data off the UI thread; only the newest build is handed to the engine.</summary>
    internal void RebuildData()
    {
        var version = ++_rebuildVersion;
        var settings = _settings.Settings;
        var snippets = _settings.Snippets.ToList();
        var autoReplace = _settings.AutoReplace.ToList();
        var frecency = _settings.FrecencyScores();
        var emojiWasLoaded = _dataFactory.CurrentEmoji is not null;
        _ = Task.Run(() =>
        {
            var data = _dataFactory.Build(settings, snippets, autoReplace, frecency);
            if (version != Volatile.Read(ref _rebuildVersion))
                return;
            _engine.SetData(data);
            if (emojiWasLoaded && data.Emoji is null)
            {
                // Emoji was just turned off: let the dataset and its index go now rather than whenever.
                GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
            }
        });
    }

    private void SetPaused(bool paused)
    {
        _paused = paused;
        _tray.Paused = paused;
        _engine.SetConfig(Config());
        Log.Info(paused ? "paused" : "resumed");
    }

    internal void TogglePause() => SetPaused(!_paused);

    internal void OpenSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
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
        _tray?.Dispose();
        _settingsWindow?.Close();
        _settings?.Dispose();
        WtsApi32.WTSUnRegisterSessionNotification(_messages.Handle);
        _messages?.Dispose();
    }
}
