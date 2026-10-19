using System.Diagnostics;
using Inlay.Core.Emoji;
using Inlay.Core.Settings;
using Inlay.Platform;
using Inlay.Platform.Fonts;
using Inlay.Platform.Updates;
using Microsoft.UI.Dispatching;

namespace Inlay.App.Services;

/// <summary>
/// Keeps the emoji data current without the user noticing: a check at launch and then daily (<see cref="DailyCheck"/>,
/// one one-shot timer, never a polling loop). A verified new pack replaces the dataset through
/// <see cref="EngineDataFactory"/>; the engine applies it only while the popup is closed. Also runs the
/// installed-font check for whichever dataset is in use.
/// </summary>
internal sealed class EmojiUpdateService : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly DailyCheck _check;
    private readonly JsonStore<EmojiUpdateState> _store;
    private readonly EmojiUpdater _updater;
    private readonly EngineDataFactory _factory;
    private readonly Func<AppSettings> _settings;
    private readonly Func<bool> _paused;
    private readonly Action _dataReplaced;
    private readonly Lock _fontGate = new();
    private EmojiUpdateState _state;
    private WeakReference<EmojiData>? _fontCheckedFor;
    private bool[]? _fontVisible;

    public EmojiUpdateService(DispatcherQueue dispatcher, EngineDataFactory factory, Func<AppSettings> settings, Func<bool> paused, Action dataReplaced)
    {
        _dispatcher = dispatcher;
        _factory = factory;
        _settings = settings;
        _paused = paused;
        _dataReplaced = dataReplaced;
        var dir = AppPaths.DataDirectory;
        _store = new JsonStore<EmojiUpdateState>(Path.Combine(dir, "emoji-update.json"), InlayJsonContext.Default.EmojiUpdateState, EmojiUpdateState.CurrentVersion);
        _state = _store.Load().Value;
        _updater = new EmojiUpdater(UpdateHttp.Client, EmojiUpdater.Endpoint, dir, new Version(AppPaths.Version));

        factory.LoadEmoji = () => EmojiUpdater.LoadStartupData(dir);
        factory.Visibility = FontVisibility;

        _check = new DailyCheck(dispatcher, () => EmojiUpdatePolicy.Enabled(_settings(), _paused()), () => _state.LastChecked, CheckAsync, "INLAY_EMOJI_UPDATE_NOW");
    }

    public DateTimeOffset? LastChecked => _state.LastChecked;

    /// <summary>Arms the timer for the next due check, or disarms it when updates are off.</summary>
    public void Schedule() => _check.Schedule();

    private async Task<bool> CheckAsync()
    {
        if (_factory.CurrentEmoji is not { } current)
            return false; // the data is still loading: try again later
        var started = Stopwatch.GetTimestamp();
        var (version, count) = (current.DataVersion, current.Count);
        var result = await DailyCheck.InBackground(() => _updater.CheckAsync(_state, version, count, EmojiUpdater.NetworkUsable)).ConfigureAwait(true);
        Log.Info($"emoji update check: {result.Outcome}, {result.Detail} ({Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms)");
        if (result.Outcome == UpdateOutcome.Skipped)
            return false;
        Save();

        if (result.Data is { } data)
        {
            // Frecency and the skin tone are keyed by the emoji themselves, so they carry over to the new data.
            _factory.ReplaceEmoji(data);
            _dataReplaced();
        }

        return true;
    }

    /// <summary>
    /// The "Check for updates" button: the same request, verification and swap as the daily check, now, whatever the
    /// schedule, the automatic-update toggle or the connection's cost say. Null when Emoji is off (no data in use to
    /// check). Logs nothing (the caller logs one line for both checks).
    /// </summary>
    public async Task<(UpdateResult Result, int DataVersion)?> CheckNowAsync()
    {
        if (_factory.CurrentEmoji is not { } current)
            return null;
        var (version, count) = (current.DataVersion, current.Count);
        var result = await DailyCheck.InBackground(() => _updater.CheckAsync(_state, version, count, () => true)).ConfigureAwait(true);
        Save();
        if (result.Data is { } data)
        {
            _factory.ReplaceEmoji(data);
            _dataReplaced();
            version = data.DataVersion;
        }

        return (result, version);
    }

    private void Save()
    {
        try
        {
            _store.Save(_state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Info($"emoji update state not saved ({ex.GetType().Name})");
        }
    }

    /// <summary>
    /// Which emoji the installed font draws (see <see cref="EmojiFontCheck"/>), for the dataset in use. The first
    /// call for a dataset starts the check in the background and returns null (the data's own flags apply);
    /// when the result differs from them, the data is rebuilt once with it.
    /// </summary>
    private bool[]? FontVisibility(EmojiData data)
    {
        lock (_fontGate)
        {
            if (_fontCheckedFor?.TryGetTarget(out var checkedFor) == true && ReferenceEquals(checkedFor, data))
                return _fontVisible;
            _fontCheckedFor = new WeakReference<EmojiData>(data); // weak: Emoji turned off lets the data go (L6)
            _fontVisible = null;
        }

        _ = Task.Run(() =>
        {
            var started = Stopwatch.GetTimestamp();
            // Emoji against Segoe UI Emoji, symbols against the text fonts.
            var visible = EmojiFontCheck.Check(data);
            var differs = visible is null ? 0 : Enumerable.Range(0, visible.Length).Count(i => visible[i] != data.RenderedOnReferenceFont(i));
            Log.Info($"font check: {visible?.Count(v => v) ?? 0} of {data.Count} emoji and symbols drawable, {differs} differ from the data ({Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms)");
            lock (_fontGate)
            {
                if (_fontCheckedFor?.TryGetTarget(out var current) != true || !ReferenceEquals(current, data))
                    return;
                _fontVisible = visible;
            }

            if (differs > 0)
                _dispatcher.TryEnqueue(() => _dataReplaced());
        });
        return null;
    }

    public void Dispose() => _check.Dispose();
}
