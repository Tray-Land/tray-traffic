namespace TrayTraffic.Core;

public enum PlaceKind
{
    Home,
    Work,
    School,
}

public readonly record struct GeoPoint(double Latitude, double Longitude)
{
    public bool IsValid =>
        double.IsFinite(Latitude) && Latitude is >= -90 and <= 90 &&
        double.IsFinite(Longitude) && Longitude is >= -180 and <= 180;
}

public sealed record SavedPlace(PlaceKind Kind, string Address, GeoPoint Location)
{
    public string Label => Kind.ToString();

    public bool IsValid =>
        Enum.IsDefined(Kind) && !string.IsNullOrWhiteSpace(Address) &&
        Address.Length <= 512 && Location.IsValid;
}

public sealed record LocationCandidate(string Address, GeoPoint Location);

public sealed record RoutePreferences(bool AvoidTolls = false, bool AvoidFerries = false);

public sealed record TrafficRoute(
    double TravelSeconds,
    double DelaySeconds,
    double DistanceMeters,
    DateTimeOffset Arrival,
    GeoPoint[] Path);

public sealed record RouteSnapshot(
    SavedPlace Origin,
    SavedPlace Destination,
    RoutePreferences Preferences,
    TrafficRoute[] Routes,
    DateTimeOffset UpdatedAt);

public sealed class TrafficServiceException(string message, int? statusCode = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
}
