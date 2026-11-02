using System.Globalization;
using System.Security.Principal;
using System.Text;
using WEBit.Core.Diagnostics;
using WEBit.Core.Storage;
using WEBit.Platform.Windows;

namespace WEBit.Service.Setup;

/// <summary>
/// Service setup: --install [--user DOMAIN\name], --uninstall [--purge].
/// Started elevated by the UI or the installer; logs to logs\setup.log.
/// </summary>
public static class ServiceSetup
{
    public const int ExitOk = 0;
    public const int ExitNotElevated = 10;
    public const int ExitFailed = 11;

    public static int Install(WebitPaths paths, string[] args)
    {
        var log = new SetupLog();
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                log.Write("Service installation is only available on Windows.");
                return ExitFailed;
            }

            if (!WindowsServiceControl.IsElevated())
            {
                log.Write("Administrator rights required.");
                return ExitNotElevated;
            }

            paths.EnsureCreated();
            var exe = Environment.ProcessPath ?? Path.Combine(paths.BaseDirectory, "WEBit.Service.exe");
            log.Write($"Service: {exe}");
            log.Write($"Data:    {paths.DataRoot}");

            Run(log, "net.exe", "localgroup", WindowsServiceControl.AdminGroupName, "/add", "/comment:May control WEBit");
            var user = ArgValue(args, "--user");
            if (!string.IsNullOrWhiteSpace(user))
            {
                Run(log, "net.exe", "localgroup", WindowsServiceControl.AdminGroupName, user, "/add");
                AllowUser(paths, user, log);
            }

            if (WindowsServiceControl.IsInstalled())
            {
                log.Write("Service already registered; updating configuration.");
                StopService(log);
                RunChecked(log, "sc.exe", "config", WindowsServiceControl.ServiceName,
                    "binPath=", $"\"{exe}\"",
                    "start=", "delayed-auto",
                    "obj=", WindowsServiceControl.VirtualAccount,
                    "DisplayName=", WindowsServiceControl.DisplayName);
            }
            else
            {
                RunChecked(log, "sc.exe", "create", WindowsServiceControl.ServiceName,
                    "binPath=", $"\"{exe}\"",
                    "start=", "delayed-auto",
                    "obj=", WindowsServiceControl.VirtualAccount,
                    "DisplayName=", WindowsServiceControl.DisplayName);
            }

            Run(log, "sc.exe", "description", WindowsServiceControl.ServiceName, "WEBit multi-site web server (sksdesign).");
            Run(log, "sc.exe", "failure", WindowsServiceControl.ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/10000/restart/30000");

            // Program folder: read/execute; data folder: modify.
            Run(log, "icacls.exe", paths.BaseDirectory, "/grant", $"{WindowsServiceControl.VirtualAccount}:(OI)(CI)RX", "/T", "/C", "/Q");
            // Data folder: only SYSTEM, Administrators, the service and "WEBit Admins". The inherited ProgramData
            // rights (Users: read + create) would expose the MariaDB secret, backups and site settings and let
            // any user plant module executables the service runs.
            Run(log, "icacls.exe", paths.DataRoot, "/inheritance:r", "/grant:r",
                "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F",
                $"{WindowsServiceControl.VirtualAccount}:(OI)(CI)M", $"{WindowsServiceControl.AdminGroupName}:(OI)(CI)M", "/C", "/Q");

            // Explicit Users entries of older installations (desktop mode up to 0.0.6) are not removed by the above.
            Run(log, "icacls.exe", paths.DataRoot, "/remove:g", "*S-1-5-32-545", "*S-1-5-11", "/C", "/Q");

            // Group membership only counts after the next sign-in: the installing user gets direct access too.
            if (!string.IsNullOrWhiteSpace(user))
            {
                Run(log, "icacls.exe", paths.DataRoot, "/grant", $"{user}:(OI)(CI)M", "/C", "/Q");
            }

            RunChecked(log, "sc.exe", "start", WindowsServiceControl.ServiceName);
            log.Write("Service installed and started.");
            return ExitOk;
        }
        catch (Exception ex)
        {
            log.Write("ERROR: " + ex.Message);
            return ExitFailed;
        }
        finally
        {
            log.Save(paths);
        }
    }

    /// <summary>Removes service, group, autostart entries and firewall rules; --purge also deletes all data.</summary>
    public static int Uninstall(WebitPaths paths, string[] args)
    {
        var purge = args.Any(a => string.Equals(a, "--purge", StringComparison.OrdinalIgnoreCase));
        var log = new SetupLog();
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                return ExitFailed;
            }

            if (!WindowsServiceControl.IsElevated())
            {
                log.Write("Administrator rights required.");
                return ExitNotElevated;
            }

            if (WindowsServiceControl.IsInstalled())
            {
                StopService(log);
                RunChecked(log, "sc.exe", "delete", WindowsServiceControl.ServiceName);
                for (var i = 0; i < 20 && WindowsServiceControl.IsInstalled(); i++)
                {
                    Thread.Sleep(250);
                }

                log.Write("Service removed.");
            }
            else
            {
                log.Write("Service not registered.");
            }

            // Desktop mode, engine and modules started from this folder.
            KillProcesses(log, paths);

            Run(log, "net.exe", "localgroup", WindowsServiceControl.AdminGroupName, "/delete");
            Run(log, "netsh.exe", "advfirewall", "firewall", "delete", "rule", "name=WEBit");
            Autostart.RemoveForAllUsers();
            log.Write("Autostart entries removed.");

            if (purge)
            {
                log.Save(paths);
                DeleteData(paths);
                return ExitOk;
            }

            log.Write("Uninstall finished. Sites and settings were kept.");
            return ExitOk;
        }
        catch (Exception ex)
        {
            log.Write("ERROR: " + ex.Message);
            return ExitFailed;
        }
        finally
        {
            if (!purge)
            {
                log.Save(paths);
            }
        }
    }

    private static void StopService(SetupLog log)
    {
        Run(log, "sc.exe", "stop", WindowsServiceControl.ServiceName);
        for (var i = 0; i < 40 && WindowsServiceControl.Query() is not (WindowsServiceState.Stopped or WindowsServiceState.NotInstalled); i++)
        {
            Thread.Sleep(500);
        }
    }

    /// <summary>Kills WEBit.Service and module processes that run from this installation or data\modules.</summary>
    private static void KillProcesses(SetupLog log, WebitPaths paths)
    {
        var self = Environment.ProcessId;
        var baseDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.BaseDirectory)) + Path.DirectorySeparatorChar;
        var modulesDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.ModulesDir)) + Path.DirectorySeparatorChar;
        foreach (var name in new[]
                 {
                     "WEBit.Service", "WEBit-Caddy", "WEBit-PHP", "WEBit-Mailpit", "WEBit-MariaDB", "WEBit-Node",
                     "caddy", "mailpit", "php-cgi", "mariadbd", "mysqld", "node", "uv"
                 })
        {
            foreach (var process in System.Diagnostics.Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        if (process.Id == self)
                        {
                            continue;
                        }

                        var file = process.MainModule?.FileName;
                        // Program folder or downloaded modules (data\modules).
                        if (file is null
                            || (!Path.GetFullPath(file).StartsWith(baseDir, StringComparison.OrdinalIgnoreCase)
                                && !Path.GetFullPath(file).StartsWith(modulesDir, StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        process.Kill(entireProcessTree: true);
                        log.Write($"Stopped {name} (PID {process.Id}).");
                    }
                    catch
                    {
                        // Access denied or already gone.
                    }
                }
            }
        }
    }

    private static void DeleteData(WebitPaths paths)
    {
        var data = Path.GetFullPath(paths.DataRoot);
        if (!Directory.Exists(data) || Path.GetPathRoot(data) == data)
        {
            return;
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(data, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(500);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(500);
            }
        }
    }

    private static void AllowUser(WebitPaths paths, string user, SetupLog log)
    {
        try
        {
            var sid = ((SecurityIdentifier)new NTAccount(user).Translate(typeof(SecurityIdentifier))).Value;
            var repository = new SettingsRepository(paths);
            var settings = repository.Load();
            if (!settings.AllowedUserSids.Contains(sid, StringComparer.OrdinalIgnoreCase))
            {
                settings.AllowedUserSids.Add(sid);
                repository.Save(settings);
            }

            log.Write($"User {user} ({sid}) may control the service.");
        }
        catch (Exception ex)
        {
            log.Write($"Could not resolve user {user}: {ex.Message}");
        }
    }

    private static string? ArgValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static ProcessRunner.Result Run(SetupLog log, string file, params string[] arguments)
    {
        var result = ProcessRunner.Run(file, arguments, TimeSpan.FromSeconds(60));
        log.Write($"> {file} {string.Join(' ', arguments)}  → {result.ExitCode}");
        if (!string.IsNullOrWhiteSpace(result.Output))
        {
            log.Write("  " + result.Output.Trim().Replace(Environment.NewLine, Environment.NewLine + "  "));
        }

        return result;
    }

    private static void RunChecked(SetupLog log, string file, params string[] arguments)
    {
        var result = Run(log, file, arguments);
        if (!result.Success)
        {
            throw new InvalidOperationException($"{file} {arguments.FirstOrDefault()} failed (exit code {result.ExitCode}).");
        }
    }

    private sealed class SetupLog
    {
        private readonly StringBuilder _text = new();

        public void Write(string line)
        {
            _text.AppendLine($"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}  {line}");
            Console.WriteLine(line);
        }

        public void Save(WebitPaths paths)
        {
            try
            {
                Directory.CreateDirectory(paths.LogsDir);
                File.AppendAllText(paths.SetupLogFile, _text.ToString());
            }
            catch
            {
                // Best effort.
            }
        }
    }
}
