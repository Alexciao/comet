namespace Inlay.Core.Updates;

/// <summary>
/// When a weekly check (emoji data, new Inlay versions) runs: rarely, quietly, never in the first minutes after
/// login. Both checks use this with one one-shot timer each; nothing polls.
/// </summary>
public static class UpdateSchedule
{
    /// <summary>At most one check per interval.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromDays(7);

    /// <summary>Nothing happens in the first minutes after start, so login stays quiet.</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long to wait before the next check: when the last one is a full interval old, but never earlier than
    /// <see cref="StartupDelay"/> after the app started.
    /// </summary>
    public static TimeSpan NextCheckIn(DateTimeOffset now, DateTimeOffset? lastChecked, TimeSpan sinceStart)
    {
        var due = lastChecked is { } last ? last + Interval : now;
        if (due > now + Interval)
            due = now + Interval; // a clock that went backwards must not postpone checks indefinitely
        var earliest = now + (sinceStart < StartupDelay ? StartupDelay - sinceStart : TimeSpan.Zero);
        var at = due > earliest ? due : earliest;
        return at - now;
    }
}
