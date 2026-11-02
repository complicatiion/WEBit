using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using WEBit.Core.Diagnostics;

namespace WEBit.Platform.Windows;

public enum WindowsServiceState
{
    NotInstalled,
    Stopped,
    StartPending,
    StopPending,
    Running,
    Unknown
}

/// <summary>Query and control of the "WEBit" Windows service via sc.exe.</summary>
public static partial class WindowsServiceControl
{
    public const string ServiceName = "WEBit";
    public const string DisplayName = "WEBit Web Server";
    public const string AdminGroupName = "WEBit Admins";
    public const string VirtualAccount = @"NT SERVICE\WEBit";

    [GeneratedRegex(@"\b([1-7])\s+(STOPPED|START_PENDING|STOP_PENDING|RUNNING|CONTINUE_PENDING|PAUSE_PENDING|PAUSED)\b")]
    private static partial Regex StateRegex();

    public static bool IsInstalled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    public static WindowsServiceState Query()
    {
        if (!IsInstalled())
        {
            return WindowsServiceState.NotInstalled;
        }

        var run = ProcessRunner.Run("sc.exe", ["query", ServiceName], TimeSpan.FromSeconds(8));
        var match = StateRegex().Match(run.Output);
        if (!match.Success)
        {
            return WindowsServiceState.Unknown;
        }

        return match.Groups[1].Value switch
        {
            "1" => WindowsServiceState.Stopped,
            "2" => WindowsServiceState.StartPending,
            "3" => WindowsServiceState.StopPending,
            "4" => WindowsServiceState.Running,
            _ => WindowsServiceState.Unknown
        };
    }

    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Runs a program elevated (UAC) and waits for it.</summary>
    /// <returns>Exit code; -2 = cancelled by user; -1 = error.</returns>
    public static int RunElevated(string fileName, string arguments, TimeSpan? timeout = null)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });

            if (process is null)
            {
                return -1;
            }

            if (!process.WaitForExit(timeout ?? TimeSpan.FromMinutes(2)))
            {
                return -1;
            }

            return process.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return -2;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>Starts or stops the service elevated.</summary>
    public static int StartElevated() => RunElevated("sc.exe", $"start {ServiceName}");

    public static int StopElevated() => RunElevated("sc.exe", $"stop {ServiceName}");
}
