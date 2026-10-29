using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace TrayTraffic.Core;

public sealed class TomTomClient(HttpClient http)
{
    private const string Host = "https://api.tomtom.com";

    // Only the instruction fields that name roads; the rest of guidance would triple the response.
    private const string RouteAttributes =
        "routes(summary,legs(path),instructions(routeOffsetInMeters," +
        "nextRoadInformation(properties,roadNames(identifier(text),source),roadNumbers(identifier(text),source))))";

    public static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 256 ||
            key.Any(character => character is < '!' or > '~'))
        {
            throw new ArgumentException("Enter a TomTom API key without spaces (up to 256 characters).", nameof(key));
        }
    }

    public async Task<LocationCandidate[]> SearchAsync(string query, string key, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        query = query.Trim();
        if (query.Length is < 3 or > 256)
        {
            throw new ArgumentException("Enter a street address, city, or intersection (3 to 256 characters).", nameof(query));
        }

        using HttpRequestMessage request = CreateRequest(HttpMethod.Get,
            $"{Host}/maps/orbis/places/geocode?query={Uri.EscapeDataString(query)}&maxResults=5", key, "2");
        request.Headers.Add("Attributes", "results(title,position,accessPoints)");
        GeocodingResponse response = await ReadAsync(request, TomTomJsonContext.Default.GeocodingResponse, cancellationToken)
            .ConfigureAwait(false);
        if (response.Results is null)
        {
            throw InvalidResponse();
        }

        return response.Results.Select(result =>
        {
            if (result is null || string.IsNullOrWhiteSpace(result.Title) || result.Title.Length > 512)
            {
                throw InvalidResponse();
            }

            // Prefer the road entrance over a building centroid for driving directions.
            GeoJsonGeometry? position = result.AccessPoints?.FirstOrDefault()?.Position ?? result.Position;
            if (position?.Type != "Point" || position.Coordinates is not { Length: 2 } coordinates)
            {
                throw InvalidResponse();
            }
            return new LocationCandidate(result.Title, ReadPoint(coordinates));
        }).DistinctBy(candidate => candidate.Address).ToArray();
    }

    public async Task<TrafficRoute[]> RoutesAsync(
        GeoPoint origin,
        GeoPoint destination,
        RoutePreferences preferences,
        string key,
        CancellationToken cancellationToken)
    {
        ValidateKey(key);
        if (!origin.IsValid || !destination.IsValid)
        {
            throw new ArgumentException("Both places must have valid coordinates.");
        }

        List<string> avoids = [];
        if (preferences.AvoidTolls)
        {
            avoids.Add("tollRoads");
        }
        if (preferences.AvoidFerries)
        {
            avoids.Add("ferries");
        }

        RoutingRequest body = new(new(GeoJsonPoint.From(origin), GeoJsonPoint.From(destination)),
            avoids.Count > 0 ? [.. avoids] : null);
        using HttpRequestMessage request = CreateRequest(HttpMethod.Post,
            $"{Host}/maps/orbis/routing/routes/calculate", key, "3");
        request.Headers.Add("Attributes", RouteAttributes);
        request.Content = JsonContent.Create(body, TomTomJsonContext.Default.RoutingRequest);

        RoutingResponse response = await ReadAsync(request, TomTomJsonContext.Default.RoutingResponse, cancellationToken)
            .ConfigureAwait(false);
        if (response.Routes is not { Length: > 0 } routes)
        {
            throw new TrafficServiceException("No driving route was found between these places.");
        }

        // Named together, after choosing the three shown, so their names tell them apart.
        (TrafficRoute Route, RouteRoad[] Roads)[] shown = [.. routes.Select(ReadRoute)
            .OrderBy(read => read.Route.TravelSeconds).ThenBy(read => read.Route.DistanceMeters).Take(3)];
        string?[] names = RouteNaming.NameRoutes([.. shown.Select(read => (read.Roads, read.Route.DistanceMeters))]);
        return [.. shown.Select((read, index) => read.Route with { Name = names[index] })];
    }

    public async Task CheckMapAccessAsync(GeoPoint location, string key, bool dark, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        await Task.WhenAll(
            CheckTileAsync(MapTiles.At(MapTiles.BasemapTemplate(dark), location), key, cancellationToken),
            CheckTileAsync(MapTiles.At(MapTiles.TrafficTemplate(dark), location), key, cancellationToken))
            .ConfigureAwait(false);
    }

    private async Task CheckTileAsync(string url, string key, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, url, key, "2");
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd("image/png");
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        CheckStatus(response.StatusCode);
        if (response.Content.Headers.ContentType?.MediaType is not ("image/png" or "image/jpeg"))
        {
            throw new TrafficServiceException("TomTom did not return a map tile. Check map and traffic access for your key.");
        }
    }

    private async Task<T> ReadAsync<T>(HttpRequestMessage request, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        CheckStatus(response.StatusCode);
        try
        {
            return await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken).ConfigureAwait(false)
                ?? throw InvalidResponse();
        }
        catch (JsonException)
        {
            throw InvalidResponse();
        }
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string key, string version)
    {
        HttpRequestMessage request = new(method, url);
        request.Headers.Add("TomTom-Api-Key", key);
        request.Headers.Add("TomTom-Api-Version", version);
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    private static void CheckStatus(HttpStatusCode code)
    {
        if ((int)code is >= 200 and < 300)
        {
            return;
        }

        string message = code switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "TomTom rejected the key. Check that it has access to Orbis routing, geocoding, maps, and traffic.",
            HttpStatusCode.TooManyRequests =>
                "TomTom's request limit has been reached. Retrying less often; check your account quota.",
            HttpStatusCode.BadRequest =>
                "TomTom could not process this request. Check the addresses and driving preferences.",
            HttpStatusCode.NotFound =>
                "TomTom could not find the requested service or route.",
            _ => "TomTom is unavailable right now. Your last good result will stay visible.",
        };
        throw new TrafficServiceException(message, (int)code);
    }

    private static (TrafficRoute Route, RouteRoad[] Roads) ReadRoute(RouteResponse? route)
    {
        if (route?.Summary is not
            {
                TravelDurationInSeconds: >= 0,
                TrafficDelayDurationInSeconds: >= 0,
                LengthInMeters: >= 0,
                ArrivalDateTime: not null,
            } summary || route.Legs is not { Length: > 0 } legs)
        {
            throw InvalidResponse();
        }
        if (!double.IsFinite(summary.TravelDurationInSeconds.Value) ||
            summary.TravelDurationInSeconds > int.MaxValue ||
            !double.IsFinite(summary.TrafficDelayDurationInSeconds.Value) ||
            summary.TrafficDelayDurationInSeconds > int.MaxValue ||
            !double.IsFinite(summary.LengthInMeters.Value))
        {
            throw InvalidResponse();
        }

        List<GeoPoint> path = [];
        foreach (RouteLeg leg in legs)
        {
            if (leg?.Path?.Type != "LineString" || leg.Path.Coordinates is not { Length: >= 2 } coordinates)
            {
                throw InvalidResponse();
            }

            foreach (double[] coordinate in coordinates)
            {
                GeoPoint point = ReadPoint(coordinate);
                if (path.Count == 0 || path[^1] != point)
                {
                    path.Add(point);
                }
            }
        }
        if (path.Count < 2)
        {
            throw InvalidResponse();
        }

        return (new(summary.TravelDurationInSeconds.Value, summary.TrafficDelayDurationInSeconds.Value,
            summary.LengthInMeters.Value, summary.ArrivalDateTime.Value, [.. path]), ReadRoads(route.Instructions));
    }

    /// <summary>
    /// Reads the road each instruction turns onto. Names only label the route, so an instruction
    /// without a usable offset is skipped rather than failing the refresh.
    /// </summary>
    private static RouteRoad[] ReadRoads(RouteInstruction?[]? instructions) =>
        [.. (instructions ?? []).Select(instruction =>
        {
            if (instruction?.RouteOffsetInMeters is not { } offset || !double.IsFinite(offset) || offset < 0)
            {
                return null;
            }

            RoadInformation? road = instruction.NextRoadInformation;
            return new RouteRoad(offset,
                RoadTexts(road?.RoadNames, "road").FirstOrDefault(),
                RoadTexts(road?.RoadNumbers, "signpost"),
                road?.Properties?.Any(property => property is "motorway" or "controlledAccess") == true);
        }).OfType<RouteRoad>()];

    private static string[] RoadTexts(RoadIdentifier?[]? identifiers, string preferredSource) =>
        [.. (identifiers ?? []).OfType<RoadIdentifier>()
            .OrderBy(identifier => identifier.Source == preferredSource ? 0 : 1)
            .Select(identifier => identifier.Identifier?.Text?.Trim())
            .OfType<string>()
            .Where(text => text.Length is > 0 and <= 100)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    private static GeoPoint ReadPoint(double[]? coordinates)
    {
        if (coordinates is not { Length: 2 })
        {
            throw InvalidResponse();
        }

        GeoPoint point = new(coordinates[1], coordinates[0]);
        return point.IsValid ? point : throw InvalidResponse();
    }

    private static TrafficServiceException InvalidResponse() =>
        new("TomTom returned incomplete or unexpected data. Try refreshing again.");
}
