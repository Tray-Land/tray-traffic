namespace TrayTraffic.Core;

public enum PlaceKind
{
    Home,
    Work,
    School,

    /// <summary>A place found by search rather than one of the saved everyday places.</summary>
    Other,
}

public readonly record struct GeoPoint(double Latitude, double Longitude)
{
    public bool IsValid =>
        double.IsFinite(Latitude) && Latitude is >= -90 and <= 90 &&
        double.IsFinite(Longitude) && Longitude is >= -180 and <= 180;
}

/// <param name="Name">The place's own name, for a place found by search; saved places are labeled by kind.</param>
public sealed record SavedPlace(PlaceKind Kind, string Address, GeoPoint Location, string? Name = null)
{
    public string Label => Name ?? Kind.ToString();

    public bool IsValid =>
        Enum.IsDefined(Kind) && !string.IsNullOrWhiteSpace(Address) &&
        Address.Length <= 512 && Location.IsValid &&
        (Name is null || (!string.IsNullOrWhiteSpace(Name) && Name.Length <= 200));
}

public sealed record LocationCandidate(string Address, GeoPoint Location);

/// <summary>A business, landmark, or address found by place search.</summary>
/// <param name="Detail">Address lines for display; may be empty.</param>
/// <param name="DistanceMeters">Distance from the search's reference point, when known.</param>
public sealed record PlaceCandidate(string Name, string Detail, GeoPoint Location, double? DistanceMeters);

public sealed record RoutePreferences(bool AvoidTolls = false, bool AvoidFerries = false);

/// <param name="Name">The route's main roads in driving order, such as "Elm St to I-90 W"; null when they aren't known.</param>
public sealed record TrafficRoute(
    double TravelSeconds,
    double DelaySeconds,
    double DistanceMeters,
    DateTimeOffset Arrival,
    GeoPoint[] Path,
    string? Name = null);

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
