using Microsoft.Win32;

namespace Govorun.App.Services;

/// <summary>Manages the HKCU Run key for launching Govorun at logon.</summary>
public static class AutoStart
{
    /// <summary>
    /// Passed by the Run entry so the app can tell a logon launch from a manual one and
    /// defer the model load. The installer (govorun.iss) writes the same flag.
    /// </summary>
    public const string Flag = "--autostart";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Govorun";

    private static string Command => $"\"{Environment.ProcessPath}\" {Flag}";

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, Command);
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// Brings an existing Run entry up to date — installs before 0.1.2 wrote it without
    /// <see cref="Flag"/>, and the exe may have moved. Never creates a missing entry: its
    /// absence can be the user switching autostart off outside the app, which must stick.
    /// </summary>
    public static void RefreshIfPresent()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is not string current) return;
        if (!string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
            key.SetValue(ValueName, Command);
    }
}
