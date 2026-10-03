using Windows.ApplicationModel;

namespace TrayTraffic.Services;

/// <summary>Wraps the MSIX "start with Windows" task declared in Package.appxmanifest.</summary>
internal static class StartupService
{
    private const string TaskId = "TrayTrafficStartup";
    private const int NoPackage = unchecked((int)0x80073D54);

    /// <summary>Returns the current state, or null when the app runs unpackaged.</summary>
    public static async Task<StartupTaskState?> GetStateAsync()
    {
        try
        {
            StartupTask task = await StartupTask.GetAsync(TaskId);
            return task.State;
        }
        catch (Exception exception) when (exception.HResult == NoPackage)
        {
            return null;
        }
    }

    public static async Task<StartupTaskState?> SetEnabledAsync(bool enabled)
    {
        try
        {
            StartupTask task = await StartupTask.GetAsync(TaskId);
            if (enabled)
            {
                return await task.RequestEnableAsync();
            }

            task.Disable();
            return task.State;
        }
        catch (Exception exception) when (exception.HResult == NoPackage)
        {
            return null;
        }
    }
}
