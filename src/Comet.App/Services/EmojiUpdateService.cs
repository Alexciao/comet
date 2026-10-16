using System.Diagnostics;
using Comet.Core.Emoji;
using Comet.Core.Settings;
using Comet.Platform;
using Comet.Platform.Fonts;
using Comet.Platform.Updates;
using Microsoft.UI.Dispatching;

namespace Comet.App.Services;

/// <summary>
/// Keeps the emoji data current without the user noticing: one one-shot timer (re-armed after each check, after
/// settings or pause changes, and after waking), never a polling loop. A verified new pack replaces the dataset
/// through <see cref="EngineDataFactory"/>; the engine applies it only while the popup is closed. Also runs the
/// installed-font check for whichever dataset is in use.
/// </summary>
internal sealed class EmojiUpdateService : IDisposable
{
    /// <summary>A skipped check (metered or offline) is retried after this, without counting as a check.</summary>
    private static readonly TimeSpan s_retryAfterSkip = TimeSpan.FromHours(6);

    private static readonly HttpClient s_http = CreateClient();

    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;
    private readonly JsonStore<EmojiUpdateState> _store;
    private readonly EmojiUpdater _updater;
    private readonly EngineDataFactory _factory;
    private readonly Func<AppSettings> _settings;
    private readonly Func<bool> _paused;
    private readonly Action _dataReplaced;
    private readonly long _startedAt = Environment.TickCount64;
    private readonly Lock _fontGate = new();
    private EmojiUpdateState _state;
    private bool _running;
    private EmojiData? _fontCheckedFor;
    private bool[]? _fontVisible;

    public EmojiUpdateService(DispatcherQueue dispatcher, EngineDataFactory factory, Func<AppSettings> settings, Func<bool> paused, Action dataReplaced)
    {
        _dispatcher = dispatcher;
        _factory = factory;
        _settings = settings;
        _paused = paused;
        _dataReplaced = dataReplaced;
        var dir = AppPaths.DataDirectory;
        _store = new JsonStore<EmojiUpdateState>(Path.Combine(dir, "emoji-update.json"), CometJsonContext.Default.EmojiUpdateState, EmojiUpdateState.CurrentVersion);
        _state = _store.Load().Value;
        _updater = new EmojiUpdater(s_http, EmojiUpdater.Endpoint, dir, new Version(AppPaths.Version));

        factory.LoadEmoji = () => EmojiUpdater.LoadStartupData(dir);
        factory.Visibility = FontVisibility;

        _timer = dispatcher.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => _ = CheckAsync();
    }

    public DateTimeOffset? LastChecked => _state.LastChecked;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Comet/{AppPaths.Version}");
        return client;
    }

    /// <summary>Arms the timer for the next due check, or disarms it when updates are off.</summary>
    public void Schedule() => Schedule(null);

    private void Schedule(TimeSpan? after)
    {
        _timer.Stop();
        if (!EmojiUpdatePolicy.Enabled(_settings(), _paused()))
            return;
        var delay = after ?? EmojiUpdatePolicy.NextCheckIn(DateTimeOffset.UtcNow, _state.LastChecked, TimeSpan.FromMilliseconds(Environment.TickCount64 - _startedAt));
        if (Environment.GetEnvironmentVariable("COMET_EMOJI_UPDATE_NOW") == "1" && _state.LastChecked is null)
            delay = TimeSpan.FromMilliseconds(500); // tests: check right away
        _timer.Interval = delay;
        _timer.Start();
    }

    private async Task CheckAsync()
    {
        if (_running || !EmojiUpdatePolicy.Enabled(_settings(), _paused()) || _factory.CurrentEmoji is not { } current)
            return;
        _running = true;
        TimeSpan? retry = null;
        try
        {
            var started = Stopwatch.GetTimestamp();
            var (version, count) = (current.DataVersion, current.Count);
            var result = await Task.Run(() => _updater.CheckAsync(_state, version, count, EmojiUpdater.NetworkUsable)).ConfigureAwait(true);
            Log.Info($"emoji update check: {result.Outcome}, {result.Detail} ({Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms)");
            if (result.Outcome == UpdateOutcome.Skipped)
                retry = s_retryAfterSkip;
            else
                Save();

            if (result.Data is { } data)
            {
                // Frecency and the skin tone are keyed by the emoji themselves, so they carry over to the new data.
                _factory.ReplaceEmoji(data);
                _dataReplaced();
            }
        }
        finally
        {
            _running = false;
            Schedule(retry);
        }
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
            if (ReferenceEquals(_fontCheckedFor, data))
                return _fontVisible;
            _fontCheckedFor = data;
            _fontVisible = null;
        }

        _ = Task.Run(() =>
        {
            var started = Stopwatch.GetTimestamp();
            var sequences = new string[data.Count];
            for (var i = 0; i < sequences.Length; i++)
                sequences[i] = data.Emoji(i).ToString();
            var visible = EmojiFontCheck.Check(sequences);
            var differs = visible is null ? 0 : Enumerable.Range(0, visible.Length).Count(i => visible[i] != data.RenderedOnReferenceFont(i));
            Log.Info($"font check: {visible?.Count(v => v) ?? 0} of {data.Count} emoji drawable by {EmojiFontCheck.FontFamily}, {differs} differ from the data ({Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms)");
            lock (_fontGate)
            {
                if (!ReferenceEquals(_fontCheckedFor, data))
                    return;
                _fontVisible = visible;
            }

            if (differs > 0)
                _dispatcher.TryEnqueue(() => _dataReplaced());
        });
        return null;
    }

    public void Dispose() => _timer.Stop();
}
