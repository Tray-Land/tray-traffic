using System.Text.Json.Serialization;

namespace TrayTraffic.Core;

internal sealed record RoutingRequest(
    RoutePlanningLocations RoutePlanningLocations,
    string[]? Avoids)
{
    public string DepartureDateTime => "now";
    public string RouteType => "fast";
    public string Traffic => "live";
    public string TravelMode => "car";
    public int MaxPathAlternativeRoutes => 2;
}

internal sealed record RoutePlanningLocations(GeoJsonPoint Origin, GeoJsonPoint Destination);

internal sealed record GeoJsonPoint(double[] Coordinates)
{
    public string Type => "Point";

    public static GeoJsonPoint From(GeoPoint point) => new([point.Longitude, point.Latitude]);
}

internal sealed class GeocodingResponse
{
    public GeocodingResult[]? Results { get; set; }
}

internal sealed class GeocodingResult
{
    public string? Title { get; set; }
    public GeoJsonGeometry? Position { get; set; }
    public AccessPoint[]? AccessPoints { get; set; }
}

internal sealed class AccessPoint
{
    public GeoJsonGeometry? Position { get; set; }
}

internal sealed class GeoJsonGeometry
{
    public string? Type { get; set; }
    public double[]? Coordinates { get; set; }
}

internal sealed class RoutingResponse
{
    public RouteResponse[]? Routes { get; set; }
}

internal sealed class RouteResponse
{
    public RouteSummary? Summary { get; set; }
    public RouteLeg[]? Legs { get; set; }
}

internal sealed class RouteSummary
{
    public double? LengthInMeters { get; set; }
    public double? TravelDurationInSeconds { get; set; }
    public double? TrafficDelayDurationInSeconds { get; set; }
    public DateTimeOffset? ArrivalDateTime { get; set; }
}

internal sealed class RouteLeg
{
    public GeoJsonPath? Path { get; set; }
}

internal sealed class GeoJsonPath
{
    public string? Type { get; set; }
    public double[][]? Coordinates { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RoutingRequest))]
[JsonSerializable(typeof(RoutingResponse))]
[JsonSerializable(typeof(GeocodingResponse))]
internal partial class TomTomJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SavedPlace[]))]
public partial class TrafficJsonContext : JsonSerializerContext;
