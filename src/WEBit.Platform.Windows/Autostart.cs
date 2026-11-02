using Microsoft.Win32;

namespace WEBit.Platform.Windows;

/// <summary>Tray autostart at sign-in (HKCU\...\Run).</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WEBit";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        catch
        {
            return false;
        }
    }

    public static void Set(bool enabled, string executablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
        {
            key.SetValue(ValueName, $"\"{executablePath}\" --tray", RegistryValueKind.String);
        }
        else if (key.GetValue(ValueName) is not null)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>Removes the entry from HKLM and every loaded user hive (uninstall, admin).</summary>
    public static void RemoveForAllUsers()
    {
        TryDelete(Registry.LocalMachine, RunKey);
        TryDelete(Registry.CurrentUser, RunKey);
        try
        {
            foreach (var sid in Registry.Users.GetSubKeyNames())
            {
                TryDelete(Registry.Users, $@"{sid}\{RunKey}");
            }
        }
        catch
        {
            // Not allowed to enumerate hives.
        }
    }

    private static void TryDelete(RegistryKey hive, string path)
    {
        try
        {
            using var key = hive.OpenSubKey(path, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // Best effort.
        }
    }
}
