using WEBit.Core.Diagnostics;

namespace WEBit.Platform.Windows;

/// <summary>Read access of the service account to site folders outside the WEBit data folder.</summary>
public static class FolderAccess
{
    /// <summary>
    /// Grants NT SERVICE\WEBit read and execute (inherited by all files) on a folder the user owns.
    /// Needs no administrator rights for the user's own folders. Returns true on success.
    /// </summary>
    public static bool GrantServiceRead(string folder) => Grant(folder, "RX");

    /// <summary>Modify rights for app folders (npm install, uv sync write into the project).</summary>
    public static bool GrantServiceModify(string folder) => Grant(folder, "M");

    private static bool Grant(string folder, string rights)
    {
        try
        {
            var run = ProcessRunner.Run(
                "icacls.exe",
                [folder, "/grant", $"{WindowsServiceControl.VirtualAccount}:(OI)(CI){rights}", "/Q"],
                TimeSpan.FromMinutes(2));
            return run.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
