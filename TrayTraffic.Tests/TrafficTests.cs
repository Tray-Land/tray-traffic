using System.Net;
using System.Text;
using System.Text.Json;
using TrayTraffic.Core;

namespace TrayTraffic.Tests;

public class TrafficTests
{
    private const string Key = "test-key-not-a-real-credential";
    private static readonly GeoPoint Origin = new(47.60, -122.33);
    private static readonly GeoPoint Destination = new(47.65, -122.30);

    [Theory]
    [InlineData(0, "<1 min")]
    [InlineData(59, "<1 min")]
    [InlineData(60, "1 min")]
    [InlineData(61, "2 min")]
    [InlineData(60.1, "2 min")]
    [InlineData(3599, "1 hr")]
    [InlineData(3601, "1 hr 1 min")]
    public void Duration_RoundsUp_WithoutUnderestimating(double seconds, string expected) =>
        Assert.Equal(expected, TrafficFormatting.Duration(seconds));

    [Fact]
    public void Delay_Zero_ReportsNoDelay() =>
        Assert.Equal("No traffic delay", TrafficFormatting.Delay(0));

    [Theory]
    [InlineData(-91, 0)]
    [InlineData(0, 181)]
    [InlineData(double.NaN, 0)]
    [InlineData(0, double.PositiveInfinity)]
    public void GeoPoint_InvalidCoordinate_IsRejected(double latitude, double longitude) =>
        Assert.False(new GeoPoint(latitude, longitude).IsValid);

    [Theory]
    [InlineData(0, 120)]
    [InlineData(1, 240)]
    [InlineData(2, 480)]
    [InlineData(3, 600)]
    [InlineData(100, 600)]
    public void RefreshBackoff_FailureCount_IsCapped(int failures, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), RefreshPolicy.NextInterval(RefreshPolicy.Interval, failures));

    [Fact]
    public void MapTiles_PolarAndWorldEdges_StayInsideTileGrid()
    {
        Assert.Equal((0, 0), MapTiles.Coordinate(new(90, -180), 0));
        Assert.Equal((4095, 4095), MapTiles.Coordinate(new(-90, 180), 12));
        Assert.DoesNotContain("key=", MapTiles.BasemapTemplate(false), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("key=", MapTiles.TrafficTemplate(true), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{", MapTiles.At(MapTiles.TrafficTemplate(false), Origin));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has a space")]
    [InlineData("newline\n")]
    public void Key_InvalidInput_Throws(string key) =>
        Assert.Throws<ArgumentException>(() => TomTomClient.ValidateKey(key));

    [Fact]
    public async Task Search_UsesOrbisV2AndRoadEntrance()
    {
        using StubHandler handler = new(_ => Json("""
            {"results":[{"title":"Example address","position":{"type":"Point","coordinates":[-122.33,47.60]},
            "accessPoints":[{"position":{"type":"Point","coordinates":[-122.331,47.601]}}]}]}
            """));
        using HttpClient http = new(handler);
        LocationCandidate[] results = await new TomTomClient(http).SearchAsync("1600 Main & Second", Key, default);
        Assert.Equal(new GeoPoint(47.601, -122.331), Assert.Single(results).Location);
        Assert.Equal("2", handler.Version);
        Assert.Equal(Key, handler.Key);
        Assert.Contains("query=1600%20Main%20%26%20Second", handler.Uri!.OriginalString);
        Assert.DoesNotContain(Key, handler.Uri.OriginalString);
    }

    [Fact]
    public async Task Search_NoResults_ReturnsEmptyList()
    {
        using StubHandler handler = new(_ => Json("""{"results":[]}"""));
        using HttpClient http = new(handler);
        Assert.Empty(await new TomTomClient(http).SearchAsync("Unknown address", Key, default));
    }

    [Fact]
    public async Task Routes_RequestUsesLiveTrafficAndGeoJsonCoordinateOrder()
    {
        using StubHandler handler = new(_ => Json(RouteResponse(600, 900)));
        using HttpClient http = new(handler);
        await new TomTomClient(http).RoutesAsync(Origin, Destination, new(true, true), Key, default);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("3", handler.Version);
        Assert.Equal("routes.summary,routes.legs.path", handler.Attributes);
        Assert.DoesNotContain(Key, handler.Uri!.OriginalString);
        using JsonDocument body = JsonDocument.Parse(handler.Body!);
        JsonElement root = body.RootElement;
        Assert.Equal("fast", root.GetProperty("routeType").GetString());
        Assert.Equal("live", root.GetProperty("traffic").GetString());
        Assert.Equal("now", root.GetProperty("departureDateTime").GetString());
        Assert.Equal(2, root.GetProperty("maxPathAlternativeRoutes").GetInt32());
        Assert.Equal("car", root.GetProperty("travelMode").GetString());
        Assert.Equal(-122.33, root.GetProperty("routePlanningLocations").GetProperty("origin")
            .GetProperty("coordinates")[0].GetDouble());
        Assert.Equal(new[] { "tollRoads", "ferries" },
            root.GetProperty("avoids").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task Routes_NoAvoidance_DoesNotSendEmptyAvoidsArray()
    {
        using StubHandler handler = new(_ => Json(RouteResponse(600)));
        using HttpClient http = new(handler);
        await new TomTomClient(http).RoutesAsync(Origin, Destination, new(), Key, default);
        using JsonDocument body = JsonDocument.Parse(handler.Body!);
        Assert.False(body.RootElement.TryGetProperty("avoids", out _));
    }

    [Fact]
    public async Task Routes_UnsortedAlternatives_RecommendLowestTrafficTravelTime()
    {
        using StubHandler handler = new(_ => Json(RouteResponse(1200, 900, 1000)));
        using HttpClient http = new(handler);
        TrafficRoute[] routes = await new TomTomClient(http).RoutesAsync(Origin, Destination, new(), Key, default);
        Assert.Equal(new double[] { 900, 1000, 1200 }, routes.Select(route => route.TravelSeconds));
        Assert.Equal(180, routes[0].DelaySeconds);
        Assert.Equal(Origin, routes[0].Path[0]);
        Assert.Equal(Destination, routes[0].Path[^1]);
    }

    [Theory]
    [InlineData("""{"routes":[]}""")]
    [InlineData("""{"routes":[{"summary":{"travelDurationInSeconds":500}}]}""")]
    [InlineData("""{"routes":[null]}""")]
    [InlineData("not json")]
    public async Task Routes_NoRouteOrIncompleteData_IsNotShownAsSuccess(string json)
    {
        using StubHandler handler = new(_ => Json(json));
        using HttpClient http = new(handler);
        await Assert.ThrowsAsync<TrafficServiceException>(() =>
            new TomTomClient(http).RoutesAsync(Origin, Destination, new(), Key, default));
    }

    [Fact]
    public async Task Routes_FractionalDurations_AreAcceptedWithoutRoundingDown()
    {
        string json = RouteResponse(900).Replace("\"travelDurationInSeconds\":900", "\"travelDurationInSeconds\":900.5");
        using StubHandler handler = new(_ => Json(json));
        using HttpClient http = new(handler);
        TrafficRoute route = Assert.Single(await new TomTomClient(http).RoutesAsync(Origin, Destination, new(), Key, default));
        Assert.Equal(900.5, route.TravelSeconds);
        Assert.Equal("16 min", TrafficFormatting.Duration(route.TravelSeconds));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "rejected the key")]
    [InlineData(HttpStatusCode.TooManyRequests, "request limit")]
    [InlineData(HttpStatusCode.BadRequest, "could not process")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "unavailable")]
    public async Task Service_HttpFailure_HasActionableNonSecretMessage(HttpStatusCode status, string message)
    {
        using StubHandler handler = new(_ => new(status) { Content = new StringContent("sensitive server details") });
        using HttpClient http = new(handler);
        TrafficServiceException exception = await Assert.ThrowsAsync<TrafficServiceException>(() =>
            new TomTomClient(http).RoutesAsync(Origin, Destination, new(), Key, default));
        Assert.Equal((int)status, exception.StatusCode);
        Assert.Contains(message, exception.Message);
        Assert.DoesNotContain(Key, exception.Message);
        Assert.DoesNotContain("sensitive server details", exception.Message);
    }

    [Fact]
    public async Task Routes_Cancellation_StopsTheRequest()
    {
        using BlockingHandler handler = new();
        using HttpClient http = new(handler);
        using CancellationTokenSource source = new();
        Task<TrafficRoute[]> pending = new TomTomClient(http).RoutesAsync(Origin, Destination, new(), Key, source.Token);
        await handler.Started.Task;
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public void SavedPlaces_SourceGeneratedRoundTrip_PreservesCoordinatesAndKind()
    {
        SavedPlace[] places = [new(PlaceKind.Home, "An example address", Origin)];
        string json = JsonSerializer.Serialize(places, TrafficJsonContext.Default.SavedPlaceArray);
        SavedPlace[] restored = JsonSerializer.Deserialize(json, TrafficJsonContext.Default.SavedPlaceArray)!;
        Assert.Equal(places, restored);
        Assert.True(restored[0].IsValid);
        Assert.False(new SavedPlace((PlaceKind)100, "Invalid", Origin).IsValid);
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string RouteResponse(params int[] travelSeconds)
    {
        string[] routes = travelSeconds.Select(seconds => $$$"""
            {"summary":{"lengthInMeters":12000,"travelDurationInSeconds":{{{seconds}}},
            "trafficDelayDurationInSeconds":180,"arrivalDateTime":"2026-10-02T20:30:00-07:00"},
            "legs":[{"path":{"type":"LineString","coordinates":[[-122.33,47.60],[-122.30,47.65]]}}]}
            """).ToArray();
        return "{\"routes\":[" + string.Join(",", routes) + "],\"futureField\":\"ignored\"}";
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? Body { get; private set; }
        public string? Key { get; private set; }
        public string? Version { get; private set; }
        public string? Attributes { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Method = request.Method;
            Key = request.Headers.GetValues("TomTom-Api-Key").Single();
            Version = request.Headers.GetValues("TomTom-Api-Version").Single();
            Attributes = request.Headers.TryGetValues("Attributes", out IEnumerable<string>? attributes) ? attributes.Single() : null;
            Body = request.Content is not null ? await request.Content.ReadAsStringAsync(cancellationToken) : null;
            return response(request);
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }
    }
}
