namespace Inlay.Core.Updates;

/// <summary>
/// When an update check (emoji data, new Inlay versions) runs: once at every launch, shortly after startup, then
/// once a day while Inlay keeps running. Both checks use this with one one-shot timer each; nothing polls.
/// </summary>
public static class UpdateSchedule
{
    /// <summary>After the launch check, at most one check per interval.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    /// <summary>The launch check waits this long, so sign-in and the first popup stay quiet.</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long to wait before the next check. Until one has run in this launch: <see cref="StartupDelay"/> after
    /// the app started, however recent the last check. After that: a full interval after the last check (wall
    /// clock, so time asleep counts).
    /// </summary>
    public static TimeSpan NextCheckIn(DateTimeOffset now, DateTimeOffset? lastChecked, TimeSpan sinceStart, bool checkedThisLaunch)
    {
        if (!checkedThisLaunch)
            return sinceStart < StartupDelay ? StartupDelay - sinceStart : TimeSpan.Zero;
        var due = (lastChecked ?? now) + Interval;
        if (due > now + Interval)
            due = now + Interval; // a clock that went backwards must not postpone checks indefinitely
        return due > now ? due - now : TimeSpan.Zero;
    }
}
