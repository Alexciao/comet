using Inlay.Core.Settings;
using Inlay.Core.Updates;
using Inlay.Platform;
using Inlay.Platform.Updates;
using Microsoft.UI.Dispatching;

namespace Inlay.App.Services;

/// <summary>
/// At launch and then daily, asks GitHub whether a newer Inlay has been released, on the same schedule and with
/// the same conditional request as the emoji data check; the answer carries the release notes, kept for "See
/// what's new". It only tells (an InfoBar in Settings › General and a tray menu item); it never downloads or
/// installs anything. Off when the user turns it off or Inlay is paused.
/// </summary>
internal sealed class AppUpdateService : IDisposable
{
    private readonly DailyCheck _check;
    private readonly JsonStore<AppUpdateState> _store;
    private readonly AppUpdateChecker _checker;
    private readonly Func<AppSettings> _settings;
    private readonly Func<bool> _paused;
    private readonly Action _changed;
    private AppUpdateState _state;

    public AppUpdateService(DispatcherQueue dispatcher, Func<AppSettings> settings, Func<bool> paused, Action changed)
    {
        _settings = settings;
        _paused = paused;
        _changed = changed;
        _store = new JsonStore<AppUpdateState>(Path.Combine(AppPaths.DataDirectory, "app-update.json"), InlayJsonContext.Default.AppUpdateState, AppUpdateState.CurrentVersion);
        _state = _store.Load().Value;
        _checker = new AppUpdateChecker(UpdateHttp.Client, AppUpdateChecker.Endpoint, new Version(AppPaths.Version));
        _check = new DailyCheck(dispatcher, Enabled, () => _state.LastChecked, CheckAsync, "INLAY_APP_UPDATE_NOW");
    }

    public DateTimeOffset? LastChecked => _state.LastChecked;

    /// <summary>A newer release with its notes, when notifications are on and one was found.</summary>
    public AppRelease? Available => _settings().NotifyAppUpdates ? _checker.Known(_state) : null;

    private bool Enabled() => AppUpdatePolicy.Enabled(_settings(), _paused());

    public void Schedule() => _check.Schedule();

    private async Task<bool> CheckAsync()
    {
        var before = Available;
        var state = _state;
        var (outcome, newer) = await DailyCheck.InBackground(() => _checker.CheckAsync(state, EmojiUpdater.NetworkUsable)).ConfigureAwait(true);
        Log.Info($"app update check: {outcome}{(newer is null ? "" : $", {newer.DisplayVersion} available")}");
        if (outcome == UpdateOutcome.Skipped)
            return false;

        await Task.Run(() => Save(state)).ConfigureAwait(true);
        var after = Available;
        if (after?.DisplayVersion != before?.DisplayVersion || after?.Tag != before?.Tag)
            _changed();
        return true;
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
