using System.Globalization;

namespace TrayTraffic.Core;

public static class TrafficFormatting
{
    public static string Duration(double seconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(seconds);
        if (!double.IsFinite(seconds) || seconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds));
        }
        if (seconds < 60)
        {
            return "<1 min";
        }

        int minutes = (int)Math.Ceiling(seconds / 60.0);
        return minutes < 60
            ? $"{minutes} min"
            : minutes % 60 == 0 ? $"{minutes / 60} hr" : $"{minutes / 60} hr {minutes % 60} min";
    }

    public static string Delay(double seconds) => seconds == 0
        ? "No traffic delay"
        : $"{Duration(seconds)} traffic delay";

    public static string Distance(double meters, bool kilometers)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(meters);
        if (!double.IsFinite(meters))
        {
            throw new ArgumentOutOfRangeException(nameof(meters));
        }
        double distance = kilometers ? meters / 1000.0 : meters / 1609.344;
        return $"{distance.ToString("0.0", CultureInfo.CurrentCulture)} {(kilometers ? "km" : "mi")}";
    }

    public static string PreferenceDescription(RoutePreferences preferences) =>
        (preferences.AvoidTolls, preferences.AvoidFerries) switch
        {
            (true, true) => "Avoiding tolls and ferries",
            (true, false) => "Avoiding tolls",
            (false, true) => "Avoiding ferries",
            _ => "Driving with live traffic",
        };
}

public static class RefreshPolicy
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MaximumInterval = TimeSpan.FromMinutes(10);

    public static TimeSpan NextInterval(TimeSpan configured, int consecutiveFailures)
    {
        if (configured <= TimeSpan.Zero || configured > MaximumInterval)
        {
            throw new ArgumentOutOfRangeException(nameof(configured));
        }

        double ticks = configured.Ticks * Math.Pow(2, Math.Clamp(consecutiveFailures, 0, 10));
        return TimeSpan.FromTicks((long)Math.Min(ticks, MaximumInterval.Ticks));
    }
}
