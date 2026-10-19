using Inlay.Core.Updates;
using Microsoft.UI.Dispatching;

namespace Inlay.App.Services;

/// <summary>
/// The scheduler shared by the weekly checks (emoji data, new Inlay versions): one one-shot timer armed for the
/// next due time (<see cref="UpdateSchedule"/>), re-armed after each check and whenever settings, pause or sleep
/// change the picture. No polling, no thread.
/// </summary>
internal sealed class WeeklyCheck : IDisposable
{
    /// <summary>A skipped check (metered or offline) is retried after this, without counting as a check.</summary>
    public static readonly TimeSpan RetryAfterSkip = TimeSpan.FromHours(6);

    private readonly DispatcherQueueTimer _timer;
    private readonly Func<bool> _enabled;
    private readonly Func<DateTimeOffset?> _lastChecked;
    private readonly string _checkNowVariable;
    private readonly long _startedAt = Environment.TickCount64;

    /// <param name="checkNowVariable">Environment variable that makes the first check run at once (tests).</param>
    public WeeklyCheck(DispatcherQueue dispatcher, Func<bool> enabled, Func<DateTimeOffset?> lastChecked, Func<Task> check, string checkNowVariable)
    {
        _enabled = enabled;
        _lastChecked = lastChecked;
        _checkNowVariable = checkNowVariable;
        _timer = dispatcher.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => _ = check();
    }

    /// <summary>Arms the timer for the next due check (or <paramref name="after"/>), or disarms it when the check is off.</summary>
    public void Schedule(TimeSpan? after = null)
    {
        _timer.Stop();
        if (!_enabled())
            return;
        var last = _lastChecked();
        var delay = after ?? UpdateSchedule.NextCheckIn(DateTimeOffset.UtcNow, last, TimeSpan.FromMilliseconds(Environment.TickCount64 - _startedAt));
        if (Environment.GetEnvironmentVariable(_checkNowVariable) == "1" && last is null)
            delay = TimeSpan.FromMilliseconds(500);
        _timer.Interval = delay;
        _timer.Start();
    }

    public void Dispose() => _timer.Stop();
}
