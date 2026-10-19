using Inlay.App.Services;
using System.Diagnostics;
using System.Globalization;
using Inlay.App.UI;
using Inlay.App.UI.Settings;
using Inlay.Core.Emoji;
using Inlay.Core.Input;
using Inlay.Platform.Images;
using Inlay.Platform.Input;
using Inlay.Core.Updates;
using Inlay.Core.Search;
using Inlay.Platform;
using Inlay.Platform.Engine;
using Inlay.Platform.Native;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Inlay.App;

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
    private QuickInsertWindow _popup = null!;
    private MessageWindow _messages = null!;
    private TrayIcon _tray = null!;
    private GlobalShortcut _shortcut = null!;
    private ImagesService _images = null!;
    private const int QuickInsertHotKeyId = 1; // GlobalShortcut also uses id + 1 to test a combination
    private SettingsWindow? _settingsWindow;
    private readonly PauseService _pause;
    private int _rebuildVersion;
    private bool _shutDown;
    private SearchEngine? _shortcodeChecker;
    private EmojiUpdateService _updates = null!;
    private AppUpdateService _appUpdates = null!;
    private DispatcherQueueTimer? _releaseTimer;
    /// <summary>Closed Settings windows, weakly: a window that stays in memory after closing is logged as a leak.</summary>
    private readonly List<WeakReference> _closedWindows = [];

    public AppHost(Application app, LaunchOptions options)
    {
        _app = app;
        _options = options;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _pause = new PauseService(_dispatcher);
        _pause.Changed += OnPauseChanged;
    }

    internal SettingsService Settings => _settings;

    internal InputEngine Engine => _engine;

    internal EngineDataFactory DataFactory => _dataFactory;

    internal bool Paused => _pause.IsPaused;

    /// <summary>Raised on the UI thread when Inlay is paused or resumed (tray or Settings).</summary>
    internal event Action<bool>? PausedChanged;

    /// <summary>Raised on the UI thread when a newer Inlay is found (or the notification is turned off).</summary>
    internal event Action? AppUpdateChanged;

    /// <summary>A newer Inlay release, when "Notify me about Inlay updates" is on and one was found.</summary>
    internal AppRelease? AvailableUpdate => _appUpdates.Available;

    internal DateTimeOffset? AppUpdateLastChecked => _appUpdates.LastChecked;

    public void Start()
    {
        _settings = new SettingsService(_dispatcher);
        _updates = new EmojiUpdateService(_dispatcher, _dataFactory, () => _settings.Settings, () => _pause.IsPaused, OnEmojiDataReplaced);
        _appUpdates = new AppUpdateService(_dispatcher, () => _settings.Settings, () => _pause.IsPaused, OnAppUpdateChanged);

        _images = new ImagesService(new ImageLibrary(_settings.ImageLibraryDirectory));
        _images.ItemsChanged += () => _dispatcher.TryEnqueue(() => RebuildData()); // a collection's files changed
        _dataFactory.Images = _images;
        UpdateImages();

        _popup = new QuickInsertWindow();
        _popup.Images = _images;
        _popup.PrepareImages(_settings.Settings.Features.Images && _settings.Images.Count > 0);
        _messages = new MessageWindow(Program.MessageWindowClass, OnMessage);
        _popup.Attach(_messages);
        _popup.WarmUp();

        _engine = new InputEngine(_popup, _options.AcceptInjectedInput);
        _engine.Picked += _settings.RecordPick;
        _engine.CreateSnippetRequested += (name, window) => _dispatcher.TryEnqueue(() => _ = CreateSnippetAsync(name, window));
        _engine.ImagePasteMethodLearned += (process, method) => _dispatcher.TryEnqueue(() => LearnImagePasteMethod(process, method));
        _engine.Start(Config(), BuildData());
        _shortcut = new GlobalShortcut(_messages.Handle, QuickInsertHotKeyId);
        ApplyShortcut();
        _settings.Changed += OnSettingsChanged;
        PrefetchEmoji();

        _tray = new TrayIcon(_messages.Handle);
        _tray.OpenSettings += OpenSettings;
        _tray.PauseRequested += _pause.Pause;
        _tray.ResumeRequested += _pause.Resume;
        _tray.Quit += Shutdown;
        _tray.OpenUpdatePage += OpenUpdatePage;
        _tray.Status = Status();
        _tray.UpdateVersion = AvailableUpdate?.DisplayVersion;
        _tray.Show();

        WtsApi32.WTSRegisterSessionNotification(_messages.Handle, WtsApi32.NOTIFY_FOR_THIS_SESSION);
        StartupRegistration.Apply(_settings.Settings.StartAtLogin);

        _updates.Schedule();
        _appUpdates.Schedule();
        Log.Info($"started: hook {(_engine.HookInstalled ? "installed" : "not needed")}, emoji {(_dataFactory.CurrentEmoji is null ? "not loaded" : "loaded")}");
        if (!_options.Background)
            OpenSettings();
    }

    private EngineConfig Config() => EngineConfig.From(_settings.Settings, _pause.IsPaused);

    private EngineData BuildData() =>
        _dataFactory.Build(_settings.Settings, [.. _settings.Snippets], [.. _settings.Shorthands], _settings.FrecencyScores());

    /// <summary>Registers the Quick Insert shortcut from the settings; none while paused or with Quick Insert off.</summary>
    private void ApplyShortcut()
    {
        var active = !_pause.IsPaused && Config().QuickInsertEnabled;
        var status = _shortcut.Set(active ? Shortcut.FromSetting(_settings.Settings.QuickInsertShortcut) : Shortcut.None);
        if (status is ShortcutRegistration.InUse or ShortcutRegistration.Failed)
            Log.Warn($"Quick Insert shortcut {_settings.Settings.QuickInsertShortcut}: {status}");
        ShortcutChanged?.Invoke();
    }

    /// <summary>Whether the Quick Insert shortcut is registered, off, or held by another app (for Settings).</summary>
    internal ShortcutRegistration ShortcutStatus => _shortcut.Status;

    /// <summary>Raised on the UI thread after the shortcut was registered again (or couldn't be).</summary>
    internal event Action? ShortcutChanged;

    /// <summary>True when <paramref name="shortcut"/> could be registered (Inlay's own counts as free).</summary>
    internal bool CanRegisterShortcut(Shortcut shortcut) => _shortcut.Test(shortcut) == ShortcutRegistration.Registered;

    /// <summary>While the recorder listens, the registered shortcut is released so its keys reach the dialog.</summary>
    internal void SuspendShortcut(bool suspended) => _shortcut.IsSuspended = suspended;

    /// <summary>The user's images, for Settings (thumbnails, adding files) and Quick Insert.</summary>
    internal ImagesService Images => _images;

    /// <summary>
    /// Starts, updates or stops folder watching and collections to match the settings. With Images off or no entries,
    /// nothing runs: no watchers, no scans, no thumbnails.
    /// </summary>
    private void UpdateImages()
    {
        var on = _settings.Settings.Features.Images;
        _images.Update(on, [.. _settings.Images], [.. _settings.Snippets.Select(s => s.Name)]);
        _popup?.PrepareImages(on && _settings.Images.Count > 0);
    }

    /// <summary>An app didn't read a pasted image: next time it gets the clipboard formats it is more likely to read.</summary>
    private void LearnImagePasteMethod(string process, string method)
    {
        if (process.Length == 0 || _settings.Settings.ImagePasteMethods.GetValueOrDefault(process) == method)
            return;
        Log.Info($"image paste: {process} read nothing; using \"{method}\" there from now on");
        _settings.Settings.ImagePasteMethods[process] = method;
        _settings.SaveSettings();
    }

    private void OnSettingsChanged(SettingsParts parts)
    {
        _engine.SetConfig(Config());
        if (parts.HasFlag(SettingsParts.Settings) || parts.HasFlag(SettingsParts.Images) || parts.HasFlag(SettingsParts.Snippets))
            UpdateImages();
        if (parts.HasFlag(SettingsParts.Settings))
        {
            ApplyShortcut();
            _shortcodeChecker = null; // rebuilt on demand: Emoji may be off (let the data go, L6) or Symbols changed
        }
        if (parts.HasFlag(SettingsParts.Settings))
            StartupRegistration.Apply(_settings.Settings.StartAtLogin);
        RebuildData(prefetch: parts.HasFlag(SettingsParts.Settings)); // emoji may have been turned on, or the skin tone changed
        if (parts.HasFlag(SettingsParts.Settings))
        {
            _updates.Schedule();
            _appUpdates.Schedule();
            OnAppUpdateChanged(); // the notification may have been turned on or off
            _tray.Status = Status();
        }
    }

    private void OnAppUpdateChanged()
    {
        var version = AvailableUpdate?.DisplayVersion;
        if (_tray.UpdateVersion == version)
            return;
        _tray.UpdateVersion = version;
        AppUpdateChanged?.Invoke();
    }

    /// <summary>Opens the release page of the newer version in the browser.</summary>
    internal void OpenUpdatePage()
    {
        if (AvailableUpdate is { } release)
            OpenInBrowser(release.Page);
    }

    internal static void OpenInBrowser(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error("open link", ex);
        }
    }

    /// <summary>"Active", "Paused" or "All features off": the tray tooltip and the Settings pane header.</summary>
    internal string Status()
    {
        var f = _settings.Settings.Features;
        return _pause.IsPaused ? _pause.Status : !f.Emoji && !f.Snippets && !f.Images && !f.Shorthands ? "All features off" : "Active";
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
            .Where(i => i >= 0 && !data.IsSymbol(i)); // symbols are text, not pre-rendered bitmaps
        var popular = Enumerable.Range(0, data.Count)
            .Where(i => !data.IsSymbol(i) && data.Popularity(i) <= PrefetchPopularRank && data.RenderedOnReferenceFont(i))
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
        var shorthands = _settings.Shorthands.ToList();
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
                data = _dataFactory.Build(settings, snippets, shorthands, frecency);
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

    /// <summary>Pauses everything for <paramref name="duration"/>, or until resumed (null). Not saved: a restart resumes.</summary>
    internal void Pause(TimeSpan? duration) => _pause.Pause(duration);

    internal void Resume() => _pause.Resume();

    private void OnPauseChanged()
    {
        var paused = _pause.IsPaused;
        _tray.ResumeText = _pause.ResumeText;
        _tray.Paused = paused;
        _tray.Status = Status();
        _engine.SetConfig(Config());
        ApplyShortcut();
        _updates.Schedule();
        _appUpdates.Schedule();
        Log.Info(paused ? $"paused{(_pause.Until is { } u ? $" until {u:u}" : "")}" : "resumed");
        PausedChanged?.Invoke(paused);
    }

    /// <summary>True when <paramref name="name"/> is also an emoji shortcode (warned about in the snippet dialog).</summary>
    internal bool IsEmojiShortcode(string name)
    {
        _shortcodeChecker ??= new SearchEngine(_dataFactory.CurrentEmoji ?? EmojiData.LoadEmbedded(), null, includeSymbols: _settings.Settings.Features.Symbols);
        return _shortcodeChecker.FindExact(name) is not null;
    }

    internal void ResetFrecency()
    {
        _settings.ResetFrecency();
        RebuildData();
    }

    /// <summary>"Emoji data version 2026.10.16 · Last checked 19/10/2026" for Settings › General › Updates.</summary>
    internal string EmojiDataDescription() =>
        $"Emoji data version {EmojiDataVersion()} · Last checked {FormatDate(_updates.LastChecked)}";

    internal static string FormatDate(DateTimeOffset? date) =>
        date is { } d ? d.LocalDateTime.ToString("d", CultureInfo.CurrentCulture) : "never";

    /// <summary>The data version as a date (yyyyMMddNN → 2026.10.16, or 2026.10.16.2 for a second build that day).</summary>
    internal string EmojiDataVersion()
    {
        var v = (_dataFactory.CurrentEmoji ?? EmojiData.LoadEmbedded()).DataVersion;
        var (date, build) = (v / 100, v % 100);
        var text = $"{date / 10000}.{date / 100 % 100:00}.{date % 100:00}";
        return build == 0 ? text : $"{text}.{build}";
    }

    internal DateTimeOffset? EmojiDataLastChecked => _updates.LastChecked;

    /// <summary>
    /// Quick Insert's "Create snippet :name": the New snippet dialog in Settings, then focus goes back to the window
    /// the user was typing in, and the snippet is inserted there on Save (nothing on Cancel).
    /// </summary>
    private async Task CreateSnippetAsync(string name, nint window)
    {
        OpenSettings();
        if (_settingsWindow is not { } settings)
            return;
        var snippet = await settings.CreateSnippetAsync(name).ConfigureAwait(true);
        Log.Info($"create snippet: {(snippet is null ? "cancelled" : "saved")}, returning focus");
        if (window == 0 || !User32.IsWindow(window))
            return;
        User32.ForceForeground(window);
        if (snippet is null)
            return;
        // Insert once the window really has focus again (focus moves asynchronously); give up after a second.
        for (var waited = 0; User32.GetForegroundWindow() != window && waited < 1000; waited += 20)
            await Task.Delay(20).ConfigureAwait(true);
        if (User32.GetForegroundWindow() != window)
        {
            Log.Warn("create snippet: the window didn't get focus back; nothing inserted");
            return;
        }

        // Saved with Ctrl+Enter: Ctrl may still be down, and typing with it held would send shortcuts instead.
        for (var waited = 0; ModifiersDown() && waited < 1000; waited += 20)
            await Task.Delay(20).ConfigureAwait(true);
        // The window is in front, but it puts keyboard focus back into its text field a moment later (Notepad's editor
        // measured: ~100 ms); keys sent before that are lost.
        await Task.Delay(FocusSettleMs).ConfigureAwait(true);

        _settings.RecordPick(Inlay.Core.Settings.Frecency.SnippetKey(snippet.Id));
        _engine.InsertSnippet(snippet.Text);
    }

    private const int FocusSettleMs = 200;

    private static bool ModifiersDown() =>
        (User32.GetAsyncKeyState(0x11) & 0x8000) != 0 || (User32.GetAsyncKeyState(0x12) & 0x8000) != 0 || (User32.GetAsyncKeyState(0x10) & 0x8000) != 0
        || (User32.GetAsyncKeyState(0x5B) & 0x8000) != 0 || (User32.GetAsyncKeyState(0x5C) & 0x8000) != 0; // Ctrl, Alt, Shift, Win

    /// <summary>Opens Settings, creating the window if it isn't open (it is destroyed when closed).</summary>
    internal void OpenSettings()
    {

        _releaseTimer?.Stop();
        if (_settingsWindow is null)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                _settingsWindow = new SettingsWindow(this, started);
            }
            catch (Exception ex)
            {
                // Settings that can't be built must not take Inlay (and typing everywhere) down with them.
                Log.WriteCrash(ex, "settings window");
                return;
            }
        }

        _settingsWindow.BringToFront();
    }

    /// <summary>
    /// The Settings window was closed and destroyed. Once XAML has let go of it, collect and trim the working set,
    /// so it goes back to where it was before Settings opened: a tray app shouldn't carry a closed window around.
    /// </summary>
    internal void OnSettingsClosed(SettingsWindow window)
    {
        if (_settingsWindow == window)
            _settingsWindow = null;
        _closedWindows.RemoveAll(w => !w.IsAlive);
        _closedWindows.Add(new WeakReference(window));
        _releaseTimer ??= CreateReleaseTimer();
        _releaseTimer.Stop();
        _releaseTimer.Start();
    }

    /// <summary>
    /// Collects in a few passes on separate UI-thread ticks: XAML lets go of the native side of a closed window only
    /// after a collection has shown its managed objects unreachable, so a second collection in the same tick
    /// would find them still held.
    /// </summary>
    private DispatcherQueueTimer CreateReleaseTimer()
    {
        var timer = _dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.IsRepeating = true;
        var pass = 0;
        timer.Tick += (_, _) =>
        {
            if (_settingsWindow is not null)
            {
                timer.Stop();
                pass = 0;
                return;
            }

            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            if (++pass < ReleasePasses)
                return;
            timer.Stop();
            pass = 0;
            var before = Environment.WorkingSet;
            Kernel32.SetProcessWorkingSetSize(Kernel32.GetCurrentProcess(), -1, -1);
            _popup.WarmUp(); // fault the popup's own pages back in before the next keystroke needs them
            Log.Info($"settings released: working set {before / 1048576} MB, trimmed to {Environment.WorkingSet / 1048576} MB");
            // The window closed last may still be on its way out; any older one still here is a leak.
            _closedWindows.RemoveAll(w => !w.IsAlive);
            if (_closedWindows.Count > 1)
                Log.Warn($"{_closedWindows.Count - 1} closed Settings windows are still in memory");
        };
        return timer;
    }

    private const int ReleasePasses = 3;

    private const uint WmEndSession = 0x0016;

    private const uint WmTimeChange = 0x001E;

    private bool OnMessage(uint message, nuint wParam, nint lParam)
    {
        if (_popup.HandleMessage(message) || _tray.HandleMessage(message, wParam, lParam))
            return true;
        if (_shortcut.IsPressed(message, wParam))
        {
            _engine.OpenFromShortcut();
            return true;
        }

        switch (message)
        {
            case Program.WmOpenSettings:
                OpenSettings();
                return true;
            case Program.WmQuit:
                Shutdown();
                return true;
            case WmEndSession when wParam != 0:
                // Signing out or shutting down: the process ends right after this returns, without Shutdown().
                Log.Info("Windows is ending the session");
                _settings.SaveFrecency();
                _settings.Flush();
                return false;
            case User32.WM_POWERBROADCAST when (uint)wParam == PowrProf.PBT_APMSUSPEND:
                _engine.Cancel(); // going to sleep: close the popup now, not on wake
                return false;
            case User32.WM_POWERBROADCAST when (uint)wParam is PowrProf.PBT_APMRESUMEAUTOMATIC or PowrProf.PBT_APMRESUMESUSPEND:
                Log.Info("resumed from sleep");
                AfterWake();
                return false;
            case User32.WM_WTSSESSION_CHANGE when (int)wParam == WtsApi32.WTS_SESSION_LOCK:
                // Locking (or sleeping with the lid): no session survives, so a key pressed while the lock screen
                // comes up can't insert the old selection.
                _engine.Cancel();
                return true;
            case User32.WM_WTSSESSION_CHANGE when (int)wParam == WtsApi32.WTS_SESSION_UNLOCK:
                Log.Info("session unlocked");
                AfterWake();
                return true;
            case User32.WM_DISPLAYCHANGE:
            case User32.WM_DPICHANGED:
                _engine.Cancel();
                _tray.Update();
                return false;
            case WmTimeChange:
                // The clock or time zone changed: a timed pause and the daily checks follow the wall clock.
                _pause.Recheck();
                _updates.Schedule();
                _appUpdates.Schedule();
                return false;
        }

        return false;
    }

    /// <summary>After sleep or unlock: forget any session and make sure Windows didn't drop the hook.</summary>
    private void AfterWake()
    {
        _pause.Recheck(); // a pause that ran out during sleep ends now
        _engine.Cancel();
        _engine.KickWatchdog(force: true);
        _tray.Update();
        _updates.Schedule(); // timers don't count time asleep the same way: recompute from the wall clock
        _appUpdates.Schedule();
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
        _pause.Dispose();
        _images?.Dispose();
        _shortcut?.Dispose();
        _engine?.Dispose();
        _updates?.Dispose();
        _appUpdates?.Dispose();
        _releaseTimer?.Stop();
        _tray?.Dispose();
        _settingsWindow?.SavePlacement();
        _settingsWindow?.Close();
        _settings?.Dispose();
        WtsApi32.WTSUnRegisterSessionNotification(_messages.Handle);
        _messages?.Dispose();
    }
}
