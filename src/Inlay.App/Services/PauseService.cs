using Inlay.Core.Pausing;
using Microsoft.UI.Dispatching;

namespace Inlay.App.Services;

/// <summary>
/// Pausing from the tray: for 15 minutes, an hour, or until resumed (<see cref="PauseState"/>). A timed pause ends
/// on one one-shot timer; the time is checked against the wall clock when it fires and after waking, so a pause
/// that ran out during sleep ends on wake. Not saved: restarting resumes everything.
/// </summary>
internal sealed class PauseService : IDisposable
{
    private readonly PauseState _state = new();
    private readonly DispatcherQueueTimer _timer;

    public PauseService(DispatcherQueue dispatcher)
    {
        _timer = dispatcher.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => Recheck();
    }

    /// <summary>Raised on the UI thread when Inlay is paused, resumed, or the pause's end changes.</summary>
    public event Action? Changed;

    public bool IsPaused => _state.IsPaused;

    /// <summary>When a timed pause ends; null for "until I resume" (or not paused).</summary>
    public DateTimeOffset? Until => _state.Until;

    /// <summary>"Paused until 17:45" or "Paused".</summary>
    public string Status => _state.Status();

    /// <summary>"Resume (paused until 17:45)" or "Resume".</summary>
    public string ResumeText => _state.ResumeText();

    /// <summary>Pauses for <paramref name="duration"/>, or until <see cref="Resume"/> when null.</summary>
    public void Pause(TimeSpan? duration)
    {
        _state.Pause(DateTimeOffset.UtcNow, duration);
        Arm();
        Changed?.Invoke();
    }

    public void Resume()
    {
        if (!_state.IsPaused)
            return;
        _timer.Stop();
        _state.Resume();
        Changed?.Invoke();
    }

    /// <summary>On the timer, after waking or a clock change: ends a pause whose time has come, or re-arms for the rest.</summary>
    public void Recheck()
    {
        if (_state.Expire(DateTimeOffset.UtcNow))
        {
            _timer.Stop();
            Changed?.Invoke();
            return;
        }

        Arm();
    }

    private void Arm()
    {
        _timer.Stop();
        if (_state.DueIn(DateTimeOffset.UtcNow) is not { } due)
            return;
        _timer.Interval = due;
        _timer.Start();
    }

    public void Dispose() => _timer.Stop();
}
