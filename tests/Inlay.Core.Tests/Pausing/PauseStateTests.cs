using System.Globalization;
using Inlay.Core.Pausing;

namespace Inlay.Core.Tests.Pausing;

public sealed class PauseStateTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 19, 15, 30, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo s_utc = TimeZoneInfo.Utc;
    private static readonly CultureInfo s_british = CultureInfo.GetCultureInfo("en-GB");
    private static readonly CultureInfo s_american = CultureInfo.GetCultureInfo("en-US");

    [Fact]
    public void A_timed_pause_ends_at_a_wall_clock_time()
    {
        var pause = new PauseState();
        pause.Pause(s_now, PauseState.FifteenMinutes);

        Assert.True(pause.IsPaused);
        Assert.Equal(s_now.AddMinutes(15), pause.Until);
        Assert.Equal(TimeSpan.FromMinutes(15), pause.DueIn(s_now));
        Assert.Equal(TimeSpan.FromMinutes(5), pause.DueIn(s_now.AddMinutes(10)));
    }

    [Fact]
    public void Until_I_resume_has_no_end_and_no_timer()
    {
        var pause = new PauseState();
        pause.Pause(s_now, null);

        Assert.True(pause.IsPaused);
        Assert.Null(pause.Until);
        Assert.Null(pause.DueIn(s_now));
        Assert.False(pause.Expire(s_now.AddDays(3)));
        Assert.True(pause.IsPaused);
    }

    [Fact]
    public void Status_and_resume_text_show_the_end_in_the_short_time_format()
    {
        var pause = new PauseState();
        pause.Pause(s_now, PauseState.OneHour);

        Assert.Equal("Paused until 16:30", pause.Status(s_utc, s_british));
        Assert.Equal("Resume (paused until 16:30)", pause.ResumeText(s_utc, s_british));
        Assert.Equal("Paused until 4:30 PM", pause.Status(s_utc, s_american).Replace(' ', ' '));

        pause.Pause(s_now, null);
        Assert.Equal("Paused", pause.Status(s_utc, s_british));
        Assert.Equal("Resume", pause.ResumeText(s_utc, s_british));
    }

    [Fact]
    public void The_end_is_shown_in_the_users_time_zone()
    {
        var pause = new PauseState();
        pause.Pause(s_now, PauseState.FifteenMinutes);
        var plusTwo = TimeZoneInfo.CreateCustomTimeZone("UTC+2", TimeSpan.FromHours(2), "UTC+2", "UTC+2");

        Assert.Equal("Paused until 17:45", pause.Status(plusTwo, s_british));
    }

    [Fact]
    public void A_pause_that_ran_out_during_sleep_ends_on_wake()
    {
        var pause = new PauseState();
        pause.Pause(s_now, PauseState.FifteenMinutes);

        // Woken after five minutes: still paused, the timer is re-armed for the other ten.
        Assert.False(pause.Expire(s_now.AddMinutes(5)));
        Assert.Equal(TimeSpan.FromMinutes(10), pause.DueIn(s_now.AddMinutes(5)));

        // Woken after two hours: the pause is over.
        Assert.Equal(TimeSpan.Zero, pause.DueIn(s_now.AddHours(2)));
        Assert.True(pause.Expire(s_now.AddHours(2)));
        Assert.False(pause.IsPaused);
        Assert.Null(pause.Until);
        Assert.Null(pause.DueIn(s_now.AddHours(2)));
    }

    [Fact]
    public void A_new_choice_replaces_the_pause_and_resume_clears_it()
    {
        var pause = new PauseState();
        pause.Pause(s_now, PauseState.FifteenMinutes);
        pause.Pause(s_now, PauseState.OneHour);
        Assert.Equal(s_now.AddHours(1), pause.Until);

        pause.Resume();
        Assert.False(pause.IsPaused);
        Assert.Null(pause.Until);
        Assert.False(pause.Expire(s_now.AddHours(2)));
    }
}
