using Comet.Core.Settings;

namespace Comet.Core.Emoji;

/// <summary>When the app may check for new emoji data (the timing is <see cref="Updates.UpdateSchedule"/>).</summary>
public static class EmojiUpdatePolicy
{
    /// <summary>Checks only while Emoji is on, the app isn't paused and the user hasn't turned updates off.</summary>
    public static bool Enabled(AppSettings settings, bool paused) =>
        settings.Features.Emoji && settings.UpdateEmojiAutomatically && !paused;
}
