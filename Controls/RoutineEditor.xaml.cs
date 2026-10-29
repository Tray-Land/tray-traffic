using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using TrayTraffic.Core;
using TrayTraffic.Services;

namespace TrayTraffic.Controls;

/// <summary>One leave-time reminder in Settings: on/off, the time, and the days it runs.</summary>
public sealed partial class RoutineEditor : UserControl
{
    private readonly List<(DayOfWeek Day, ToggleButton Button)> _days = [];
    private string _id = "";
    private bool _loading;

    public RoutineEditor()
    {
        InitializeComponent();
        DateTimeFormatInfo format = CultureInfo.CurrentCulture.DateTimeFormat;
        for (int index = 0; index < 7; index++)
        {
            DayOfWeek day = (DayOfWeek)(((int)format.FirstDayOfWeek + index) % 7);
            ToggleButton button = new()
            {
                Content = format.ShortestDayNames[(int)day],
                Width = 40,
                Height = 32,
                Padding = new Thickness(0),
            };
            AutomationProperties.SetName(button, format.DayNames[(int)day]);
            ToolTipService.SetToolTip(button, format.DayNames[(int)day]);
            button.Checked += Routine_Changed;
            button.Unchecked += Routine_Changed;
            DayButtons.Children.Add(button);
            _days.Add((day, button));
        }
    }

    public void Initialize(string routineId)
    {
        _id = routineId;
        Refresh();
    }

    /// <summary>Reloads the routine, e.g. after a place it needs was saved or removed.</summary>
    public void Refresh()
    {
        CommuteRoutine routine = SettingsService.Routines.First(item => item.Id == _id);
        SavedPlace[] places = SettingsService.Places;
        string[] missing = new[] { routine.From, routine.To }
            .Where(kind => places.All(place => place.Kind != kind)).Select(kind => kind.ToString()).ToArray();

        _loading = true;
        string title = routine.Kind == RoutineKind.ArriveBy
            ? $"Arrive at {routine.To.ToString().ToLowerInvariant()} by"
            : $"Leave {routine.From.ToString().ToLowerInvariant()} at";
        EnabledBox.Content = title;
        AutomationProperties.SetName(TimeBox, $"{title} time");
        EnabledBox.IsChecked = routine.Enabled;
        // Leave a routine that's already on switchable, so it can be turned off after removing a place.
        EnabledBox.IsEnabled = missing.Length == 0 || routine.Enabled;
        TimeBox.SelectedTime = routine.Time.ToTimeSpan();
        foreach ((DayOfWeek day, ToggleButton button) in _days)
        {
            button.IsChecked = routine.RunsOn(day);
        }
        TripText.Text = missing.Length == 0
            ? $"From {routine.From} to {routine.To}"
            : $"Save {string.Join(" and ", missing)} under Your places to use this reminder.";
        _loading = false;
    }

    private void Routine_Changed(object sender, RoutedEventArgs args) => Save();

    private void TimeBox_SelectedTimeChanged(TimePicker sender, TimePickerSelectedValueChangedEventArgs args) => Save();

    private void Save()
    {
        if (_loading || _id.Length == 0)
        {
            return;
        }
        CommuteRoutine routine = SettingsService.Routines.First(item => item.Id == _id);
        WeekDays days = _days.Where(item => item.Button.IsChecked == true)
            .Aggregate(WeekDays.None, (all, item) => all | (WeekDays)(1 << (int)item.Day));
        try
        {
            SettingsService.SaveRoutine(routine with
            {
                Enabled = EnabledBox.IsChecked == true,
                Time = TimeBox.SelectedTime is { } time ? TimeOnly.FromTimeSpan(time) : routine.Time,
                Days = days,
            });
        }
        catch (Exception exception)
        {
            LogService.Warning("Saving a leave reminder", exception);
            Refresh();
        }
    }
}
