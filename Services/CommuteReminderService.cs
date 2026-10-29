using Microsoft.UI.Dispatching;
using TrayTraffic.Core;

namespace TrayTraffic.Services;

/// <summary>
/// Leave-time reminders for the user's commute routines.
/// </summary>
/// <remarks>
/// This is the app's one deliberate exception to "no network work while hidden": a reminder only
/// helps if it arrives before the user opens anything. It stays cheap and bounded. Each routine is
/// opt-in and runs only on its chosen days. Between reminders the service sleeps on a single timer
/// and makes no requests. Arrive-by routines check live traffic from about half an hour before the
/// reminder, a handful of times as it nears; leave-at routines check once. After reminding, a
/// routine is done for that day. Pausing from the tray menu stops everything.
/// </remarks>
internal sealed class CommuteReminderService
{
    // Re-plan at least this often, so a long sleep or a clock change can't make a reminder very late.
    private static readonly TimeSpan MaxSleep = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan FreshEnough = TimeSpan.FromMinutes(1);

    private readonly DispatcherQueueTimer _timer;
    private readonly Dictionary<string, Check> _checks = [];
    private CancellationTokenSource? _work;
    private bool _running;
    private bool _rerun;
    private bool _stopped;

    /// <summary>The last live traffic check for a routine's upcoming occurrence.</summary>
    private sealed record Check(DateTimeOffset Target, DateTimeOffset CheckedAt, DateTimeOffset NextCheck, TrafficRoute? Route);

    public CommuteReminderService(DispatcherQueue dispatcher)
    {
        _timer = dispatcher.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => _ = RunAsync();
    }

    /// <summary>Plans again now, e.g. after routines, places, or the key changed.</summary>
    public void Refresh()
    {
        _stopped = false;
        _ = RunAsync();
    }

    public void Stop()
    {
        _stopped = true;
        _timer.Stop();
        _work?.Cancel();
    }

    private async Task RunAsync()
    {
        if (_running)
        {
            _rerun = true;
            return;
        }

        _running = true;
        _timer.Stop();
        using CancellationTokenSource work = new();
        _work = work;
        DateTimeOffset? wake = null;
        try
        {
            wake = await PlanAsync(work.Token);
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LogService.Warning("Planning commute reminders", exception);
            wake = DateTimeOffset.Now + CommutePlanner.MinCheckInterval;
        }
        finally
        {
            _work = null;
            _running = false;
        }

        if (_stopped)
        {
            return;
        }
        if (_rerun)
        {
            _rerun = false;
            _ = RunAsync();
        }
        else if (wake is { } next)
        {
            TimeSpan delay = next - DateTimeOffset.Now;
            _timer.Interval = delay < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay > MaxSleep ? MaxSleep : delay;
            _timer.Start();
        }
    }

    /// <summary>Checks and reminds whatever is due, and returns when to look again (null: nothing to do).</summary>
    private async Task<DateTimeOffset?> PlanAsync(CancellationToken cancellationToken)
    {
        CommuteRoutine[] routines = SettingsService.RemindersPaused ? [] : SettingsService.Routines.Where(routine => routine.Enabled).ToArray();
        if (routines.Length == 0)
        {
            _checks.Clear();
            return null;
        }

        SavedPlace[] places = SettingsService.Places;
        TimeSpan lead = TimeSpan.FromMinutes(SettingsService.ReminderLeadMinutes);
        DateTimeOffset wake = DateTimeOffset.Now + MaxSleep;
        string? key = null;
        foreach (CommuteRoutine routine in routines)
        {
            SavedPlace? from = places.FirstOrDefault(place => place.Kind == routine.From);
            SavedPlace? to = places.FirstOrDefault(place => place.Kind == routine.To);
            DateTimeOffset now = DateTimeOffset.Now;
            if (from is null || to is null ||
                CommutePlanner.NextTarget(routine, now, SettingsService.RemindedOn(routine.Id), TimeZoneInfo.Local) is not { } target)
            {
                _checks.Remove(routine.Id);
                continue;
            }

            Check? check = _checks.TryGetValue(routine.Id, out Check? previous) && previous.Target == target ? previous : null;
            TimeSpan usual = SettingsService.UsualTravel(routine.Id);
            DateTimeOffset remindAt = RemindAt(routine, target, check, usual, lead);
            if (now < CommutePlanner.FirstCheck(routine, remindAt))
            {
                wake = Earliest(wake, CommutePlanner.FirstCheck(routine, remindAt));
                continue;
            }

            bool due = now >= remindAt;
            if (check is null || now >= check.NextCheck || (due && now - check.CheckedAt > FreshEnough))
            {
                key ??= await TrafficServices.ReadKeyAsync(cancellationToken);
                TrafficRoute? route = key is null ? null : await TryRouteAsync(from, to, key, cancellationToken);
                if (route is not null)
                {
                    SettingsService.SetUsualTravel(routine.Id, route.TravelSeconds);
                }
                now = DateTimeOffset.Now;
                check = new(target, now, now, route ?? check?.Route);
                remindAt = RemindAt(routine, target, check, usual, lead);
                check = check with { NextCheck = now + CommutePlanner.NextCheckInterval(remindAt - now) };
                _checks[routine.Id] = check;
                due = now >= remindAt;
            }

            if (due)
            {
                CommuteReminder reminder = CommutePlanner.Compose(routine, to.Label, target, now, check.Route, usual);
                NotificationService.ShowReminder(routine, reminder, target + CommutePlanner.LateGrace);
                SettingsService.SetRemindedOn(routine.Id, DateOnly.FromDateTime(target.DateTime));
                _checks.Remove(routine.Id);
                // Plan the routine's next occurrence on the following pass.
                wake = Earliest(wake, now);
            }
            else
            {
                wake = Earliest(wake, Earliest(remindAt, check.NextCheck));
            }
        }
        return wake;
    }

    private static DateTimeOffset RemindAt(CommuteRoutine routine, DateTimeOffset target, Check? check, TimeSpan usual, TimeSpan lead)
    {
        TimeSpan travel = check?.Route is { } route ? TimeSpan.FromSeconds(route.TravelSeconds) : usual;
        return CommutePlanner.LeaveBy(routine, target, travel) - lead;
    }

    private static async Task<TrafficRoute?> TryRouteAsync(SavedPlace from, SavedPlace to, string key, CancellationToken cancellationToken)
    {
        try
        {
            TrafficRoute[] routes = await TrafficServices.Client.RoutesAsync(
                from.Location, to.Location, SettingsService.Preferences, key, cancellationToken);
            return routes[0];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Remind anyway with the usual travel time rather than staying silent.
            LogService.Warning("Checking commute traffic", exception);
            return null;
        }
    }

    private static DateTimeOffset Earliest(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;
}
