using Inlay.Core.Settings;
using Inlay.Core.Updates;
using Inlay.Platform;
using Inlay.Platform.Updates;
using Microsoft.UI.Dispatching;

namespace Inlay.App.Services;

/// <summary>
/// Once a week, asks GitHub whether a newer Inlay has been released, on the same schedule and with the same
/// conditional request as the emoji data check. It only tells (an InfoBar in Settings › General and a tray menu
/// item); it never downloads or installs anything. Off when the user turns it off or Inlay is paused.
/// </summary>
internal sealed class AppUpdateService : IDisposable
{
    private readonly WeeklyCheck _check;
    private readonly JsonStore<AppUpdateState> _store;
    private readonly AppUpdateChecker _checker;
    private readonly Func<AppSettings> _settings;
    private readonly Func<bool> _paused;
    private readonly Action _changed;
    private AppUpdateState _state;
    private bool _running;

    public AppUpdateService(DispatcherQueue dispatcher, Func<AppSettings> settings, Func<bool> paused, Action changed)
    {
        _settings = settings;
        _paused = paused;
        _changed = changed;
        _store = new JsonStore<AppUpdateState>(Path.Combine(AppPaths.DataDirectory, "app-update.json"), InlayJsonContext.Default.AppUpdateState, AppUpdateState.CurrentVersion);
        _state = _store.Load().Value;
        _checker = new AppUpdateChecker(UpdateHttp.Client, AppUpdateChecker.Endpoint, new Version(AppPaths.Version));
        _check = new WeeklyCheck(dispatcher, Enabled, () => _state.LastChecked, CheckAsync, "INLAY_APP_UPDATE_NOW");
    }

    public DateTimeOffset? LastChecked => _state.LastChecked;

    /// <summary>A newer release, when notifications are on and one was found.</summary>
    public AppRelease? Available => _settings().NotifyAppUpdates ? _checker.Known(_state) : null;

    private bool Enabled() => AppUpdatePolicy.Enabled(_settings(), _paused());

    public void Schedule() => _check.Schedule();

    private async Task CheckAsync()
    {
        if (_running || !Enabled())
            return;
        _running = true;
        TimeSpan? retry = null;
        try
        {
            var before = Available?.DisplayVersion;
            var (outcome, newer) = await Task.Run(() => _checker.CheckAsync(_state, EmojiUpdater.NetworkUsable)).ConfigureAwait(true);
            Log.Info($"app update check: {outcome}{(newer is null ? "" : $", {newer.DisplayVersion} available")}");
            if (outcome == UpdateOutcome.Skipped)
            {
                retry = WeeklyCheck.RetryAfterSkip;
            }
            else
            {
                var state = _state;
                await Task.Run(() => Save(state)).ConfigureAwait(true);
            }

            if (Available?.DisplayVersion != before)
                _changed();
        }
        finally
        {
            _running = false;
            _check.Schedule(retry);
        }
    }

    private void Save(AppUpdateState state)
    {
        try
        {
            _store.Save(state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Info($"app update state not saved ({ex.GetType().Name})");
        }
    }

    public void Dispose() => _check.Dispose();
}
