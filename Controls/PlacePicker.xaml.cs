using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TrayTraffic.Core;
using TrayTraffic.Services;

namespace TrayTraffic.Controls;

public sealed partial class PlacePicker : UserControl, IDisposable
{
    private PlaceKind _kind;
    private LocationCandidate[] _candidates = [];
    private CancellationTokenSource? _search;
    private bool _disposed;

    public PlacePicker()
    {
        InitializeComponent();
    }

    public void Initialize(PlaceKind kind)
    {
        _kind = kind;
        AddressBox.Header = kind.ToString();
        AutomationProperties.SetAutomationId(AddressBox, $"{kind}Address");
        AutomationProperties.SetName(AddressBox, $"{kind} address");
        AutomationProperties.SetAutomationId(RemoveButton, $"Remove{kind}");
        AutomationProperties.SetName(RemoveButton, $"Remove {kind} address");
        ToolTipService.SetToolTip(RemoveButton, $"Remove {kind} address");
        ShowSavedPlace();
    }

    public void Dispose()
    {
        _disposed = true;
        _search?.Cancel();
    }

    private async void AddressBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is string chosen)
        {
            SaveCandidate(chosen);
            return;
        }

        _search?.Cancel();
        CancellationTokenSource source = new();
        _search = source;
        _candidates = [];
        sender.ItemsSource = null;
        PlaceStatus.Text = "Searching...";
        try
        {
            string? key = await TrafficServices.ReadKeyAsync(source.Token);
            if (key is null)
            {
                PlaceStatus.Text = "Add a TomTom API key under Use your own TomTom key below before searching.";
                return;
            }

            LocationCandidate[] candidates = await TrafficServices.Client.SearchAsync(args.QueryText, key, source.Token);
            source.Token.ThrowIfCancellationRequested();
            if (_disposed)
            {
                return;
            }
            _candidates = candidates;
            sender.ItemsSource = candidates.Select(candidate => candidate.Address).ToArray();
            sender.IsSuggestionListOpen = candidates.Length > 0;
            PlaceStatus.Text = candidates.Length > 0
                ? "Choose an address from the results to save it."
                : "No address found. Include a street, city, and postal code.";
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LogService.Warning("Searching for a saved place", exception);
            if (!_disposed && !source.IsCancellationRequested)
            {
                PlaceStatus.Text = TrafficServices.ErrorMessage(exception);
            }
        }
        finally
        {
            if (ReferenceEquals(_search, source))
            {
                _search = null;
            }
            source.Dispose();
        }
    }

    private void AddressBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            _search?.Cancel();
            _candidates = [];
            sender.ItemsSource = null;
            PlaceStatus.Text = "Press Enter or the search icon, then choose the correct address.";
        }
    }

    private void AddressBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is string address)
        {
            SaveCandidate(address);
        }
    }

    private void SaveCandidate(string address)
    {
        LocationCandidate? candidate = _candidates.FirstOrDefault(result => result.Address == address);
        if (candidate is null)
        {
            PlaceStatus.Text = "Search again and choose a valid result.";
            return;
        }
        try
        {
            SettingsService.SavePlace(new(_kind, candidate.Address, candidate.Location));
            ShowSavedPlace();
        }
        catch (Exception exception)
        {
            LogService.Warning("Saving a place", exception);
            PlaceStatus.Text = "Couldn't save this address. Your previous address is unchanged.";
        }
    }

    private void ShowSavedPlace()
    {
        SavedPlace? saved = SettingsService.Places.FirstOrDefault(place => place.Kind == _kind);
        AddressBox.Text = saved?.Address ?? "";
        RemoveButton.IsEnabled = saved is not null;
        PlaceStatus.Text = saved is null ? "Not set. Search and choose an address." : "Saved. Search again to change this place.";
    }

    private async void RemoveButton_Click(object sender, RoutedEventArgs args)
    {
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = $"Remove {_kind}?",
            Content = "This removes the saved address and routes involving this place.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && !_disposed)
            {
                _search?.Cancel();
                SettingsService.RemovePlace(_kind);
                _candidates = [];
                AddressBox.ItemsSource = null;
                ShowSavedPlace();
            }
        }
        catch (Exception exception)
        {
            LogService.Warning("Removing a saved place", exception);
            PlaceStatus.Text = "Couldn't remove this address. Try again.";
        }
    }
}
