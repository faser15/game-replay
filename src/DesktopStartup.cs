using Microsoft.Win32;

namespace GameReplay;

public static class DesktopStartup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "GameReplay";
    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return string.Equals(key?.GetValue(ValueName) as string, StartupCommand(), StringComparison.OrdinalIgnoreCase);
        }
    }
    // Call only on an explicit user toggle/apply, never when initializing the UI.
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("Windows startup settings could not be opened.");
        if (enabled) key.SetValue(ValueName, StartupCommand(), RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
    static string StartupCommand()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The executable path is unavailable.");
        return "\"" + executable + "\"";
    }
}
