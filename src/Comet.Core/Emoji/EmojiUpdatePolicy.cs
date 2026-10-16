using Comet.Core.Settings;

namespace Comet.Core.Emoji;

/// <summary>When the app may check for new emoji data: rarely, quietly, and only when it could matter.</summary>
public static class EmojiUpdatePolicy
{
    /// <summary>At most one check per interval.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromDays(7);

    /// <summary>Nothing happens in the first minutes after start, so login stays quiet.</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    /// <summary>Checks only while Emoji is on, the app isn't paused and the user hasn't turned updates off.</summary>
    public static bool Enabled(AppSettings settings, bool paused) =>
        settings.Features.Emoji && settings.UpdateEmojiAutomatically && !paused;

    /// <summary>
    /// How long to wait before the next check: when the last one is a full interval old, but never earlier than
    /// <see cref="StartupDelay"/> after the app started. One one-shot timer is armed with this; no polling.
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
