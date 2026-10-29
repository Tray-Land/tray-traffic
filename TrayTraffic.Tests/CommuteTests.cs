using System.Globalization;
using System.Text.Json;
using TrayTraffic.Core;

namespace TrayTraffic.Tests;

public class CommuteTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.Utc;
    private static readonly CommuteRoutine ArriveAtWork = CommuteRoutine.Defaults[0] with { Enabled = true };
    private static readonly CommuteRoutine LeaveWork = CommuteRoutine.Defaults[2] with { Enabled = true };

    // 2026-10-28 is a Wednesday.
    private static DateTimeOffset At(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void NextTarget_LaterToday_IsToday() =>
        Assert.Equal(At(28, 9), CommutePlanner.NextTarget(ArriveAtWork, At(28, 7), null, Zone));

    [Fact]
    public void NextTarget_AfterFridayArrival_SkipsTheWeekend() =>
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero),
            CommutePlanner.NextTarget(ArriveAtWork, At(30, 9, 30), null, Zone));

    [Fact]
    public void NextTarget_AlreadyRemindedToday_MovesToTheNextDay() =>
        Assert.Equal(At(29, 9), CommutePlanner.NextTarget(ArriveAtWork, At(28, 8), new DateOnly(2026, 10, 28), Zone));

    [Theory]
    [InlineData(5, 28)]
    [InlineData(11, 29)]
    public void NextTarget_LeaveAt_StillRemindsShortlyAfterTheLeavingTime(int minutesLate, int expectedDay) =>
        Assert.Equal(At(expectedDay, 17), CommutePlanner.NextTarget(LeaveWork, At(28, 17, minutesLate), null, Zone));

    private static UpcomingTrip? Upcoming(DateTimeOffset now, params CommuteRoutine[] routines) =>
        CommutePlanner.Upcoming(routines, now, _ => CommutePlanner.DefaultTravel, TimeSpan.FromMinutes(10), Zone);

    [Theory]
    [InlineData(7, 0, "arrive-work", false)]
    [InlineData(7, 50, "arrive-work", false)]
    [InlineData(7, 55, "arrive-work", true)]
    [InlineData(12, 0, "leave-work", false)]
    [InlineData(16, 30, "leave-work", false)]
    [InlineData(16, 40, "leave-work", true)]
    [InlineData(17, 30, "leave-work", true)]
    [InlineData(17, 31, "arrive-work", false)]
    public void Upcoming_PicksTheNextTripAndWhetherItsWindowIsOpen(int hour, int minute, string id, bool inWindow)
    {
        UpcomingTrip? trip = Upcoming(At(28, hour, minute), LeaveWork, ArriveAtWork);
        Assert.Equal(id, trip?.Routine.Id);
        Assert.Equal(inWindow, trip?.InWindow);
    }

    [Fact]
    public void Upcoming_StaysOnATripUntilItsArrivalEvenAfterTheReminder() =>
        Assert.Equal("arrive-work", Upcoming(At(28, 8, 30), ArriveAtWork, LeaveWork)?.Routine.Id);

    [Fact]
    public void Upcoming_NoEnabledRoutines_IsNull() =>
        Assert.Null(Upcoming(At(28, 7), ArriveAtWork with { Enabled = false }));

    [Fact]
    public void NextTarget_OffOrNoDays_NeverRuns()
    {
        Assert.Null(CommutePlanner.NextTarget(ArriveAtWork with { Enabled = false }, At(28, 7), null, Zone));
        Assert.Null(CommutePlanner.NextTarget(ArriveAtWork with { Days = WeekDays.None }, At(28, 7), null, Zone));
    }

    [Fact]
    public void ArriveBy_LeavesTravelTimeEarlyAndWatchesBeforeReminding()
    {
        DateTimeOffset leaveBy = CommutePlanner.LeaveBy(ArriveAtWork, At(28, 9), TimeSpan.FromMinutes(40));
        Assert.Equal(At(28, 8, 20), leaveBy);
        Assert.Equal(At(28, 7, 50), CommutePlanner.FirstCheck(ArriveAtWork, leaveBy));
        Assert.Equal(At(28, 17), CommutePlanner.LeaveBy(LeaveWork, At(28, 17), TimeSpan.FromMinutes(40)));
        Assert.Equal(At(28, 17), CommutePlanner.FirstCheck(LeaveWork, At(28, 17)));
    }

    [Theory]
    [InlineData(2, 3)]
    [InlineData(10, 5)]
    [InlineData(90, 15)]
    public void NextCheckInterval_TightensAsTheReminderNears(int untilReminder, int expected) =>
        Assert.Equal(TimeSpan.FromMinutes(expected), CommutePlanner.NextCheckInterval(TimeSpan.FromMinutes(untilReminder)));

    [Fact]
    public void Compose_OnTime_SaysWhenToLeaveAndIncludesDelays()
    {
        using CultureScope culture = new();
        CommuteReminder reminder = CommutePlanner.Compose(ArriveAtWork, "Work", At(28, 9), At(28, 8, 10), Route(40, 6), TimeSpan.Zero);
        Assert.Equal("Leave for Work in 10 min", reminder.Title);
        Assert.Equal("40 min with traffic, including 6 min of delays. Leave by 08:20 to arrive by 09:00.", reminder.Message);
    }

    [Fact]
    public void Compose_AlreadyLate_SaysLeaveNowAndWhenYoullArrive()
    {
        using CultureScope culture = new();
        CommuteReminder reminder = CommutePlanner.Compose(ArriveAtWork, "Work", At(28, 9), At(28, 8, 30), Route(40, 0), TimeSpan.Zero);
        Assert.Equal("Time to leave for Work", reminder.Title);
        Assert.Equal("40 min with current traffic. Leaving now gets you there about 09:10, after 09:00.", reminder.Message);
    }

    [Fact]
    public void Compose_WithoutLiveTraffic_FallsBackToTheUsualTime()
    {
        using CultureScope culture = new();
        CommuteReminder reminder = CommutePlanner.Compose(ArriveAtWork, "Work", At(28, 9), At(28, 8, 20), null, TimeSpan.FromMinutes(35));
        Assert.Equal("Leave for Work in 5 min", reminder.Title);
        Assert.StartsWith("Live traffic is unavailable; it usually takes 35 min. Leave by 08:25", reminder.Message);
    }

    [Fact]
    public void Compose_LeaveAt_SaysWhenYoullGetThere()
    {
        using CultureScope culture = new();
        CommuteReminder reminder = CommutePlanner.Compose(LeaveWork, "Home", At(28, 17), At(28, 16, 55), Route(30, 0), TimeSpan.Zero);
        Assert.Equal("Leave for Home in 5 min", reminder.Title);
        Assert.Equal("30 min with current traffic, arriving about 17:30.", reminder.Message);
    }

    [Fact]
    public void Compose_PartialMinuteArrival_RoundsUpLikeTheDuration()
    {
        using CultureScope culture = new();
        TrafficRoute route = Route(40, 0) with { TravelSeconds = 39 * 60 + 30 };
        CommuteReminder reminder = CommutePlanner.Compose(LeaveWork, "Home", At(28, 17), At(28, 16, 55), route, TimeSpan.Zero);
        Assert.Equal("40 min with current traffic, arriving about 17:40.", reminder.Message);
    }

    [Fact]
    public void Routines_RoundTripAndRejectImpossibleTrips()
    {
        CommuteRoutine[] routines = CommuteRoutine.Defaults;
        string json = JsonSerializer.Serialize(routines, TrafficJsonContext.Default.CommuteRoutineArray);
        Assert.Equal(routines, JsonSerializer.Deserialize(json, TrafficJsonContext.Default.CommuteRoutineArray));
        Assert.All(routines, routine => Assert.True(routine.IsValid));
        Assert.False((ArriveAtWork with { To = PlaceKind.Home }).IsValid);
        Assert.False((ArriveAtWork with { To = PlaceKind.Other }).IsValid);
        Assert.False((ArriveAtWork with { Days = (WeekDays)512 }).IsValid);
    }

    private static TrafficRoute Route(int minutes, int delayMinutes) =>
        new(minutes * 60, delayMinutes * 60, 30_000, DateTimeOffset.MinValue, [new(0, 0), new(1, 1)]);

    /// <summary>Formats times as 24-hour "HH:mm" for the duration of a test.</summary>
    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _previous = CultureInfo.CurrentCulture;

        public CultureScope() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        public void Dispose() => CultureInfo.CurrentCulture = _previous;
    }
}
