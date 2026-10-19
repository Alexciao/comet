using Inlay.Core.Updates;
using Microsoft.UI.Dispatching;

namespace Inlay.App.Services;

/// <summary>
/// The scheduler shared by the update checks (emoji data, new Inlay versions): once at every launch, then once a
/// day (<see cref="UpdateSchedule"/>). One one-shot timer armed for the next due time, re-armed after each check
/// and whenever settings, pause or sleep change the picture. No polling; each check runs on its own
/// low-priority thread.
/// </summary>
internal sealed class DailyCheck : IDisposable
{
    /// <summary>A skipped check (metered or offline) is retried after this, without counting as a check.</summary>
    public static readonly TimeSpan RetryAfterSkip = TimeSpan.FromHours(6);

    private readonly DispatcherQueueTimer _timer;
    private readonly Func<bool> _enabled;
    private readonly Func<DateTimeOffset?> _lastChecked;
    private readonly Func<Task<bool>> _check;
    private readonly bool _checkNow;
    private readonly long _startedAt = Environment.TickCount64;
    private bool _checkedThisLaunch;
    private bool _running;

    /// <param name="check">One check, started on the UI thread; returns false when it was skipped (metered or offline).</param>
    /// <param name="checkNowVariable">Environment variable that makes the launch check run at once (tests).</param>
    public DailyCheck(DispatcherQueue dispatcher, Func<bool> enabled, Func<DateTimeOffset?> lastChecked, Func<Task<bool>> check, string checkNowVariable)
    {
        _enabled = enabled;
        _lastChecked = lastChecked;
        _check = check;
        _checkNow = Environment.GetEnvironmentVariable(checkNowVariable) == "1" || ForcedForDebugging();
        _timer = dispatcher.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => _ = RunAsync();
    }

    /// <summary>Arms the timer for the next due check, or disarms it when the check is off.</summary>
    public void Schedule() => Schedule(after: null);

    /// <summary>
    /// Runs <paramref name="work"/> on a background thread of its own at the lowest priority. The requests are
    /// synchronous (<c>UpdateHttp</c>), so the parsing and verifying that follow them stay on that thread too.
    /// </summary>
    public static Task<T> InBackground<T>(Func<Task<T>> work)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                result.SetResult(work().GetAwaiter().GetResult());
            }
            catch (Exception ex)
            {
                result.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.Lowest,
            Name = "Inlay update check",
        };
        thread.Start();
        return result.Task;
    }

    private void Schedule(TimeSpan? after)
    {
        _timer.Stop();
        if (_running || !_enabled())
            return; // a running check re-arms the timer when it ends
        var delay = after ?? UpdateSchedule.NextCheckIn(DateTimeOffset.UtcNow, _lastChecked(), TimeSpan.FromMilliseconds(Environment.TickCount64 - _startedAt), _checkedThisLaunch);
        if (_checkNow && after is null && !_checkedThisLaunch)
            delay = TimeSpan.FromMilliseconds(500);
        _timer.Interval = delay;
        _timer.Start();
    }

    private async Task RunAsync()
    {
        if (_running || !_enabled())
            return;
        _running = true;
        var done = false;
        try
        {
            done = await _check().ConfigureAwait(true);
            _checkedThisLaunch |= done;
        }
        finally
        {
            _running = false;
            Schedule(done ? null : RetryAfterSkip);
        }
    }

    /// <summary><c>INLAY_FORCE_UPDATE_CHECK=1</c> runs both checks at once at startup, in Debug builds only.</summary>
    private static bool ForcedForDebugging()
    {
#if DEBUG
        return Environment.GetEnvironmentVariable("INLAY_FORCE_UPDATE_CHECK") == "1";
#else
        return false;
#endif
    }

    public void Dispose() => _timer.Stop();
}
