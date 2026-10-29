using System.Globalization;
using System.Text;
using System.Text.Json;
using TrayTraffic.Core;
using Windows.Storage;

namespace TrayTraffic.Services;

internal enum WindowMode
{
    Flyout,
    Window,
    AlwaysOnTop,
}

internal static class SettingsService
{
    public static event EventHandler? Changed;

    /// <summary>Routines, the reminder lead time, or pausing changed. Doesn't disturb the flyout's trip.</summary>
    public static event EventHandler? RemindersChanged;

    public static int Revision { get; private set; }

    public static readonly int[] ReminderLeadChoices = [5, 10, 15, 20, 30];

    public static bool HasLaunchedBefore
    {
        get => Get(nameof(HasLaunchedBefore), false);
        set => Set(nameof(HasLaunchedBefore), value);
    }

    /// <summary>How the main popup behaves: a light-dismissing flyout, a normal window, or a window kept on top.</summary>
    public static WindowMode WindowMode
    {
        get
        {
            int value = Get(nameof(WindowMode), (int)WindowMode.Flyout);
            return Enum.IsDefined((WindowMode)value) ? (WindowMode)value : WindowMode.Flyout;
        }
        set => Set(nameof(WindowMode), (int)value);
    }

    /// <summary>Whether the window can be resized. Only applies outside flyout mode.</summary>
    public static bool WindowResizable
    {
        get => Get(nameof(WindowResizable), false);
        set => Set(nameof(WindowResizable), value);
    }

    /// <summary>Whether a window mode reopens at the size and spot it was last left in.</summary>
    public static bool RememberWindowBounds
    {
        get => Get(nameof(RememberWindowBounds), false);
        set => Set(nameof(RememberWindowBounds), value);
    }

    /// <summary>The window's last bounds in physical pixels, or null when none are saved.</summary>
    public static Windows.Graphics.RectInt32? WindowBounds
    {
        get
        {
            string[] parts = Get(nameof(WindowBounds), "").Split(',');
            return parts.Length == 4 && parts.All(part => int.TryParse(part, CultureInfo.InvariantCulture, out _))
                ? new Windows.Graphics.RectInt32(
                    int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture),
                    int.Parse(parts[2], CultureInfo.InvariantCulture), int.Parse(parts[3], CultureInfo.InvariantCulture))
                : null;
        }
        set
        {
            if (value is { } bounds)
            {
                Set(nameof(WindowBounds), string.Create(CultureInfo.InvariantCulture, $"{bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}"));
            }
        }
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
                if (places.Length > 3 || places.Any(place => place is null || !place.IsValid || place.Kind == PlaceKind.Other) ||
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

    /// <summary>The last place found by search in the flyout, offered alongside the saved places.</summary>
    public static SavedPlace? SearchedPlace
    {
        get
        {
            string json = Get(nameof(SearchedPlace), "");
            if (json.Length == 0)
            {
                return null;
            }
            try
            {
                SavedPlace? place = JsonSerializer.Deserialize(json, TrafficJsonContext.Default.SavedPlace);
                return place is { Kind: PlaceKind.Other, IsValid: true } ? place : null;
            }
            catch (JsonException exception)
            {
                LogService.Warning("Reading the searched place", exception);
                return null;
            }
        }
        set
        {
            if (value is { Kind: not PlaceKind.Other } or { IsValid: false })
            {
                throw new ArgumentException("Choose a valid place from the search results.");
            }
            Set(nameof(SearchedPlace), value is null ? "" : JsonSerializer.Serialize(value, TrafficJsonContext.Default.SavedPlace));
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

    /// <summary>The leave-time reminder routines, always one for each default pattern.</summary>
    public static CommuteRoutine[] Routines
    {
        get
        {
            CommuteRoutine[] saved = [];
            try
            {
                saved = JsonSerializer.Deserialize(Get(nameof(Routines), "[]"), TrafficJsonContext.Default.CommuteRoutineArray) ?? [];
            }
            catch (JsonException exception)
            {
                LogService.Warning("Reading reminder routines", exception);
            }
            return CommuteRoutine.Defaults
                .Select(fallback => saved.FirstOrDefault(routine => routine is { IsValid: true } && routine.Id == fallback.Id
                    && routine.From == fallback.From && routine.To == fallback.To && routine.Kind == fallback.Kind) ?? fallback)
                .ToArray();
        }
    }

    /// <summary>Saves one routine. A changed routine may be due again today, so its reminder resets.</summary>
    public static void SaveRoutine(CommuteRoutine routine)
    {
        if (!routine.IsValid)
        {
            throw new ArgumentException("This reminder isn't valid.");
        }
        CommuteRoutine[] routines = Routines.Select(existing => existing.Id == routine.Id ? routine : existing).ToArray();
        Set(nameof(Routines), JsonSerializer.Serialize(routines, TrafficJsonContext.Default.CommuteRoutineArray));
        ApplicationData.Current.LocalSettings.Values.Remove(RemindedKey(routine.Id));
        RemindersChanged?.Invoke(null, EventArgs.Empty);
    }

    public static int ReminderLeadMinutes
    {
        get
        {
            int minutes = Get(nameof(ReminderLeadMinutes), ReminderLeadChoices[0]);
            return ReminderLeadChoices.Contains(minutes) ? minutes : ReminderLeadChoices[0];
        }
        set
        {
            Set(nameof(ReminderLeadMinutes), value);
            RemindersChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    public static bool RemindersPaused
    {
        get => Get(nameof(RemindersPaused), false);
        set
        {
            Set(nameof(RemindersPaused), value);
            RemindersChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>The date of the last occurrence a routine reminded for, so a restart doesn't repeat it.</summary>
    public static DateOnly? RemindedOn(string routineId) =>
        DateOnly.TryParseExact(Get(RemindedKey(routineId), ""), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date)
            ? date : null;

    public static void SetRemindedOn(string routineId, DateOnly date) =>
        Set(RemindedKey(routineId), date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    /// <summary>The routine's last measured travel time, used before today's traffic is known.</summary>
    public static TimeSpan UsualTravel(string routineId)
    {
        double seconds = Get(TravelKey(routineId), 0d);
        return double.IsFinite(seconds) && seconds > 0 ? TimeSpan.FromSeconds(seconds) : CommutePlanner.DefaultTravel;
    }

    public static void SetUsualTravel(string routineId, double seconds) => Set(TravelKey(routineId), seconds);

    private static string RemindedKey(string routineId) => $"RemindedOn.{routineId}";
    private static string TravelKey(string routineId) => $"UsualTravel.{routineId}";

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
