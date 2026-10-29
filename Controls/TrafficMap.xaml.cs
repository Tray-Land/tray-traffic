using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TrayTraffic.Core;
using TrayTraffic.Services;
using Windows.Devices.Geolocation;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinUIEx.Maps;
using MapAnimationKind = WinUIEx.Maps.MapAnimationKind;
using MapElementsLayer = WinUIEx.Maps.MapElementsLayer;
using MapIcon = WinUIEx.Maps.MapIcon;
using MapPolyline = WinUIEx.Maps.MapPolyline;

namespace TrayTraffic.Controls;

public sealed partial class TrafficMap : UserControl, IDisposable
{
    // Keeps pins (26 px emoji) clear of the overlay buttons at the top and the attribution at the bottom.
    private static readonly Thickness RouteMargin = new(32, 60, 32, 40);
    private static readonly TimeSpan TrafficMaxAge = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan TrafficCrossFade = TimeSpan.FromSeconds(2);
    private readonly string _key;
    private readonly MapElementsLayer _routes = new() { IsVisible = false };
    private readonly TileLayer _basemap;
    private TileLayer _traffic;
    private DateTimeOffset _trafficLoadedAt = DateTimeOffset.UtcNow;
    private int _trafficGeneration;
    private (SavedPlace Origin, SavedPlace Destination)? _trip;
    private RouteSnapshot? _snapshot;
    private int _selected;
    private int _fitRequest;
    private bool _hasFitted;
    private bool _active;
    private bool _disposed;
    private TaskCompletionSource? _viewportReady;

    public TrafficMap(string key, GeoPoint startingPlace)
    {
        _key = key;
        InitializeComponent();
        Map.Center = new Geopoint(Position(startingPlace));
        Map.ZoomLevel = 12;
        _basemap = CreateTiles(MapTiles.BasemapTemplate(IsDark), "tomtom-basemap");
        _basemap.Attribution = "\u00A9 TomTom";
        _basemap.AttributionLink = new Uri("https://www.tomtom.com/legal/");
        _routes.Attribution = "\u00A9 OpenStreetMap contributors";
        _routes.AttributionLink = new Uri("https://www.openstreetmap.org/copyright");
        _traffic = CreateTiles(MapTiles.TrafficTemplate(IsDark), "tomtom-traffic");
        Map.Layers.Add(_basemap);
        Map.Layers.Add(_traffic);
        Map.Layers.Add(_routes);
        ActualThemeChanged += OnThemeChanged;
    }

    private bool IsDark => ActualTheme == ElementTheme.Dark;

    public void SetActive(bool active)
    {
        _active = active;
        _basemap.IsVisible = active;
        _traffic.IsVisible = active && TrafficToggle.IsChecked == true;
        Map.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetRoutesVisible(bool visible)
    {
        _routes.IsVisible = visible;
        FitRouteButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Shows a trip whose routes are still loading: the old routes go, the two places are pinned,
    /// and the camera glides to frame them.
    /// </summary>
    public async Task ShowTripAsync(SavedPlace origin, SavedPlace destination)
    {
        _snapshot = null;
        _trip = (origin, destination);
        DrawRoutes();
        await FitAsync();
    }

    public async Task ShowRouteAsync(RouteSnapshot snapshot, int selected, bool fit)
    {
        _snapshot = snapshot;
        _trip = (snapshot.Origin, snapshot.Destination);
        _selected = selected;
        DrawRoutes();
        if (fit)
        {
            await FitAsync();
        }
    }

    public void RefreshTraffic()
    {
        // Flow tiles change about once a minute, so a trip switch right after a refresh reuses them.
        if (DateTimeOffset.UtcNow - _trafficLoadedAt < TrafficMaxAge)
        {
            return;
        }

        // Fade the new overlay in above the old one before dropping it, so traffic never blinks out.
        TileLayer previous = _traffic;
        _traffic = CreateTiles(MapTiles.TrafficTemplate(IsDark), $"tomtom-traffic-{++_trafficGeneration}");
        Map.Layers.Insert(Map.Layers.IndexOf(previous) + 1, _traffic);
        _trafficLoadedAt = DateTimeOffset.UtcNow;
        SetActive(_active);
        _ = RetireLayerAsync(previous);
    }

    private async Task RetireLayerAsync(TileLayer layer)
    {
        await Task.Delay(TrafficCrossFade);
        Map.Layers.Remove(layer);
    }

    public void Dispose()
    {
        _disposed = true;
        SetActive(false);
        ActualThemeChanged -= OnThemeChanged;
        Map.LayoutUpdated -= OnMapLayoutUpdated;
        _viewportReady?.TrySetCanceled();
        _viewportReady = null;
        _routes.MapElements.Clear();
        Map.Layers.Clear();
        _snapshot = null;
        _trip = null;
    }

    private TileLayer CreateTiles(string url, string id) => new(new TileLayerOptions
    {
        TileUrl = url,
        TileSize = 256,
        MinSourceZoom = 0,
        MaxSourceZoom = 22,
        RequestHeaders = new Dictionary<string, string>
        {
            ["TomTom-Api-Key"] = _key,
            ["TomTom-Api-Version"] = "2",
        },
    }, id)
    {
        IsVisible = _active,
    };

    private void OnThemeChanged(FrameworkElement sender, object args)
    {
        if (_disposed)
        {
            return;
        }
        _basemap.TileUrl = MapTiles.BasemapTemplate(IsDark);
        _traffic.TileUrl = MapTiles.TrafficTemplate(IsDark);
        DrawRoutes();
    }

    private void DrawRoutes()
    {
        _routes.MapElements.Clear();
        if (_trip is not { } trip)
        {
            return;
        }

        if (_snapshot is { } snapshot)
        {
            Color accent = BrushColor("AccentFillColorDefaultBrush", UIColorType.Accent);
            Color secondary = BrushColor("TextFillColorSecondaryBrush", UIColorType.Foreground);
            Color outline = BrushColor("SolidBackgroundFillColorBaseBrush", UIColorType.Background);
            for (int index = 0; index < snapshot.Routes.Length; index++)
            {
                if (index == _selected)
                {
                    continue;
                }
                _routes.MapElements.Add(new MapPolyline
                {
                    Path = Path(snapshot.Routes[index]),
                    StrokeColor = secondary,
                    StrokeThickness = 3,
                    StrokeDashed = true,
                });
            }

            Geopath selectedPath = Path(snapshot.Routes[_selected]);
            _routes.MapElements.Add(new MapPolyline { Path = selectedPath, StrokeColor = outline, StrokeThickness = 8, ZIndex = 1 });
            _routes.MapElements.Add(new MapPolyline { Path = selectedPath, StrokeColor = accent, StrokeThickness = 5, ZIndex = 2 });
        }
        AddPin(trip.Origin);
        AddPin(trip.Destination);
    }

    private void AddPin(SavedPlace place)
    {
        // Color emoji read against both the light and dark basemaps and the accent route line.
        FontIcon visual = new()
        {
            FontFamily = new FontFamily("Segoe UI Emoji"),
            Glyph = place.Kind switch
            {
                PlaceKind.Home => "\U0001F3E0",
                PlaceKind.Work => "\U0001F3E2",
                PlaceKind.School => "\U0001F3EB",
                _ => "\U0001F4CD",
            },
            FontSize = 26,
        };
        _routes.MapElements.Add(new MapIcon(visual, new Geopoint(Position(place.Location)))
        {
            NormalizedAnchorPoint = new Point(0.5, 0.5),
            ZIndex = 3,
        });
    }

    /// <summary>Frames the selected route, or the trip's two places while its routes load.</summary>
    private async Task FitAsync()
    {
        int request = ++_fitRequest;
        if (_disposed || !_routes.IsVisible || _trip is null)
        {
            return;
        }

        try
        {
            // Route cards change the map's height; fit only after the final layout is arranged.
            do
            {
                await WaitForViewportAsync();
            }
            while (!_disposed && !HasViewport);
        }
        catch (OperationCanceledException) when (_disposed)
        {
            return;
        }

        if (_disposed || !_routes.IsVisible || request != _fitRequest || _trip is not { } trip)
        {
            return;
        }
        IEnumerable<GeoPoint> points = _snapshot is { } snapshot
            ? snapshot.Routes[_selected].Path
            : [trip.Origin.Location, trip.Destination.Location];
        if (GeoboundingBox.TryCompute(points.Select(Position)) is not { } bounds)
        {
            throw new InvalidOperationException("The trip has no valid map bounds.");
        }

        // Glide between views; the first fit lands directly because the map is fading in anyway.
        MapAnimationKind animation = _hasFitted && Motion.Enabled ? MapAnimationKind.Default : MapAnimationKind.None;
        _hasFitted = true;
        // A newer fit interrupts an animated one, which is not a failure.
        if (!await Map.TrySetViewBoundsAsync(bounds, RouteMargin, animation) && !_disposed && request == _fitRequest)
        {
            throw new InvalidOperationException("The trip's map view could not be applied.");
        }
    }

    private bool HasViewport => _active && Map.IsLoaded &&
        Map.ActualWidth > RouteMargin.Left + RouteMargin.Right &&
        Map.ActualHeight > RouteMargin.Top + RouteMargin.Bottom;

    private Task WaitForViewportAsync()
    {
        if (_viewportReady is null)
        {
            _viewportReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Map.LayoutUpdated += OnMapLayoutUpdated;
        }
        Map.InvalidateArrange();
        return _viewportReady.Task;
    }

    private void OnMapLayoutUpdated(object? sender, object args)
    {
        if (!HasViewport)
        {
            return;
        }
        Map.LayoutUpdated -= OnMapLayoutUpdated;
        TaskCompletionSource? ready = _viewportReady;
        _viewportReady = null;
        ready?.TrySetResult();
    }

    private async void FitRoute_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            await FitAsync();
        }
        catch (Exception exception)
        {
            LogService.Warning("Fitting the selected route", exception);
        }
    }

    private void TrafficToggle_Changed(object sender, RoutedEventArgs args)
    {
        if (_traffic is not null)
        {
            _traffic.IsVisible = _active && TrafficToggle.IsChecked == true;
        }
    }

    private static BasicGeoposition Position(GeoPoint point) => new() { Latitude = point.Latitude, Longitude = point.Longitude };
    private static Geopath Path(TrafficRoute route) => new(route.Path.Select(Position));
    private static Color BrushColor(string key, UIColorType fallback) =>
        Application.Current.Resources[key] is SolidColorBrush brush ? brush.Color : new UISettings().GetColorValue(fallback);
}
