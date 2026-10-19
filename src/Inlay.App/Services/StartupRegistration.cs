using Inlay.Platform;
using Microsoft.Win32;

namespace Inlay.App.Services;

/// <summary>Start at login through the per-user Run key (Inlay is unpackaged; no admin rights needed).</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Inlay";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    /// <summary>Writes or removes the Run entry so it matches the setting (and the current executable path).</summary>
    public static void Apply(bool enabled)
    {
        if (AppPaths.IsScratch)
            return; // a test or measurement run must not register itself to start at login

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key.GetValue(CometLegacy.RunValueName) is not null)
                key.DeleteValue(CometLegacy.RunValueName, throwOnMissingValue: false); // Comet 1.0's entry; Inlay's replaces it
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (exe is null)
                    return;
                var command = $"\"{exe}\" --background";
                if (key.GetValue(ValueName) as string != command)
                    key.SetValue(ValueName, command);
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Error("start at login", ex);
        }
    }
}
