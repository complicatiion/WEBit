using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace WEBit.App.Services;

/// <summary>Paths and shell actions of the app.</summary>
public static class AppEnvironment
{
    public const string ProjectUrl = "https://github.com/complicatiion/WEBit";
    public const string MitLicenseUrl = "https://opensource.org/license/mit";

    public static string Version =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppEnvironment).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";

    public static string BaseDirectory => AppContext.BaseDirectory;

    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(BaseDirectory, "WEBit.exe");

    public static string LicensesDirectory => Path.Combine(BaseDirectory, "licenses");

    public static bool IsPortable => File.Exists(Path.Combine(BaseDirectory, "WEBit.portable"));

    /// <summary>WEBit.Service.exe next to the app (release) or in the service build folder (development).</summary>
    public static string? FindServiceExecutable()
    {
        var exeName = OperatingSystem.IsWindows() ? "WEBit.Service.exe" : "WEBit.Service";
        var local = Path.Combine(BaseDirectory, exeName);
        if (File.Exists(local))
        {
            return local;
        }

        var dir = new DirectoryInfo(BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var projectBin = Path.Combine(dir.FullName, "src", "WEBit.Service", "bin");
            if (Directory.Exists(projectBin))
            {
                return Directory.EnumerateFiles(projectBin, exeName, SearchOption.AllDirectories)
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault()?.FullName;
            }
        }

        return null;
    }

    /// <summary>Inno Setup uninstaller of an installed copy.</summary>
    public static string? FindSetupUninstaller() =>
        Directory.Exists(BaseDirectory)
            ? Directory.EnumerateFiles(BaseDirectory, "unins*.exe").OrderBy(f => f).FirstOrDefault()
            : null;

    public static bool Launch(string file)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // No default browser.
        }
    }

    public static void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path) && OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
                return;
            }

            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
        }
        catch
        {
            // Ignore.
        }
    }

    /// <summary>
    /// Ends running WEBit.Service processes in desktop mode (other WEBit version).
    /// False when one could not be ended (e.g. it belongs to another user).
    /// </summary>
    public static bool StopServiceProcesses()
    {
        var ok = true;
        foreach (var process in Process.GetProcessesByName("WEBit.Service"))
        {
            using (process)
            {
                try
                {
                    // Every running instance speaks another protocol (also an old build at the same path).
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
                catch (Exception)
                {
                    ok = false;
                }
            }
        }

        return ok;
    }

    /// <summary>Starts the service in desktop mode (no Windows service).</summary>
    public static bool StartDesktopMode()
    {
        var exe = FindServiceExecutable();
        if (exe is null)
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                ArgumentList = { "--console" }
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes the portable program folder after the app has exited.
    /// Only runs for a real portable WEBit folder (marker and both executables present).
    /// </summary>
    public static bool ScheduleFolderRemoval()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var dir = Path.GetFullPath(BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var isWebitFolder = IsPortable
                            && File.Exists(Path.Combine(dir, "WEBit.exe"))
                            && File.Exists(Path.Combine(dir, "WEBit.Service.exe"))
                            && !string.Equals(Path.GetPathRoot(dir)?.TrimEnd(Path.DirectorySeparatorChar), dir, StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(dir, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(dir, Environment.GetFolderPath(Environment.SpecialFolder.Desktop), StringComparison.OrdinalIgnoreCase);
        if (!isWebitFolder)
        {
            return false;
        }

        var pid = Environment.ProcessId;
        var script = Path.Combine(Path.GetTempPath(), $"webit-remove-{pid}.cmd");
        var text = new StringBuilder()
            .AppendLine("@echo off")
            .AppendLine("chcp 65001 >nul")
            .AppendLine(":wait")
            .AppendLine($"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul && (timeout /t 1 /nobreak >nul & goto wait)")
            .AppendLine("timeout /t 2 /nobreak >nul")
            .AppendLine($"rmdir /s /q \"{dir}\"")
            .AppendLine("del \"%~f0\"")
            .ToString();

        try
        {
            File.WriteAllText(script, text, new UTF8Encoding(false));
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath()
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
