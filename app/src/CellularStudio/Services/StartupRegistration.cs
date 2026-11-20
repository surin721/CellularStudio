using Microsoft.Win32;

namespace CellularStudio.Services;

/// <summary>Per-user "run at sign-in" via HKCU\...\Run. Starts hidden with only the tray icon.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CellularStudio";
    public const string TrayArg = "--tray";

    private static string Command => $"\"{Environment.ProcessPath}\" {TrayArg}";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
    }

    /// <summary>True when the registered command points at a different exe (e.g. the app was moved).</summary>
    public static bool IsStale
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string s && !string.Equals(s, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>Re-points an existing registration at the current exe location.</summary>
    public static void Repair()
    {
        if (IsEnabled && IsStale) Set(true);
    }
}
