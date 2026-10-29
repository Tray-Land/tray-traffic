using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using TrayTraffic.Core;

namespace TrayTraffic.Services;

/// <summary>
/// Windows notifications for leave-time reminders. Each routine has its own tag, so an update
/// replaces that routine's reminder instead of piling up in the notification center.
/// </summary>
internal static class NotificationService
{
    private const string Group = "commute";
    private const string FromKey = "from";
    private const string ToKey = "to";

    private static bool s_registered;

    /// <summary>Raised on a background thread when a reminder is clicked, with the trip to show.</summary>
    public static event EventHandler<(PlaceKind From, PlaceKind To)>? TripRequested;

    /// <summary>Whether Windows will show the app's notifications; false when the user or policy turned them off.</summary>
    public static bool IsEnabled => s_registered && AppNotificationManager.Default.Setting == AppNotificationSetting.Enabled;

    public static void Register()
    {
        try
        {
            AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
            AppNotificationManager.Default.Register();
            s_registered = true;
        }
        catch (Exception exception)
        {
            // Reminders are a nice-to-have; everything else works without them.
            LogService.Warning("Registering for notifications", exception);
        }
    }

    public static void Unregister()
    {
        if (s_registered)
        {
            AppNotificationManager.Default.Unregister();
            s_registered = false;
        }
    }

    /// <summary>Reads the trip from a notification that launched the app.</summary>
    public static (PlaceKind From, PlaceKind To)? ReadTrip(IDictionary<string, string> arguments) =>
        arguments.TryGetValue(FromKey, out string? from) && Enum.TryParse(from, out PlaceKind origin) && Enum.IsDefined(origin) &&
        arguments.TryGetValue(ToKey, out string? to) && Enum.TryParse(to, out PlaceKind destination) && Enum.IsDefined(destination)
            ? (origin, destination)
            : null;

    /// <param name="expires">When the reminder stops being useful, so it leaves the notification center.</param>
    public static void ShowReminder(CommuteRoutine routine, CommuteReminder reminder, DateTimeOffset expires)
    {
        if (!s_registered)
        {
            return;
        }

        AppNotification notification = new AppNotificationBuilder()
            .AddArgument(FromKey, routine.From.ToString())
            .AddArgument(ToKey, routine.To.ToString())
            .AddText(reminder.Title)
            .AddText(reminder.Message)
            .AddButton(new AppNotificationButton("Show route")
                .AddArgument(FromKey, routine.From.ToString())
                .AddArgument(ToKey, routine.To.ToString()))
            .BuildNotification();
        notification.Tag = routine.Id;
        notification.Group = Group;
        notification.Expiration = expires;
        notification.ExpiresOnReboot = true;
        AppNotificationManager.Default.Show(notification);
    }

    private static void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        if (ReadTrip(args.Arguments) is { } trip)
        {
            TripRequested?.Invoke(null, trip);
        }
    }
}
