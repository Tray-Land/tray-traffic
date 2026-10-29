using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using TrayTraffic.Controls;
using TrayTraffic.Core;
using TrayTraffic.Services;
using Windows.System;

namespace TrayTraffic.Views;

public sealed partial class FlyoutPage : Page, IDisposable
{
    private const int MaxRouteCount = 3;

    private readonly ForegroundPoller _poller;
    private CancellationTokenSource? _activation;
    private RouteSnapshot? _snapshot;
    private TrafficMap? _map;
    private SavedPlace? _origin;
    private SavedPlace? _destination;
    private string? _key;
    private int _revision = -1;
    private int _routeCount = MaxRouteCount;
    private CancellationTokenSource? _placeSearch;
    private bool _loadingPlaces;
    private bool _showingTrip;
    private bool _searching;
    private bool _visible;
    private bool _disposed;
    private bool _mapValidated;

    public FlyoutPage()
    {
        InitializeComponent();
        RouteToggle.Checked += RouteToggle_Changed;
        RouteToggle.Unchecked += RouteToggle_Changed;
        Motion.AnimateVisibility(TripPicker);
        Motion.AnimateVisibility(PlaceSearch);
        Motion.AnimateVisibility(RouteHeading);
        Motion.AnimateVisibility(RoutesArea);
        Motion.AnimateVisibility(EmptyState);
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
        TryCloseSearch();
        LoadingRing.IsActive = false;
        MapLoadingRing.IsActive = false;
        SkeletonPulse.Stop();
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
            if (_revision != SettingsService.Revision)
            {
                _revision = SettingsService.Revision;
                PopulatePlaces();
                ResetTrip();
            }
            else if (_origin is null || _destination is null)
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
                // Map tiles are requested with the key, so only a new key needs a new map.
                _key = key;
                ClearMap();
                ResetTrip();
            }
            if (key is null)
            {
                ShowEmpty("Connect to live traffic",
                    "Open Settings and add a free TomTom API key under Use your own TomTom key, then save your everyday places.");
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
            _map?.SetActive(_mapValidated);
            if (_snapshot is null)
            {
                ShowLoading();
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
        SavedPlace[] places = SettingsService.SearchedPlace is { } searched
            ? [.. SettingsService.Places, searched]
            : SettingsService.Places;
        _loadingPlaces = true;
        PlaceKind origin = originKind ?? SettingsService.Origin;
        PlaceKind destination = destinationKind ?? SettingsService.Destination;
        _origin = places.FirstOrDefault(place => place.Kind == origin) ?? places.FirstOrDefault();
        _destination = places.FirstOrDefault(place => place.Kind == destination && place != _origin)
            ?? places.FirstOrDefault(place => place != _origin);
        FillPicker(FromPicker, places, _origin);
        FillPicker(ToPicker, places.Where(place => place != _origin).ToArray(), _destination);
        FromPicker.IsEnabled = places.Length >= 2;
        ToPicker.IsEnabled = places.Length >= 2;
        SwapButton.IsEnabled = places.Length >= 2;
        _loadingPlaces = false;
        UpdateRouteControls();
    }

    /// <summary>
    /// Replaces a picker's items only when its places changed. Rebuilding the items of the picker
    /// that is raising SelectionChanged crashes XAML (E_UNEXPECTED) while its drop-down closes.
    /// </summary>
    private static void FillPicker(ComboBox picker, SavedPlace[] places, SavedPlace? selected)
    {
        // A newly searched place keeps its kind but changes its label, so compare both.
        if (!picker.Items.Cast<ComboBoxItem>().Select(item => (item.Tag, item.Content))
            .SequenceEqual(places.Select(place => ((object)place.Kind, (object)place.Label))))
        {
            picker.Items.Clear();
            foreach (SavedPlace place in places)
            {
                picker.Items.Add(new ComboBoxItem { Content = place.Label, Tag = place.Kind });
            }
        }
        picker.SelectedItem = picker.Items.Cast<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, selected?.Kind));
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
            bool newTrip = _snapshot is null;
            RouteSnapshot snapshot = new(origin, destination, preferences, routes, DateTimeOffset.Now);
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
            _snapshot = snapshot;
            _map.SetActive(_visible && _mapValidated);
            RenderRoutes(newTrip);
            UpdatedText.Text = $"Updated {snapshot.UpdatedAt:t} | {TrafficFormatting.PreferenceDescription(preferences)}";
            App.Current.SetTrayStatus($"{origin.Label} to {destination.Label}: {TrafficFormatting.Duration(routes[0].TravelSeconds)} (updated {snapshot.UpdatedAt:t})");
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
    }

    /// <summary>
    /// Cross-fades from the loading placeholders to the routes. A periodic refresh of the same trip
    /// keeps the chosen route selected and leaves the map where the user put it.
    /// </summary>
    private void RenderRoutes(bool newTrip)
    {
        if (_snapshot is not { } snapshot)
        {
            return;
        }
        _showingTrip = true;
        EmptyState.Visibility = Visibility.Collapsed;
        MapPlaceholder.Opacity = _mapValidated ? 0 : 1;
        MapLoadingRing.IsActive = false;
        MapHost.Opacity = 1;
        int selected = newTrip ? 0 : Math.Clamp(RoutesList.SelectedIndex, 0, snapshot.Routes.Length - 1);
        _routeCount = Math.Clamp(snapshot.Routes.Length, 1, MaxRouteCount);
        RoutesList.ItemsSource = snapshot.Routes.Select((route, index) =>
            new RouteItem(route, index, snapshot.Routes[0].TravelSeconds, SettingsService.UseKilometers)).ToArray();
        RoutesList.SelectedIndex = selected;
        RoutesList.IsEnabled = true;
        RoutesList.Opacity = 1;
        RouteSkeleton.Opacity = 0;
        SkeletonPulse.Stop();
        DirectionsButton.IsEnabled = true;
        UpdateRouteControls();
        if (newTrip && RouteToggle.IsChecked == true)
        {
            _ = DrawSelectionAsync(true);
        }
    }

    /// <summary>
    /// Holds the layout steady while routes load: the map (or a placeholder card) stays, and rows
    /// shaped like the last result stand in for the route list.
    /// </summary>
    private void ShowLoading()
    {
        _showingTrip = true;
        EmptyState.Visibility = Visibility.Collapsed;
        bool hasMap = _map is not null && _mapValidated;
        MapHost.Opacity = hasMap ? 1 : 0;
        MapPlaceholder.Opacity = hasMap ? 0 : 1;
        MapLoadingRing.IsActive = !hasMap;
        RecommendationText.Text = "Finding the fastest route...";
        DirectionsButton.IsEnabled = false;
        RoutesList.IsEnabled = false;
        RoutesList.Opacity = 0;
        if (SkeletonRows.Items.Count != _routeCount)
        {
            SkeletonRows.ItemsSource = new int[_routeCount];
        }
        RouteSkeleton.Opacity = 1;
        if (Motion.Enabled && SkeletonPulse.GetCurrentState() != ClockState.Active)
        {
            SkeletonPulse.Begin();
        }
        UpdatedText.Text = $"Checking traffic from {_origin?.Label} to {_destination?.Label}...";
        UpdateRouteControls();
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
        bool showTrip = showRoutes && _origin is not null && _destination is not null;
        TripPicker.Visibility = showTrip && !_searching ? Visibility.Visible : Visibility.Collapsed;
        PlaceSearch.Visibility = showTrip && _searching ? Visibility.Visible : Visibility.Collapsed;
        Visibility routeVisibility = showRoutes && _showingTrip
            ? Visibility.Visible : Visibility.Collapsed;
        RouteHeading.Visibility = routeVisibility;
        RoutesArea.Visibility = routeVisibility;
        _map?.SetRoutesVisible(showRoutes);
    }

    /// <summary>
    /// Forgets the routes for the previous trip or preferences. The map stays: it drops the old
    /// routes, pins the new places and glides to them while the new routes load.
    /// </summary>
    private void ResetTrip()
    {
        _snapshot = null;
        _poller.Invalidate();
        StatusBar.IsOpen = false;
        App.Current.SetTrayStatus(null);
        if (_origin is null || _destination is null)
        {
            return;
        }
        ShowLoading();
        if (_map is not null)
        {
            _ = ShowTripOnMapAsync(_map, _origin, _destination);
        }
    }

    private static async Task ShowTripOnMapAsync(TrafficMap map, SavedPlace origin, SavedPlace destination)
    {
        try
        {
            await map.ShowTripAsync(origin, destination);
        }
        catch (Exception exception)
        {
            LogService.Warning("Moving the map to a new trip", exception);
        }
    }

    private void ClearMap()
    {
        _mapValidated = false;
        _map?.Dispose();
        _map = null;
        MapHost.Content = null;
        MapHost.Opacity = 0;
        MapNotice.Visibility = Visibility.Collapsed;
    }

    private void ShowEmpty(string title, string description)
    {
        _poller.Stop();
        _showingTrip = false;
        _map?.SetActive(false);
        RefreshButton.IsEnabled = false;
        RefreshButton.Visibility = Visibility.Collapsed;
        EmptyTitle.Text = title;
        EmptyDescription.Text = description;
        EmptyState.Visibility = Visibility.Visible;
        MapHost.Opacity = 0;
        MapPlaceholder.Opacity = 0;
        MapLoadingRing.IsActive = false;
        SkeletonPulse.Stop();
        UpdatedText.Text = "Live traffic by TomTom";
        UpdateRouteControls();
    }

    private void ShowError(Exception exception)
    {
        StatusBar.Title = "Traffic couldn't update";
        StatusBar.Message = TrafficServices.ErrorMessage(exception) +
            (_snapshot is null ? "" : " Showing the last estimate.");
        StatusBar.IsOpen = true;
        if (_snapshot is null)
        {
            // With a map on screen, keep it and say so in the route heading; otherwise explain in place of the map.
            SkeletonPulse.Stop();
            RouteSkeleton.Opacity = 0;
            MapLoadingRing.IsActive = false;
            RecommendationText.Text = "No route yet. Refresh to try again.";
            UpdatedText.Text = "Live traffic by TomTom";
            if (_map is null)
            {
                _showingTrip = false;
                MapPlaceholder.Opacity = 0;
                EmptyTitle.Text = "No route yet";
                EmptyDescription.Text = "Check your key and saved places, or refresh to try again.";
                EmptyState.Visibility = Visibility.Visible;
                UpdateRouteControls();
            }
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
        ChangeTrip(origin, destination);
    }

    private void ChangeTrip(PlaceKind origin, PlaceKind destination)
    {
        _poller.Stop();
        _activation?.Cancel();
        PopulatePlaces(origin, destination);
        ResetTrip();
        PersistSelection();
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
        if (_origin is not null && _destination is not null)
        {
            ChangeTrip(_destination.Kind, _origin.Kind);
        }
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
        if (RouteToggle.IsChecked != true)
        {
            TryCloseSearch();
        }
        UpdateRouteControls();
        if (RouteToggle.IsChecked == true)
        {
            _ = DrawSelectionAsync(true);
        }
    }

    private void SearchButton_Click(object sender, RoutedEventArgs args)
    {
        _searching = true;
        PlaceSearchBox.Text = "";
        PlaceSearchBox.ItemsSource = null;
        PlaceSearchBox.Description = null;
        UpdateRouteControls();
        // Focus once the box has been laid out.
        DispatcherQueue.TryEnqueue(() => PlaceSearchBox.Focus(FocusState.Programmatic));
    }

    private void CloseSearchButton_Click(object sender, RoutedEventArgs args) => TryCloseSearch();

    /// <summary>Closes place search if it is open, so Escape backs out of it before hiding the flyout.</summary>
    public bool TryCloseSearch()
    {
        if (!_searching)
        {
            return false;
        }
        _searching = false;
        _placeSearch?.Cancel();
        PlaceSearchBox.IsSuggestionListOpen = false;
        UpdateRouteControls();
        return true;
    }

    private void PlaceSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            _placeSearch?.Cancel();
            sender.ItemsSource = null;
            sender.Description = null;
        }
    }

    // One search per Enter rather than per keystroke keeps well inside TomTom's free allowance.
    private async void PlaceSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is PlaceResultItem chosen)
        {
            // Let the box finish closing its list before the trip pickers are rebuilt.
            DispatcherQueue.TryEnqueue(() => GoTo(chosen.Place));
            return;
        }
        if (string.IsNullOrWhiteSpace(args.QueryText) || _key is not { } key || _origin is not { } origin)
        {
            return;
        }

        _placeSearch?.Cancel();
        CancellationTokenSource source = new();
        _placeSearch = source;
        sender.ItemsSource = null;
        sender.Description = null;
        LoadingRing.IsActive = true;
        try
        {
            PlaceCandidate[] places = await TrafficServices.Client.FindPlacesAsync(args.QueryText, origin.Location, key, source.Token);
            source.Token.ThrowIfCancellationRequested();
            if (!_searching)
            {
                return;
            }
            bool kilometers = SettingsService.UseKilometers;
            sender.ItemsSource = places.Select(place => new PlaceResultItem(place, origin, kilometers)).ToArray();
            sender.IsSuggestionListOpen = places.Length > 0;
            if (places.Length == 0)
            {
                sender.Description = "Nothing found. Try a place name, an address, or a city.";
            }
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LogService.Warning("Searching for a place", exception);
            if (_searching && !source.IsCancellationRequested)
            {
                sender.Description = TrafficServices.ErrorMessage(exception);
            }
        }
        finally
        {
            if (ReferenceEquals(_placeSearch, source))
            {
                _placeSearch = null;
                LoadingRing.IsActive = _poller.IsRunning && _snapshot is null;
            }
            source.Dispose();
        }
    }

    /// <summary>Makes a search result the destination, keeping a saved starting place.</summary>
    private void GoTo(PlaceCandidate place)
    {
        try
        {
            SettingsService.SearchedPlace = new(PlaceKind.Other, place.Detail.Length > 0 ? place.Detail : place.Name, place.Location, place.Name);
        }
        catch (Exception exception)
        {
            LogService.Warning("Saving the searched place", exception);
            PlaceSearchBox.Description = "This place couldn't be used. Try another result.";
            return;
        }
        TryCloseSearch();
        PlaceKind origin = _origin is { Kind: not PlaceKind.Other } start
            ? start.Kind
            : SettingsService.Places.FirstOrDefault()?.Kind ?? PlaceKind.Home;
        ChangeTrip(origin, PlaceKind.Other);
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
    /// <summary>
    /// Titles a route by its main roads when TomTom named them, falling back to its rank. The list is
    /// sorted fastest first and the selected route's comparison is shown above it, so the rank only
    /// needs to be spoken.
    /// </summary>
    public RouteItem(TrafficRoute route, int index, double fastestSeconds, bool kilometers)
    {
        string rank = index == 0 ? "Fastest route" : route.TravelSeconds == fastestSeconds ? "Also fastest" : $"Alternative {index}";
        Title = route.Name ?? rank;
        Duration = TrafficFormatting.Duration(route.TravelSeconds);
        Detail = $"{TrafficFormatting.Distance(route.DistanceMeters, kilometers)} | {TrafficFormatting.Delay(route.DelaySeconds)}";
        AccessibleName = $"{(route.Name is null ? rank : $"{rank} via {route.Name}")}, {Duration}, {Detail}";
    }

    public string Title { get; }
    public string Duration { get; }
    public string Detail { get; }
    public string AccessibleName { get; }
}

public sealed class PlaceResultItem(PlaceCandidate place, SavedPlace from, bool kilometers)
{
    public PlaceCandidate Place { get; } = place;
    public string Name => Place.Name;

    public string Detail { get; } = place.DistanceMeters is { } meters
        ? string.Join(" | ", new[] { $"{TrafficFormatting.Distance(meters, kilometers)} from {from.Label}", place.Detail }.Where(part => part.Length > 0))
        : place.Detail;

    public string AccessibleName => $"{Name}, {Detail}";

    // The suggestion list's automation and text fallbacks read this.
    public override string ToString() => Name;
}
