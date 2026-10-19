using Inlay.Core.Updates;

namespace Inlay.Core.Tests.Updates;

public sealed class UpdateScheduleTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan s_hour = TimeSpan.FromHours(1);

    [Fact]
    public void Every_launch_checks_thirty_seconds_after_start_however_recent_the_last_check()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), UpdateSchedule.NextCheckIn(s_now, lastChecked: null, sinceStart: TimeSpan.Zero, checkedThisLaunch: false));
        Assert.Equal(TimeSpan.FromSeconds(30), UpdateSchedule.NextCheckIn(s_now, s_now - TimeSpan.FromMinutes(5), TimeSpan.Zero, checkedThisLaunch: false));
        Assert.Equal(TimeSpan.FromSeconds(20), UpdateSchedule.NextCheckIn(s_now, s_now - s_hour, TimeSpan.FromSeconds(10), checkedThisLaunch: false));
        // Turned on (or resumed) long after start: at once.
        Assert.Equal(TimeSpan.Zero, UpdateSchedule.NextCheckIn(s_now, s_now - s_hour, TimeSpan.FromHours(3), checkedThisLaunch: false));
    }

    [Fact]
    public void After_the_launch_check_once_a_day()
    {
        Assert.Equal(TimeSpan.FromDays(1) - s_hour, UpdateSchedule.NextCheckIn(s_now, s_now - s_hour, s_hour, checkedThisLaunch: true));
        Assert.Equal(TimeSpan.FromDays(1), UpdateSchedule.NextCheckIn(s_now, s_now, s_hour, checkedThisLaunch: true));
    }

    [Fact]
    public void A_day_that_passed_asleep_counts()
    {
        // The wall clock moved 30 hours since the last check while the timer slept: check now.
        Assert.Equal(TimeSpan.Zero, UpdateSchedule.NextCheckIn(s_now, s_now - TimeSpan.FromHours(30), TimeSpan.FromHours(2), checkedThisLaunch: true));
    }

    [Fact]
    public void A_last_check_in_the_future_does_not_postpone_beyond_one_interval()
    {
        Assert.Equal(TimeSpan.FromDays(1), UpdateSchedule.NextCheckIn(s_now, s_now + TimeSpan.FromDays(365), s_hour, checkedThisLaunch: true));
    }
}
