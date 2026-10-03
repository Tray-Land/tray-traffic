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
    private static readonly Thickness RouteMargin = new(32, 44, 32, 32);
    private readonly string _key;
    private readonly MapElementsLayer _routes = new() { IsVisible = false };
    private readonly TileLayer _basemap;
    private TileLayer _traffic;
    private RouteSnapshot? _snapshot;
    private int _selected;
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

    public async Task ShowRouteAsync(RouteSnapshot snapshot, int selected, bool fit)
    {
        _snapshot = snapshot;
        _selected = selected;
        DrawRoutes();
        if (fit)
        {
            await FitRouteAsync();
        }
    }

    public void RefreshTraffic()
    {
        Map.Layers.Remove(_traffic);
        _traffic = CreateTiles(MapTiles.TrafficTemplate(IsDark), "tomtom-traffic");
        Map.Layers.Insert(1, _traffic);
        SetActive(_active);
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
        if (_snapshot is not { } snapshot)
        {
            return;
        }

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
        AddPin(snapshot.Origin, accent);
        AddPin(snapshot.Destination, accent);
    }

    private void AddPin(SavedPlace place, Color color)
    {
        FontIcon visual = new()
        {
            Glyph = place.Kind switch
            {
                PlaceKind.Home => "\uE80F",
                PlaceKind.Work => "\uE821",
                _ => "\uE7BE",
            },
            FontSize = 20,
            Foreground = new SolidColorBrush(color),
        };
        _routes.MapElements.Add(new MapIcon(visual, new Geopoint(Position(place.Location)))
        {
            NormalizedAnchorPoint = new Point(0.5, 0.5),
            ZIndex = 3,
        });
    }

    private async Task FitRouteAsync()
    {
        if (_disposed || !_routes.IsVisible || _snapshot is null)
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

        if (_disposed || !_routes.IsVisible || _snapshot is not { } snapshot)
        {
            return;
        }
        if (GeoboundingBox.TryCompute(snapshot.Routes[_selected].Path.Select(Position)) is not { } bounds)
        {
            throw new InvalidOperationException("The selected route has no valid map bounds.");
        }
        if (!await Map.TrySetViewBoundsAsync(bounds, RouteMargin, MapAnimationKind.None) && !_disposed)
        {
            throw new InvalidOperationException("The selected route's map view could not be applied.");
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
            await FitRouteAsync();
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
