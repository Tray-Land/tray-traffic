using System.Text;
using System.Text.Json;
using TrayTraffic.Core;
using Windows.Storage;

namespace TrayTraffic.Services;

internal static class SettingsService
{
    public static event EventHandler? Changed;
    public static int Revision { get; private set; }

    public static bool HasLaunchedBefore
    {
        get => Get(nameof(HasLaunchedBefore), false);
        set => Set(nameof(HasLaunchedBefore), value);
    }

    public static SavedPlace[] Places
    {
        get
        {
            string json = Get(nameof(Places), "[]");
            try
            {
                SavedPlace[] places = JsonSerializer.Deserialize(json, TrafficJsonContext.Default.SavedPlaceArray)
                    ?? throw new JsonException();
                if (places.Length > 3 || places.Any(place => place is null || !place.IsValid) ||
                    places.DistinctBy(place => place.Kind).Count() != places.Length)
                {
                    throw new JsonException();
                }
                return places.OrderBy(place => place.Kind).ToArray();
            }
            catch (JsonException exception)
            {
                LogService.Warning("Reading saved places", exception);
                return [];
            }
        }
    }

    public static PlaceKind Origin
    {
        get => ReadKind(nameof(Origin), PlaceKind.Home);
        set => Set(nameof(Origin), (int)value);
    }

    public static PlaceKind Destination
    {
        get => ReadKind(nameof(Destination), PlaceKind.Work);
        set => Set(nameof(Destination), (int)value);
    }

    public static bool AvoidTolls
    {
        get => Get(nameof(AvoidTolls), false);
        set { Set(nameof(AvoidTolls), value); NotifyChanged(); }
    }

    public static bool AvoidFerries
    {
        get => Get(nameof(AvoidFerries), false);
        set { Set(nameof(AvoidFerries), value); NotifyChanged(); }
    }

    public static bool UseKilometers
    {
        get => Get(nameof(UseKilometers), false);
        set { Set(nameof(UseKilometers), value); NotifyChanged(); }
    }

    public static RoutePreferences Preferences => new(AvoidTolls, AvoidFerries);

    public static void SavePlace(SavedPlace place)
    {
        if (!place.IsValid)
        {
            throw new ArgumentException("Choose a valid address from the search results.");
        }
        SavedPlace[] places = Places;
        if (places.FirstOrDefault(existing => existing.Kind == place.Kind) != place)
        {
            WritePlaces(places.Where(existing => existing.Kind != place.Kind).Append(place).ToArray());
        }
    }

    public static void RemovePlace(PlaceKind kind) =>
        WritePlaces(Places.Where(place => place.Kind != kind).ToArray());

    public static void NotifyChanged()
    {
        Revision++;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static void WritePlaces(SavedPlace[] places)
    {
        string json = JsonSerializer.Serialize(places, TrafficJsonContext.Default.SavedPlaceArray);
        if (Encoding.UTF8.GetByteCount(json) > 7500)
        {
            throw new ArgumentException("These addresses are too long to save.");
        }
        Set(nameof(Places), json);
        NotifyChanged();
    }

    private static PlaceKind ReadKind(string key, PlaceKind fallback)
    {
        int value = Get(key, (int)fallback);
        return Enum.IsDefined((PlaceKind)value) ? (PlaceKind)value : fallback;
    }

    private static T Get<T>(string key, T fallback)
    {
        try
        {
            return ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out object? value) && value is T typed
                ? typed : fallback;
        }
        catch (Exception exception)
        {
            LogService.Warning("Reading preferences", exception);
            return fallback;
        }
    }

    private static void Set(string key, object value) =>
        ApplicationData.Current.LocalSettings.Values[key] = value;
}
