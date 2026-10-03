using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TrayTraffic.Controls;
using TrayTraffic.Core;
using TrayTraffic.Services;
using Windows.System;

namespace TrayTraffic.Views;

public sealed partial class FlyoutPage : Page, IDisposable
{
    private readonly ForegroundPoller _poller;
    private CancellationTokenSource? _activation;
    private RouteSnapshot? _snapshot;
    private TrafficMap? _map;
    private SavedPlace? _origin;
    private SavedPlace? _destination;
    private string? _key;
    private int _revision = -1;
    private bool _loadingPlaces;
    private bool _visible;
    private bool _disposed;
    private bool _mapValidated;

    public FlyoutPage()
    {
        InitializeComponent();
        RouteToggle.Checked += RouteToggle_Changed;
        RouteToggle.Unchecked += RouteToggle_Changed;
        _poller = new(DispatcherQueue, RefreshPolicy.Interval, RefreshAsync, RefreshPolicy.Interval);
        _poller.Failed += (_, exception) => ShowError(exception);
    }

    public static Thickness StatusMargin(bool isOpen) => new(0, isOpen ? 8 : 0, 0, 0);

    public void OnShown()
    {
        _visible = true;
        SettingsService.Changed += OnSettingsChanged;
        _ = ActivateAsync();
    }

    public void OnHidden()
    {
        _visible = false;
        SettingsService.Changed -= OnSettingsChanged;
        _activation?.Cancel();
        _poller.Stop();
        _map?.SetActive(false);
        LoadingRing.IsActive = false;
    }

    public void Dispose()
    {
        _disposed = true;
        OnHidden();
        _map?.Dispose();
        MapHost.Content = null;
        _map = null;
        _key = null;
        _snapshot = null;
    }

    private async Task ActivateAsync()
    {
        _activation?.Cancel();
        CancellationTokenSource source = new();
        _activation = source;
        LoadingRing.IsActive = true;
        try
        {
            bool changed = _revision != SettingsService.Revision;
            if (changed)
            {
                _revision = SettingsService.Revision;
                ClearResult();
                _poller.Invalidate();
            }
            if (changed || _origin is null || _destination is null)
            {
                PopulatePlaces();
            }
            string? key = await TrafficServices.ReadKeyAsync(source.Token);
            source.Token.ThrowIfCancellationRequested();
            if (!_visible || _disposed)
            {
                return;
            }
            if (_key != key)
            {
                _key = key;
                ClearResult();
                _poller.Invalidate();
            }
            if (key is null)
            {
                ShowEmpty("Connect to live traffic",
                    "Add a free TomTom API key in Settings, then search and save your everyday places.");
                return;
            }
            if (_origin is null || _destination is null)
            {
                ShowEmpty("Where do you go?",
                    "Save at least two places in Settings. Home, work, and school are all optional.");
                return;
            }

            RefreshButton.IsEnabled = true;
            RefreshButton.Visibility = Visibility.Visible;
            if (_snapshot is not null)
            {
                _map?.SetActive(_mapValidated);
            }
            else
            {
                EmptyTitle.Text = "Finding the quickest route";
                EmptyDescription.Text = "Comparing driving routes with current traffic...";
            }
            _poller.Start();
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LogService.Warning("Opening the traffic view", exception);
            if (_visible && !_disposed)
            {
                ShowError(exception);
            }
        }
        finally
        {
            if (ReferenceEquals(_activation, source))
            {
                _activation = null;
                if (!_poller.IsRunning)
                {
                    LoadingRing.IsActive = false;
                }
            }
            source.Dispose();
        }
    }

    private void PopulatePlaces(PlaceKind? originKind = null, PlaceKind? destinationKind = null)
    {
        SavedPlace[] places = SettingsService.Places;
        _loadingPlaces = true;
        PlaceKind origin = originKind ?? SettingsService.Origin;
        PlaceKind destination = destinationKind ?? SettingsService.Destination;
        _origin = places.FirstOrDefault(place => place.Kind == origin) ?? places.FirstOrDefault();
        _destination = places.FirstOrDefault(place => place.Kind == destination && place != _origin)
            ?? places.FirstOrDefault(place => place != _origin);
        FromPicker.Items.Clear();
        ToPicker.Items.Clear();
        foreach (SavedPlace place in places)
        {
            ComboBoxItem from = new() { Content = place.Label, Tag = place.Kind };
            FromPicker.Items.Add(from);
            if (place == _origin)
            {
                FromPicker.SelectedItem = from;
                continue;
            }
            ComboBoxItem to = new() { Content = place.Label, Tag = place.Kind };
            ToPicker.Items.Add(to);
            if (place == _destination)
            {
                ToPicker.SelectedItem = to;
            }
        }
        FromPicker.IsEnabled = places.Length >= 2;
        ToPicker.IsEnabled = places.Length >= 2;
        SwapButton.IsEnabled = places.Length >= 2;
        _loadingPlaces = false;
        UpdateRouteControls();
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        SavedPlace origin = _origin ?? throw new InvalidOperationException("A starting place is required.");
        SavedPlace destination = _destination ?? throw new InvalidOperationException("A destination is required.");
        string key = _key ?? throw new InvalidOperationException("A traffic key is required.");
        RoutePreferences preferences = SettingsService.Preferences;
        LoadingRing.IsActive = true;
        try
        {
            TrafficRoute[] routes = await TrafficServices.Client.RoutesAsync(
                origin.Location, destination.Location, preferences, key, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            bool fit = _snapshot is null;
            _snapshot = new(origin, destination, preferences, routes, DateTimeOffset.Now);
            StatusBar.IsOpen = false;
            _map ??= new TrafficMap(key, origin.Location);
            MapHost.Content = _map;
            _map.RefreshTraffic();
            if (!_mapValidated)
            {
                try
                {
                    await TrafficServices.Client.CheckMapAccessAsync(origin.Location, key, ActualTheme == ElementTheme.Dark, cancellationToken);
                    _mapValidated = true;
                    MapNotice.Visibility = Visibility.Collapsed;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    LogService.Warning("Checking map and traffic access", exception);
                    MapNotice.Text = $"Map unavailable. {TrafficServices.ErrorMessage(exception)}";
                    MapNotice.Visibility = Visibility.Visible;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            _map.SetActive(_visible && _mapValidated);
            RenderRoutes(fit);
            UpdatedText.Text = $"Updated {_snapshot.UpdatedAt:t} | {TrafficFormatting.PreferenceDescription(preferences)}";
            App.Current.SetTrayStatus($"{origin.Label} to {destination.Label}: {TrafficFormatting.Duration(routes[0].TravelSeconds)} (updated {_snapshot.UpdatedAt:t})");
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
    }

    private void RenderRoutes(bool fit)
    {
        if (_snapshot is not { } snapshot)
        {
            return;
        }
        EmptyState.Visibility = Visibility.Collapsed;
        RouteItem[] items = snapshot.Routes.Select((route, index) =>
            new RouteItem(route, index, snapshot.Routes[0].TravelSeconds, SettingsService.UseKilometers)).ToArray();
        RoutesList.ItemsSource = items;
        RoutesList.SelectedIndex = 0;
        UpdateRouteControls();
        if (fit && RouteToggle.IsChecked == true)
        {
            _ = DrawSelectionAsync(true);
        }
    }

    private async Task DrawSelectionAsync(bool fit)
    {
        if (_snapshot is not { } snapshot || RoutesList.SelectedIndex < 0)
        {
            return;
        }
        int index = RoutesList.SelectedIndex;
        TrafficRoute route = snapshot.Routes[index];
        string recommendation = index == 0 ? "Fastest now" :
            route.TravelSeconds == snapshot.Routes[0].TravelSeconds ? "Same travel time" :
            $"{TrafficFormatting.Duration(route.TravelSeconds - snapshot.Routes[0].TravelSeconds)} slower";
        RecommendationText.Text = $"{recommendation} | Arrive {route.Arrival.ToLocalTime():t}";
        try
        {
            if (_map is not null)
            {
                await _map.ShowRouteAsync(snapshot, index, fit);
            }
        }
        catch (Exception exception)
        {
            LogService.Warning("Drawing driving routes", exception);
            MapNotice.Text = "The route map couldn't be drawn. Traffic estimates are still available.";
            MapNotice.Visibility = Visibility.Visible;
        }
    }

    private void UpdateRouteControls()
    {
        bool showRoutes = RouteToggle.IsChecked == true;
        TripPicker.Visibility = showRoutes && _origin is not null && _destination is not null
            ? Visibility.Visible : Visibility.Collapsed;
        Visibility routeVisibility = showRoutes && _snapshot is not null
            ? Visibility.Visible : Visibility.Collapsed;
        RouteHeading.Visibility = routeVisibility;
        RoutesList.Visibility = routeVisibility;
        _map?.SetRoutesVisible(showRoutes);
    }

    private void ClearResult()
    {
        _snapshot = null;
        _mapValidated = false;
        _map?.Dispose();
        _map = null;
        MapHost.Content = null;
        RoutesList.ItemsSource = null;
        RoutesList.Visibility = Visibility.Collapsed;
        RouteHeading.Visibility = Visibility.Collapsed;
        MapNotice.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
        StatusBar.IsOpen = false;
        RefreshButton.IsEnabled = false;
        RefreshButton.Visibility = Visibility.Collapsed;
        UpdatedText.Text = "Live traffic by TomTom";
        App.Current.SetTrayStatus(null);
    }

    private void ShowEmpty(string title, string description)
    {
        _poller.Stop();
        RefreshButton.IsEnabled = false;
        RefreshButton.Visibility = Visibility.Collapsed;
        EmptyTitle.Text = title;
        EmptyDescription.Text = description;
    }

    private void ShowError(Exception exception)
    {
        StatusBar.Title = "Traffic couldn't update";
        StatusBar.Message = TrafficServices.ErrorMessage(exception) +
            (_snapshot is null ? "" : " Showing the last estimate.");
        StatusBar.IsOpen = true;
        if (_snapshot is null)
        {
            EmptyTitle.Text = "No route yet";
            EmptyDescription.Text = "Check your key and saved places, or refresh to try again.";
        }
        App.Current.SetTrayStatus(_snapshot is null ? "Traffic unavailable" : $"Traffic unavailable | Last update {_snapshot.UpdatedAt:t}");
    }

    private void Place_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loadingPlaces || !_visible || FromPicker.SelectedItem is not ComboBoxItem { Tag: PlaceKind origin })
        {
            return;
        }
        PlaceKind destination = ToPicker.SelectedItem is ComboBoxItem { Tag: PlaceKind selected }
            ? selected : SettingsService.Destination;
        _poller.Stop();
        _activation?.Cancel();
        PopulatePlaces(origin, destination);
        ClearResult();
        PersistSelection();
        _poller.Invalidate();
        _ = ActivateAsync();
    }

    private void PersistSelection()
    {
        try
        {
            if (_origin is not null && _destination is not null)
            {
                SettingsService.Origin = _origin.Kind;
                SettingsService.Destination = _destination.Kind;
            }
        }
        catch (Exception exception)
        {
            LogService.Warning("Saving the selected trip", exception);
            StatusBar.Title = "Trip selection couldn't be saved";
            StatusBar.Message = "You can still check this trip. Choose it again next time.";
            StatusBar.IsOpen = true;
        }
    }

    private void SwapButton_Click(object sender, RoutedEventArgs args)
    {
        if (_origin is null || _destination is null)
        {
            return;
        }
        _poller.Stop();
        _activation?.Cancel();
        PopulatePlaces(_destination.Kind, _origin.Kind);
        ClearResult();
        PersistSelection();
        _poller.Invalidate();
        _ = ActivateAsync();
    }

    private void OnSettingsChanged(object? sender, EventArgs args)
    {
        if (_visible)
        {
            _poller.Stop();
            _ = ActivateAsync();
        }
    }

    private void RouteToggle_Changed(object sender, RoutedEventArgs args)
    {
        UpdateRouteControls();
        if (RouteToggle.IsChecked == true)
        {
            _ = DrawSelectionAsync(true);
        }
    }

    private void RoutesList_SelectionChanged(object sender, SelectionChangedEventArgs args) => _ = DrawSelectionAsync(false);
    private void RefreshButton_Click(object sender, RoutedEventArgs args) => _ = _poller.RefreshNowAsync();
    private void SettingsButton_Click(object sender, RoutedEventArgs args) => App.Current.ShowSettings();

    private async void DirectionsButton_Click(object sender, RoutedEventArgs args)
    {
        if (_snapshot is not { } snapshot)
        {
            return;
        }
        string Point(GeoPoint point) => string.Create(CultureInfo.InvariantCulture, $"{point.Latitude},{point.Longitude}");
        Uri uri = new("https://www.google.com/maps/dir/?api=1&travelmode=driving" +
            $"&origin={Uri.EscapeDataString(Point(snapshot.Origin.Location))}&destination={Uri.EscapeDataString(Point(snapshot.Destination.Location))}");
        try
        {
            if (!await Launcher.LaunchUriAsync(uri))
            {
                throw new InvalidOperationException("No browser accepted the directions link.");
            }
        }
        catch (Exception exception)
        {
            LogService.Warning("Opening directions", exception);
            StatusBar.Title = "Couldn't open directions";
            StatusBar.Message = "Check that a default browser is installed.";
            StatusBar.IsOpen = true;
        }
    }
}

public sealed class RouteItem
{
    public RouteItem(TrafficRoute route, int index, double fastestSeconds, bool kilometers)
    {
        Title = index == 0 ? "Fastest route" : route.TravelSeconds == fastestSeconds ? "Also fastest" : $"Alternative {index}";
        Duration = TrafficFormatting.Duration(route.TravelSeconds);
        Detail = $"{TrafficFormatting.Distance(route.DistanceMeters, kilometers)} | {TrafficFormatting.Delay(route.DelaySeconds)}";
    }

    public string Title { get; }
    public string Duration { get; }
    public string Detail { get; }
    public string AccessibleName => $"{Title}, {Duration}, {Detail}";
}
