using System.Diagnostics;

namespace TrayTraffic.Services;

internal static class LogService
{
    public static void Warning(string operation, Exception exception) =>
        Debug.WriteLine($"TrayTraffic: {operation} failed ({exception.GetType().Name}, HRESULT 0x{exception.HResult:X8}).");

    public static void Crash(Exception? exception)
    {
        string message = $"[{DateTimeOffset.Now:O}] Unhandled {exception?.GetType().FullName} " +
            $"(HRESULT 0x{exception?.HResult:X8}){Environment.NewLine}{exception?.StackTrace}{Environment.NewLine}";
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "TrayTraffic-crash.log"), message);
        }
        catch (Exception failure)
        {
            Warning("Writing the crash log", failure);
        }
    }
}
