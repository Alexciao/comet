using System.Globalization;

namespace Inlay.Core.Pausing;

/// <summary>
/// Whether Inlay is paused, and until when: for a while (15 minutes, an hour) or until the user resumes. Pure:
/// the caller supplies the wall-clock time and runs one one-shot timer for <see cref="DueIn"/>. Not saved, so
/// restarting resumes everything.
/// </summary>
public sealed class PauseState
{
    /// <summary>The tray menu's "Pause" choices.</summary>
    public static readonly TimeSpan FifteenMinutes = TimeSpan.FromMinutes(15);

    public static readonly TimeSpan OneHour = TimeSpan.FromHours(1);

    public bool IsPaused { get; private set; }

    /// <summary>When a timed pause ends (wall clock); null while paused until the user resumes, or not paused.</summary>
    public DateTimeOffset? Until { get; private set; }

    /// <summary>Pauses for <paramref name="duration"/>, or until <see cref="Resume"/> when null. Replaces any earlier pause.</summary>
    public void Pause(DateTimeOffset now, TimeSpan? duration)
    {
        IsPaused = true;
        Until = now + duration;
    }

    public void Resume()
    {
        IsPaused = false;
        Until = null;
    }

    /// <summary>
    /// Ends a timed pause whose time has come (on the timer, after sleep, after a clock change). Compares wall-clock
    /// times, so time asleep counts. Returns true when it resumed.
    /// </summary>
    public bool Expire(DateTimeOffset now)
    {
        if (Until is not { } until || now < until)
            return false;
        Resume();
        return true;
    }

    /// <summary>When the timer should fire to end the pause; null when there is nothing to end.</summary>
    public TimeSpan? DueIn(DateTimeOffset now)
    {
        if (Until is not { } until)
            return null;
        return until > now ? until - now : TimeSpan.Zero;
    }

    /// <summary>"Paused until 17:45" or "Paused" (the tray tooltip and the Settings pane header).</summary>
    public string Status(TimeZoneInfo? zone = null, CultureInfo? culture = null) =>
        Until is { } until ? $"Paused until {Time(until, zone, culture)}" : "Paused";

    /// <summary>"Resume (paused until 17:45)" or "Resume" (the tray menu item).</summary>
    public string ResumeText(TimeZoneInfo? zone = null, CultureInfo? culture = null) =>
        Until is { } until ? $"Resume (paused until {Time(until, zone, culture)})" : "Resume";

    /// <summary>The user's short time format ("17:45", "5:45 PM"), in their time zone.</summary>
    private static string Time(DateTimeOffset at, TimeZoneInfo? zone, CultureInfo? culture) =>
        TimeZoneInfo.ConvertTime(at, zone ?? TimeZoneInfo.Local).ToString("t", culture ?? CultureInfo.CurrentCulture);
}
