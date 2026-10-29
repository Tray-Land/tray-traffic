using System.Globalization;

namespace TrayTraffic.Core;

public enum RoutineKind
{
    /// <summary>Be at the destination by the routine's time.</summary>
    ArriveBy,

    /// <summary>Set off from the starting place at the routine's time.</summary>
    LeaveAt,
}

[Flags]
public enum WeekDays
{
    None = 0,
    Sunday = 1 << (int)DayOfWeek.Sunday,
    Monday = 1 << (int)DayOfWeek.Monday,
    Tuesday = 1 << (int)DayOfWeek.Tuesday,
    Wednesday = 1 << (int)DayOfWeek.Wednesday,
    Thursday = 1 << (int)DayOfWeek.Thursday,
    Friday = 1 << (int)DayOfWeek.Friday,
    Saturday = 1 << (int)DayOfWeek.Saturday,
    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
    Every = Weekdays | Saturday | Sunday,
}

/// <summary>A regular trip the user wants a leave-time reminder for.</summary>
public sealed record CommuteRoutine(
    string Id,
    PlaceKind From,
    PlaceKind To,
    RoutineKind Kind,
    TimeOnly Time,
    WeekDays Days,
    bool Enabled)
{
    public const string ArriveAtWork = "arrive-work";
    public const string ArriveAtSchool = "arrive-school";
    public const string LeaveWork = "leave-work";

    /// <summary>The everyday patterns offered in Settings, all off until the user opts in.</summary>
    public static CommuteRoutine[] Defaults =>
    [
        new(ArriveAtWork, PlaceKind.Home, PlaceKind.Work, RoutineKind.ArriveBy, new(9, 0), WeekDays.Weekdays, false),
        new(ArriveAtSchool, PlaceKind.Home, PlaceKind.School, RoutineKind.ArriveBy, new(8, 0), WeekDays.Weekdays, false),
        new(LeaveWork, PlaceKind.Work, PlaceKind.Home, RoutineKind.LeaveAt, new(17, 0), WeekDays.Weekdays, false),
    ];

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Id) && Id.Length <= 40 &&
        Enum.IsDefined(From) && Enum.IsDefined(To) && From != To &&
        From != PlaceKind.Other && To != PlaceKind.Other &&
        Enum.IsDefined(Kind) && (Days & ~WeekDays.Every) == 0;

    public bool RunsOn(DayOfWeek day) => Days.HasFlag((WeekDays)(1 << (int)day));
}

/// <summary>The routine a user is most likely about to make, and whether its reminder window is open.</summary>
public sealed record UpcomingTrip(CommuteRoutine Routine, DateTimeOffset Target, DateTimeOffset RemindAt, bool InWindow);

/// <summary>The text of one leave-time notification.</summary>
public sealed record CommuteReminder(string Title, string Message);

/// <summary>
/// When to check traffic and when to remind for a routine. Arrive-by routines start watching
/// traffic shortly before the reminder is due, so a slow day moves the reminder earlier; checks
/// get closer together as it approaches. Leave-at routines only need one check, for the message.
/// </summary>
public static class CommutePlanner
{
    /// <summary>The travel time assumed before a routine's trip has ever been measured.</summary>
    public static readonly TimeSpan DefaultTravel = TimeSpan.FromMinutes(45);

    /// <summary>How long before an arrive-by reminder the first traffic check runs.</summary>
    public static readonly TimeSpan WatchAhead = TimeSpan.FromMinutes(30);

    public static readonly TimeSpan MinCheckInterval = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan MaxCheckInterval = TimeSpan.FromMinutes(15);

    /// <summary>A leave-at reminder still goes out this long after the leaving time, e.g. after the PC wakes.</summary>
    public static readonly TimeSpan LateGrace = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The next arrival or departure time for the routine that hasn't been reminded yet, or null
    /// if the routine is off or runs on no days.
    /// </summary>
    public static DateTimeOffset? NextTarget(CommuteRoutine routine, DateTimeOffset now, DateOnly? remindedOn, TimeZoneInfo zone)
    {
        if (!routine.Enabled || routine.Days == WeekDays.None)
        {
            return null;
        }

        DateOnly today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        TimeSpan grace = routine.Kind == RoutineKind.LeaveAt ? LateGrace : TimeSpan.Zero;
        for (int days = 0; days <= 7; days++)
        {
            DateOnly date = today.AddDays(days);
            if (!routine.RunsOn(date.DayOfWeek) || date <= remindedOn)
            {
                continue;
            }

            DateTime wallClock = date.ToDateTime(routine.Time);
            DateTimeOffset target = new(wallClock, zone.GetUtcOffset(wallClock));
            if (target + grace > now)
            {
                return target;
            }
        }
        return null;
    }

    /// <summary>
    /// The scheduled trip the user most likely cares about now: the enabled routine whose reminder
    /// is soonest, counting one already reminded today until its arrival or departure passes. A trip
    /// whose window is open wins over a sooner-looking one that isn't.
    /// <see cref="UpcomingTrip.InWindow"/> is true from two reminder widths (<paramref name="lead"/>)
    /// before the leave-by time to three after it: leaving at 5:00 with a 10 minute lead, 4:40 to 5:30.
    /// </summary>
    public static UpcomingTrip? Upcoming(
        IEnumerable<CommuteRoutine> routines,
        DateTimeOffset now,
        Func<CommuteRoutine, TimeSpan> usualTravel,
        TimeSpan lead,
        TimeZoneInfo zone)
    {
        TimeSpan before = lead * 2;
        TimeSpan after = lead * 3;
        UpcomingTrip? best = null;
        foreach (CommuteRoutine routine in routines)
        {
            // Look back far enough to find a leave-at trip that is past its time but still in its window.
            if (NextTarget(routine, now - after, null, zone) is not { } target)
            {
                continue;
            }
            bool leaveAt = routine.Kind == RoutineKind.LeaveAt;
            if (leaveAt ? target + (after > LateGrace ? after : LateGrace) < now : target <= now)
            {
                // The earlier occurrence is over; the one after it is the next target.
                if (NextTarget(routine, now, null, zone) is not { } next)
                {
                    continue;
                }
                target = next;
            }
            DateTimeOffset leaveBy = LeaveBy(routine, target, usualTravel(routine));
            bool inWindow = now >= leaveBy - before && now <= leaveBy + after;
            if (best is null || (inWindow, best.InWindow) is (true, false) ||
                (inWindow == best.InWindow && leaveBy - lead < best.RemindAt))
            {
                best = new(routine, target, leaveBy - lead, inWindow);
            }
        }
        return best;
    }

    public static DateTimeOffset LeaveBy(CommuteRoutine routine, DateTimeOffset target, TimeSpan travel) =>
        routine.Kind == RoutineKind.ArriveBy ? target - travel : target;

    public static DateTimeOffset FirstCheck(CommuteRoutine routine, DateTimeOffset remindAt) =>
        routine.Kind == RoutineKind.ArriveBy ? remindAt - WatchAhead : remindAt;

    /// <summary>Half the time left before the reminder, kept between 3 and 15 minutes.</summary>
    public static TimeSpan NextCheckInterval(TimeSpan untilReminder) =>
        TimeSpan.FromTicks(Math.Clamp(untilReminder.Ticks / 2, MinCheckInterval.Ticks, MaxCheckInterval.Ticks));

    /// <param name="route">The fastest route right now, or null when live traffic couldn't be checked.</param>
    /// <param name="usualTravel">The last measured travel time, used when <paramref name="route"/> is null.</param>
    public static CommuteReminder Compose(
        CommuteRoutine routine,
        string toLabel,
        DateTimeOffset target,
        DateTimeOffset now,
        TrafficRoute? route,
        TimeSpan usualTravel)
    {
        TimeSpan travel = route is null ? usualTravel : TimeSpan.FromSeconds(route.TravelSeconds);
        DateTimeOffset leaveBy = LeaveBy(routine, target, travel);
        int minutes = (int)Math.Ceiling((leaveBy - now).TotalMinutes);
        string title = minutes <= 1 ? $"Time to leave for {toLabel}" : $"Leave for {toLabel} in {minutes} min";

        string drive = TrafficFormatting.Duration(travel.TotalSeconds);
        string traffic = route is null
            ? $"Live traffic is unavailable; it usually takes {drive}"
            : route.DelaySeconds >= 60
                ? $"{drive} with traffic, including {TrafficFormatting.Duration(route.DelaySeconds)} of delays"
                : $"{drive} with current traffic";
        string message = routine.Kind switch
        {
            RoutineKind.ArriveBy when leaveBy < now - TimeSpan.FromMinutes(1) =>
                $"{traffic}. Leaving now gets you there about {Arrival(now + travel)}, after {Time(target)}.",
            RoutineKind.ArriveBy => $"{traffic}. Leave by {Time(leaveBy)} to arrive by {Time(target)}.",
            _ => $"{traffic}, arriving about {Arrival(Max(leaveBy, now) + travel)}.",
        };
        return new(title, message);
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left > right ? left : right;

    // Leave-by times truncate to the minute (earlier is safe); arrivals round up, like durations do.
    private static string Time(DateTimeOffset time) => time.ToString("t", CultureInfo.CurrentCulture);

    private static string Arrival(DateTimeOffset time) =>
        Time(time.Second > 0 || time.Millisecond > 0 ? time.AddTicks(TimeSpan.TicksPerMinute - time.Ticks % TimeSpan.TicksPerMinute) : time);
}
