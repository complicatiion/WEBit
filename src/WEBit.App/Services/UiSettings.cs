using WEBit.Core.Storage;

namespace WEBit.App.Services;

/// <summary>Per-user UI settings (%AppData%\WEBit\ui.json).</summary>
public sealed class UiSettings
{
    public string? Language { get; set; }

    /// <summary>Custom message template; empty = default of the language.</summary>
    public string? MessageTemplate { get; set; }

    public int LogLines { get; set; } = 300;

    /// <summary>Auto refresh of the logs page in seconds.</summary>
    public int LogRefreshSeconds { get; set; } = 3;

    /// <summary>Notifications (toasts) bottom right.</summary>
    public bool NotificationsEnabled { get; set; } = true;

    /// <summary>Show only error notifications.</summary>
    public bool NotificationsErrorsOnly { get; set; }

    public double ToastSeconds { get; set; } = 4;

    public double ErrorToastSeconds { get; set; } = 8;

    /// <summary>
    /// Start the service in desktop mode when none is reachable. Null = default of the installation
    /// (true when the setup chose desktop mode, marker file WEBit.desktop next to the app).
    /// </summary>
    public bool? AutoDesktopMode { get; set; }

    public bool AutoDesktopModeEffective =>
        AutoDesktopMode ?? File.Exists(Path.Combine(AppContext.BaseDirectory, "WEBit.desktop"));

    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WEBit", "ui.json");

    public static UiSettings Load()
    {
        try
        {
            return JsonFile.Read<UiSettings>(FilePath) ?? new UiSettings();
        }
        catch
        {
            return new UiSettings();
        }
    }

    public void Save()
    {
        try
        {
            JsonFile.Write(FilePath, this);
        }
        catch
        {
            // Not critical.
        }
    }
}
