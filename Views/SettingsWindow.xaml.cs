using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TrayTraffic.Core;
using TrayTraffic.Services;
using Windows.ApplicationModel;
using WinUIEx;

namespace TrayTraffic.Views;

public sealed partial class SettingsWindow : WindowEx
{
    private readonly CancellationTokenSource _lifetime = new();
    private bool _loading = true;
    private bool _closed;
    private StartupTaskState? _startupState;

    public SettingsWindow()
    {
        InitializeComponent();
        Title = $"{App.DisplayName} Settings";
        AppTitleBar.Title = Title;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        WindowPlacementService.CenterOnPrimary(this, 560, 760);
        HomePicker.Initialize(PlaceKind.Home);
        WorkPicker.Initialize(PlaceKind.Work);
        SchoolPicker.Initialize(PlaceKind.School);
        AvoidTollsBox.IsChecked = SettingsService.AvoidTolls;
        AvoidFerriesBox.IsChecked = SettingsService.AvoidFerries;
        KilometersBox.IsChecked = SettingsService.UseKilometers;
        VersionText.Text = $"{App.DisplayName} {GetVersion()}";
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            HomePicker.Dispose();
            WorkPicker.Dispose();
            SchoolPicker.Dispose();
        };
        _loading = false;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            await UpdateKeyStatusAsync();
            StartupTaskState? state = await StartupService.GetStateAsync();
            if (!_closed)
            {
                ShowStartupState(state);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowFailure("Settings couldn't load", exception);
        }
    }

    private async Task UpdateKeyStatusAsync()
    {
        string? stored = await Task.Run(() => CredentialStore.Get(TrafficServices.CredentialName), _lifetime.Token);
        string? effective = stored ?? await TrafficServices.ReadKeyAsync(_lifetime.Token);
        if (!_closed)
        {
            RemoveKeyButton.IsEnabled = stored is not null;
            KeyStatus.Text = stored is not null ? "Key saved in Windows Credential Manager."
                : effective is not null ? "Using the optional build-time key. You can override it with your own."
                : "No key saved. Add one above before searching for an address.";
        }
    }

    private async void SaveKeyButton_Click(object sender, RoutedEventArgs args)
    {
        string key = ApiKeyBox.Password.Trim();
        try
        {
            TomTomClient.ValidateKey(key);
            SaveKeyButton.IsEnabled = false;
            await Task.Run(() => CredentialStore.Set(TrafficServices.CredentialName, key), _lifetime.Token);
            SettingsService.NotifyChanged();
            ApiKeyBox.Password = "";
            await UpdateKeyStatusAsync();
            if (!_closed)
            {
                SettingsStatus.Title = "Key saved securely";
                SettingsStatus.Message = "Search for an address below to check the connection.";
                SettingsStatus.Severity = InfoBarSeverity.Success;
                SettingsStatus.IsOpen = true;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowFailure("Key couldn't be saved", exception, exception is ArgumentException
                ? "Enter a TomTom API key without spaces (up to 256 characters)."
                : "Windows Credential Manager couldn't save the key. Your previous key is unchanged.");
        }
        finally
        {
            if (!_closed)
            {
                SaveKeyButton.IsEnabled = true;
            }
        }
    }

    private async void RemoveKeyButton_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            ContentDialog dialog = new()
            {
                XamlRoot = SettingsRoot.XamlRoot,
                Title = "Remove your TomTom key?",
                Content = "Live traffic and address searches will stop unless an optional build-time key is available.",
                PrimaryButtonText = "Remove key",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || _closed)
            {
                return;
            }
            await Task.Run(() => CredentialStore.Remove(TrafficServices.CredentialName), _lifetime.Token);
            SettingsService.NotifyChanged();
            await UpdateKeyStatusAsync();
            SettingsStatus.IsOpen = false;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowFailure("Key couldn't be removed", exception);
        }
    }

    private void Preference_Changed(object sender, RoutedEventArgs args)
    {
        if (_loading)
        {
            return;
        }
        try
        {
            if (ReferenceEquals(sender, AvoidTollsBox))
            {
                SettingsService.AvoidTolls = AvoidTollsBox.IsChecked == true;
            }
            else if (ReferenceEquals(sender, AvoidFerriesBox))
            {
                SettingsService.AvoidFerries = AvoidFerriesBox.IsChecked == true;
            }
            else
            {
                SettingsService.UseKilometers = KilometersBox.IsChecked == true;
            }
        }
        catch (Exception exception)
        {
            _loading = true;
            AvoidTollsBox.IsChecked = SettingsService.AvoidTolls;
            AvoidFerriesBox.IsChecked = SettingsService.AvoidFerries;
            KilometersBox.IsChecked = SettingsService.UseKilometers;
            _loading = false;
            ShowFailure("Preference couldn't be saved", exception);
        }
    }

    private void ShowFailure(string title, Exception exception, string? message = null)
    {
        LogService.Warning(title, exception);
        if (_closed)
        {
            return;
        }
        SettingsStatus.Title = title;
        SettingsStatus.Message = message ?? TrafficServices.ErrorMessage(exception);
        SettingsStatus.Severity = InfoBarSeverity.Error;
        SettingsStatus.IsOpen = true;
    }

    private static string GetVersion()
    {
        try
        {
            PackageVersion version = Package.Current.Id.Version;
            return $"{version.Major}.{version.Minor}.{version.Build}";
        }
        catch (Exception exception) when (exception.HResult == unchecked((int)0x80073D54))
        {
            return "(unpackaged)";
        }
    }

    private void ShowStartupState(StartupTaskState? state)
    {
        _startupState = state;
        _loading = true;
        StartupToggle.Visibility = state is null ? Visibility.Collapsed : Visibility.Visible;
        StartupToggle.IsOn = state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        StartupToggle.IsEnabled = state is StartupTaskState.Enabled or StartupTaskState.Disabled;
        StartupDescription.Text = state switch
        {
            StartupTaskState.DisabledByUser => "Turned off in Settings > Apps > Startup. Turn it on there.",
            StartupTaskState.DisabledByPolicy or StartupTaskState.EnabledByPolicy => "Managed by your organization.",
            null => "Only available when the app is installed.",
            _ => "Keep the tray icon ready after you sign in.",
        };
        _loading = false;
    }

    private async void StartupToggle_Toggled(object sender, RoutedEventArgs args)
    {
        if (!_loading)
        {
            StartupToggle.IsEnabled = false;
            try
            {
                StartupTaskState? state = await StartupService.SetEnabledAsync(StartupToggle.IsOn);
                if (!_closed)
                {
                    ShowStartupState(state);
                }
            }
            catch (Exception exception)
            {
                if (!_closed)
                {
                    ShowStartupState(_startupState);
                    ShowFailure("Start with Windows couldn't be changed", exception);
                }
            }
        }
    }

    private void DoneButton_Click(object sender, RoutedEventArgs args)
    {
        Close();
        App.Current.ShowFlyout();
    }
}
