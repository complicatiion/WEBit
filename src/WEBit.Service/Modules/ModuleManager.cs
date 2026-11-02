using System.Diagnostics;
using Microsoft.Extensions.Logging;
using WEBit.Contracts;
using WEBit.Core.Diagnostics;
using WEBit.Core.Models;
using WEBit.Core.Net;
using WEBit.Core.Storage;
using WEBit.Service.Engine;

namespace WEBit.Service.Modules;

/// <summary>
/// Modules next to the web server: Mailpit, PHP (FastCGI) and MariaDB run as processes;
/// Node.js and Python (uv) are runtimes for the app sites (processes in <see cref="Apps.AppManager"/>).
/// </summary>
public sealed partial class ModuleManager : IDisposable
{
    private readonly WebitPaths _paths;
    private readonly ProcessJob _job;
    private readonly ILogger<ModuleManager> _log;
    private readonly ManagedProcess _mailpit;
    private readonly ManagedProcess _php;
    private readonly ManagedProcess _mariadb;
    private readonly MariaDb _db;
    private readonly object _versionLock = new();
    private readonly Dictionary<string, string?> _versions = new(StringComparer.OrdinalIgnoreCase);
    private WebitSettings _settings = new();
    private CancellationTokenSource? _upgradeCts;

    /// <summary>Port the running MariaDB server was started with (shutdown must use it, not a changed setting).</summary>
    private volatile int _mariaDbPort;

    /// <summary>First start: the data folder is being created in the background.</summary>
    private volatile bool _mariaDbInitializing;

    /// <summary>Creating the data folder failed; not retried until restart or switch.</summary>
    private LocalizedMessage? _mariaDbInitFailure;

    /// <summary>Serializes start/stop of MariaDB (the background setup starts it without the SiteManager gate).</summary>
    private readonly object _mariaDbLock = new();

    /// <summary>The server should run once the background setup is done (cleared by every stop).</summary>
    private bool _mariaDbWanted;

    public ModuleManager(WebitPaths paths, ProcessJob job, ILogger<ModuleManager> log)
    {
        _paths = paths;
        _job = job;
        _log = log;
        _db = new MariaDb(paths, log);
        _mailpit = new ManagedProcess("Mailpit", MailpitLogFile, job, log);
        _mailpit.StateChanged += () => Changed?.Invoke();
        _php = new ManagedProcess("PHP", Path.Combine(paths.LogsDir, "php.log"), job, log);
        _php.StateChanged += OnPhpStateChanged;
        _mariadb = new ManagedProcess("MariaDB", MariaDbLogFile, job, log)
        {
            // InnoDB: shut down cleanly instead of killing the server.
            GracefulStop = () => MariaDbExecutable is { } exe && _mariaDbPort > 0 && _db.Shutdown(exe, _mariaDbPort)
        };
        _mariadb.StateChanged += () => Changed?.Invoke();
    }

    public event Action? Changed;

    /// <summary>PHP went up or down: the web server config must route *.php differently.</summary>
    public event Action? PhpAvailabilityChanged;

    private bool _phpWasAvailable;

    private void OnPhpStateChanged()
    {
        var available = _php.State is ModuleState.Running or ModuleState.Starting;
        if (available != _phpWasAvailable)
        {
            _phpWasAvailable = available;
            PhpAvailabilityChanged?.Invoke();
        }

        Changed?.Invoke();
    }

    public MariaDb Database => _db;

    public string MailpitLogFile => Path.Combine(_paths.LogsDir, "mailpit.log");

    public string MariaDbLogFile => Path.Combine(_paths.LogsDir, "mariadb.log");

    public string PhpLogFile => _paths.PhpErrorLogFile;

    public string? MailpitExecutable => FindModule(ModuleIds.Mailpit, "mailpit", "mailpit");

    public string? PhpExecutable => FindModule(ModuleIds.Php, "php", "php-cgi");

    public string? NodeExecutable => FindModule(ModuleIds.Node, "node", "node");

    /// <summary>uv (Python module); Python versions themselves are installed by uv into data\runtimes\python.</summary>
    public string? UvExecutable => FindModule(ModuleIds.Python, "uv", "uv");

    public string? MariaDbExecutable => FindModule(ModuleIds.MariaDb, "mariadb", Path.Combine("bin", "mariadbd"), Path.Combine("bin", "mysqld"));

    /// <summary>PHP command line (php.exe next to php-cgi.exe): runs Composer and WP-CLI.</summary>
    public string? PhpCliExecutable
    {
        get
        {
            if (PhpExecutable is not { } cgi)
            {
                return null;
            }

            var cli = Path.Combine(Path.GetDirectoryName(cgi)!, OperatingSystem.IsWindows() ? "php.exe" : "php");
            return File.Exists(cli) ? cli : null;
        }
    }

    public string? ComposerPhar => FindFile(ModuleIds.Composer, "composer.phar");

    public string? WpCliPhar => FindFile(ModuleIds.WpCli, "wp-cli.phar");

    /// <summary>Adminer, downloaded as index.php (front controller of the internal Adminer site).</summary>
    public string? AdminerFile => FindFile(ModuleIds.Adminer, "index.php");

    /// <summary>Executable or main file of a module (for the dependencies page and the module cards).</summary>
    public string? ExecutableOf(string id) => id switch
    {
        ModuleIds.Mailpit => MailpitExecutable,
        ModuleIds.Php => PhpExecutable,
        ModuleIds.Node => NodeExecutable,
        ModuleIds.Python => UvExecutable,
        ModuleIds.MariaDb => MariaDbExecutable,
        ModuleIds.Composer => ComposerPhar,
        ModuleIds.WpCli => WpCliPhar,
        ModuleIds.Adminer => AdminerFile,
        ModuleIds.Xdebug => XdebugFiles().FirstOrDefault(),
        _ when ModuleIds.IsPhpVersion(id) => FindModule(id, id, "php-cgi"),
        _ => null
    };

    /// <summary>Address of the Adminer site: 127.0.0.1, or the default network address when allowed in the settings.</summary>
    public static string AdminerAddress(WebitSettings settings) =>
        !settings.AdminerNetworkAccess ? "127.0.0.1"
        : settings.DefaultBindAddress is { } address && !Endpoint.IsWildcard(address) ? address
        : HostNetwork.FirstIPv4() ?? "127.0.0.1";

    /// <summary>Adminer site can run: switched on, downloaded and PHP available.</summary>
    public bool IsAdminerAvailable(WebitSettings settings) =>
        settings.IsModuleEnabled(ModuleIds.Adminer) && AdminerFile is not null && IsPhpAvailable(settings);

    private string? FindFile(string id, string file)
    {
        var path = Path.Combine(_paths.ModuleDir(id), file);
        return File.Exists(path) ? path : null;
    }

    /// <summary>After a download: read the version again.</summary>
    public void InvalidateVersion(string id)
    {
        if (id == ModuleIds.MariaDb)
        {
            // New files: try the data folder setup again.
            _mariaDbInitFailure = null;
        }

        lock (_versionLock)
        {
            _versions.Remove(id);
        }
    }

    /// <summary>
    /// Installed version, also for switched-off modules: .version of the download, otherwise asked once
    /// from the executable (cached until <see cref="InvalidateVersion"/>).
    /// </summary>
    public string? VersionOf(string id)
    {
        lock (_versionLock)
        {
            if (_versions.TryGetValue(id, out var cached))
            {
                return cached;
            }
        }

        if (ExecutableOf(id) is not { } exe)
        {
            return null;
        }

        // Downloaded modules: only the .version of the download – never run an executable from the data folder
        // just to show its version (bin\ and the repository runtime are not writable for users).
        var downloaded = exe.StartsWith(_paths.ModuleDir(id), StringComparison.OrdinalIgnoreCase);
        var version = ReadVersionFile(id, exe) ?? (downloaded ? null : id switch
        {
            ModuleIds.Php => ReadVersion(exe, "-v", t => t.Length > 2 && char.IsDigit(t[0]) && t.Contains('.')),
            _ when ModuleIds.IsPhpVersion(id) => ReadVersion(exe, "-v", t => t.Length > 2 && char.IsDigit(t[0]) && t.Contains('.')),
            ModuleIds.Mailpit => ReadVersion(exe, "version", t => t.Length > 1 && t[0] == 'v' && char.IsDigit(t[1])),
            ModuleIds.Node => ReadVersion(exe, "-v", t => t.Length > 1 && t[0] == 'v' && char.IsDigit(t[1]))?.TrimStart('v'),
            ModuleIds.Python => ReadVersion(exe, "--version", t => t.Length > 2 && char.IsDigit(t[0]) && t.Contains('.')),
            ModuleIds.MariaDb => ReadVersion(exe, "--version", t => t.Length > 2 && char.IsDigit(t[0]) && t.Contains('.'))?.Split('-')[0],
            _ => null
        });

        lock (_versionLock)
        {
            _versions[id] = version;
        }

        return version;
    }

    /// <summary>Stops a module process before its files are replaced (apps: see AppManager).</summary>
    public void StopModule(string id)
    {
        switch (id)
        {
            case ModuleIds.Php:
                _php.Stop();
                StopPool(k => k.Branch == PhpDefaultBranch);
                break;
            case ModuleIds.Xdebug:
                StopPool(k => k.Xdebug);
                break;
            case ModuleIds.Mailpit:
                _mailpit.Stop();
                break;
            case var _ when ModuleIds.PhpBranchOf(id) is { } branch:
                StopPool(k => k.Branch == branch);
                break;
            case ModuleIds.MariaDb:
                lock (_mariaDbLock)
                {
                    _mariaDbWanted = false;
                    _upgradeCts?.Cancel();
                    _mariadb.Stop();
                }

                break;
        }
    }

    /// <summary>PHP is switched on, installed and running; Caddy then routes *.php to FastCGI.</summary>
    public bool IsPhpAvailable(WebitSettings settings) =>
        settings.IsModuleEnabled(ModuleIds.Php) && PhpExecutable is not null
        && _php.State is ModuleState.Running or ModuleState.Starting;

    /// <summary>Runtime for app sites: switched on and installed.</summary>
    public bool IsRuntimeReady(WebitSettings settings, string id) =>
        settings.IsModuleEnabled(id) && ExecutableOf(id) is not null;

    public bool IsMariaDbRunning => _mariadb.State is ModuleState.Running or ModuleState.Starting;

    /// <summary>Module whose files are being replaced by a download: leave it alone until it is done.</summary>
    public Func<string, bool>? IsUpdating { get; set; }

    private bool Updating(string id) => IsUpdating?.Invoke(id) == true;

    /// <summary>Starts or stops modules according to the switches.</summary>
    public void Apply(WebitSettings settings)
    {
        _settings = settings;
        ConfigureLogs(settings.Logging);
        ApplyMailpit(settings);
        ApplyPhp(settings);
        ApplyMariaDb(settings);
    }

    public void Restart(WebitSettings settings, string id)
    {
        if (Updating(id))
        {
            return;
        }

        _settings = settings;
        switch (id.ToLowerInvariant())
        {
            case ModuleIds.Php:
                _php.Stop();

                // Additional processes too (php.ini settings); started again by the next SetPhpDemand.
                StopPool(_ => true);
                ApplyPhp(settings);
                break;
            case ModuleIds.Mailpit:
                _mailpit.Stop();
                ApplyMailpit(settings);
                break;
            case ModuleIds.MariaDb:
                StopModule(ModuleIds.MariaDb);
                _mariaDbInitFailure = null;
                ApplyMariaDb(settings);
                break;
            default:
                throw new NotSupportedException($"Module '{id}' cannot be restarted.");
        }
    }

    public void StopAll()
    {
        _mailpit.Stop();
        _php.Stop();
        StopPool(_ => true);
        StopModule(ModuleIds.MariaDb);
    }

    private void ApplyMailpit(WebitSettings settings)
    {
        if (Updating(ModuleIds.Mailpit))
        {
            return;
        }

        var exe = MailpitExecutable;
        if (!Prepare(_mailpit, settings.IsModuleEnabled(ModuleIds.Mailpit), exe, "Mailpit", "127.0.0.1", settings.MailpitSmtpPort, settings.MailpitUiPort))
        {
            return;
        }

        _ = VersionOf(ModuleIds.Mailpit);
        var dataDir = Path.Combine(_paths.DataRoot, "mailpit");
        Directory.CreateDirectory(dataDir);

        var psi = new ProcessStartInfo(Alias(exe!, "WEBit-Mailpit")) { WorkingDirectory = dataDir };
        psi.ArgumentList.Add("--listen");
        psi.ArgumentList.Add($"127.0.0.1:{settings.MailpitUiPort}");
        psi.ArgumentList.Add("--smtp");
        psi.ArgumentList.Add($"127.0.0.1:{settings.MailpitSmtpPort}");
        psi.ArgumentList.Add("--database");
        psi.ArgumentList.Add(Path.Combine(dataDir, "mailpit.db"));
        _mailpit.Start(psi);
    }

    private void ApplyPhp(WebitSettings settings)
    {
        if (Updating(ModuleIds.Php))
        {
            return;
        }

        if (!settings.IsModuleEnabled(ModuleIds.Php))
        {
            StopPool(_ => true);
        }

        var exe = PhpExecutable;
        if (!Prepare(_php, settings.IsModuleEnabled(ModuleIds.Php), exe, "PHP", "127.0.0.1", settings.PhpFastCgiPort))
        {
            return;
        }

        // The official Windows build needs the Visual C++ runtime (vcruntime140.dll).
        if (OperatingSystem.IsWindows() && !HasVcRuntime(exe!))
        {
            _php.Fail(LocalizedMessage.Of(MessageCodes.VcRuntimeMissing, VcRedistInstaller ?? "vc_redist.x64.exe"));
            return;
        }

        string ini;
        try
        {
            ini = PhpIni.Write(_paths, settings, exe!);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not write php.ini.");
            _php.Fail(LocalizedMessage.Of(MessageCodes.ModuleStartFailed, ex.Message));
            return;
        }

        _ = VersionOf(ModuleIds.Php);
        var psi = new ProcessStartInfo(Alias(exe!, "WEBit-PHP")) { WorkingDirectory = Path.GetDirectoryName(exe!)! };
        psi.ArgumentList.Add("-b");
        psi.ArgumentList.Add($"127.0.0.1:{settings.PhpFastCgiPort}");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(ini);

        // php-cgi exits after 500 requests by default; WEBit keeps one process.
        psi.Environment["PHP_FCGI_MAX_REQUESTS"] = "0";
        psi.Environment["PHPRC"] = Path.GetDirectoryName(ini)!;
        _php.Start(psi);
    }

    private void ApplyMariaDb(WebitSettings settings)
    {
        lock (_mariaDbLock)
        {
            ApplyMariaDbLocked(settings);
        }
    }

    private void ApplyMariaDbLocked(WebitSettings settings)
    {
        if (Updating(ModuleIds.MariaDb))
        {
            return;
        }

        if (_mariaDbInitializing)
        {
            // Started when the setup is done.
            _mariaDbWanted = settings.IsModuleEnabled(ModuleIds.MariaDb);
            return;
        }

        var exe = MariaDbExecutable;
        var enabled = settings.IsModuleEnabled(ModuleIds.MariaDb);
        if (!enabled)
        {
            _mariaDbInitFailure = null;
        }

        var bind = settings.MariaDbNetworkAccess ? "0.0.0.0" : "127.0.0.1";
        if (!Prepare(_mariadb, enabled, exe, "MariaDB", bind, settings.MariaDbPort))
        {
            return;
        }

        if (_mariaDbInitFailure is { } failure)
        {
            _mariadb.Fail(failure);
            return;
        }

        if (!_db.IsInitialized)
        {
            // mariadb-install-db takes a while: never under the SiteManager gate.
            _mariaDbInitializing = true;
            _mariaDbWanted = true;
            Changed?.Invoke();
            _ = Task.Run(() =>
            {
                LocalizedMessage? problem;
                try
                {
                    problem = _db.EnsureInitialized(exe!, settings);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "MariaDB data folder could not be created.");
                    problem = LocalizedMessage.Of(MessageCodes.DbInitFailed, ex.Message);
                }

                lock (_mariaDbLock)
                {
                    _mariaDbInitFailure = problem;
                    _mariaDbInitializing = false;
                    if (problem is not null)
                    {
                        _mariadb.Fail(problem);
                    }
                    else if (_mariaDbWanted)
                    {
                        // Not stopped meanwhile (service stop, update, switch): start with the current settings.
                        ApplyMariaDbLocked(_settings);
                    }
                }

                Changed?.Invoke();
            });
            return;
        }

        string ini;
        try
        {
            if (_db.EnsureInitialized(exe!, settings) is { } problem)
            {
                _mariadb.Fail(problem);
                return;
            }

            ini = _db.WriteIni(exe!, settings);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "MariaDB could not be prepared.");
            _mariadb.Fail(LocalizedMessage.Of(MessageCodes.ModuleStartFailed, ex.Message));
            return;
        }

        var version = VersionOf(ModuleIds.MariaDb);
        var psi = new ProcessStartInfo(Alias(exe!, "WEBit-MariaDB")) { WorkingDirectory = Path.GetDirectoryName(exe!)! };

        // --defaults-file must be the first option.
        psi.ArgumentList.Add($"--defaults-file={ini}");
        psi.ArgumentList.Add("--console");
        _mariaDbPort = settings.MariaDbPort;
        _mariadb.Start(psi);

        _upgradeCts?.Cancel();
        _upgradeCts = new CancellationTokenSource();
        _ = _db.UpgradeIfNeededAsync(exe!, settings, version, _upgradeCts.Token);
    }

    /// <summary>Applies the log switches to the module processes (also while they run).</summary>
    public void ConfigureLogs(LogSettings logging)
    {
        foreach (var module in new[] { _mailpit, _php, _mariadb }.Concat(PoolProcesses()))
        {
            module.LogEnabled = logging.ModuleLogs;
            module.MaxLogBytes = logging.MaxSizeMb * 1024L * 1024;
        }
    }

    /// <summary>Common checks: switch, executable, ports. Returns true when the module must be started.</summary>
    private static bool Prepare(ManagedProcess module, bool enabled, string? exe, string name, string address, params int[] ports)
    {
        if (!enabled)
        {
            if (module.State != ModuleState.Stopped)
            {
                module.Stop();
            }

            return false;
        }

        if (exe is null)
        {
            module.Fail(LocalizedMessage.Of(MessageCodes.ModuleNotInstalled, name));
            return false;
        }

        if (module.State is ModuleState.Running or ModuleState.Starting)
        {
            return false;
        }

        foreach (var port in ports)
        {
            if (!PortProbe.CanBind(address, port, out _))
            {
                module.Fail(LocalizedMessage.Of(MessageCodes.PortInUse, $"{address}:{port}", "?", "?"));
                return false;
            }
        }

        return true;
    }

    /// <param name="appCount">Running apps per runtime (node, python).</param>
    public IEnumerable<ModuleStatus> Snapshot(WebitSettings settings, Func<string, int> appCount)
    {
        yield return Status(ModuleIds.Mailpit, "Mailpit", _mailpit, MailpitExecutable, settings, VersionOf(ModuleIds.Mailpit),
            $"SMTP 127.0.0.1:{settings.MailpitSmtpPort}", $"http://127.0.0.1:{settings.MailpitUiPort}/", "MIT");

        var php = Status(ModuleIds.Php, "PHP", _php, PhpExecutable, settings, VersionOf(ModuleIds.Php),
            $"FastCGI 127.0.0.1:{settings.PhpFastCgiPort}" + PoolEndpoints(), null, "PHP License 3.01");
        if (php.Message?.Code == MessageCodes.VcRuntimeMissing)
        {
            php.VcRedistInstaller = VcRedistInstaller;
        }

        yield return php;

        var mariadb = Status(ModuleIds.MariaDb, "MariaDB", _mariadb, MariaDbExecutable, settings, VersionOf(ModuleIds.MariaDb),
            $"{(settings.MariaDbNetworkAccess ? "0.0.0.0" : "127.0.0.1")}:{settings.MariaDbPort}", null, "GPL-2.0");
        if (_mariaDbInitializing && mariadb.Enabled)
        {
            mariadb.State = ModuleState.Starting;
        }

        yield return mariadb;

        yield return Runtime(ModuleIds.Node, "Node.js", NodeExecutable, settings, "MIT", appCount(ModuleIds.Node));
        yield return Runtime(ModuleIds.Python, "Python (uv)", UvExecutable, settings, "uv: MIT / Apache-2.0 · Python: PSF", appCount(ModuleIds.Python));

        // Adminer: switch = internal site on 127.0.0.1:AdminerPort.
        var adminer = Runtime(ModuleIds.Adminer, "Adminer", AdminerFile, settings, "Apache-2.0 / GPL-2.0", 0);
        adminer.Endpoint = $"{AdminerAddress(settings)}:{settings.AdminerPort}";
        adminer.Url = $"http://{AdminerAddress(settings)}:{settings.AdminerPort}/";
        if (adminer.State == ModuleState.Ready && !IsPhpAvailable(settings))
        {
            adminer.State = ModuleState.Error;
            adminer.Message = LocalizedMessage.Of(MessageCodes.PhpUnavailable);
        }
        else if (adminer.State == ModuleState.Ready)
        {
            adminer.State = ModuleState.Running;
        }

        yield return adminer;

        // Tools without switch: available once downloaded.
        foreach (var (id, name, file, license) in new[]
        {
            (ModuleIds.Composer, "Composer", ComposerPhar, "MIT"),
            (ModuleIds.WpCli, "WP-CLI", WpCliPhar, "MIT"),
            (ModuleIds.Xdebug, "Xdebug", XdebugFiles().FirstOrDefault(), "Xdebug License")
        })
        {
            var tool = Runtime(id, name, file, settings, license, 0);
            tool.Enabled = file is not null;
            tool.CanToggle = false;
            tool.State = file is null ? ModuleState.NotInstalled : PhpCliExecutable is null ? ModuleState.Error : ModuleState.Ready;
            tool.Message = file is not null && PhpCliExecutable is null ? LocalizedMessage.Of(MessageCodes.ModuleNotInstalled, "PHP") : null;
            tool.Endpoint = id == ModuleIds.Xdebug ? $"IDE 127.0.0.1:{settings.Php.XdebugPort}" : "PHP CLI";
            yield return tool;
        }
    }

    private ModuleStatus Runtime(string id, string name, string? exe, WebitSettings settings, string license, int apps)
    {
        var enabled = settings.IsModuleEnabled(id);
        return new ModuleStatus
        {
            Id = id,
            Name = name,
            Enabled = enabled,
            CanToggle = exe is not null || enabled,
            State = exe is null ? ModuleState.NotInstalled : !enabled ? ModuleState.Disabled : ModuleState.Ready,
            Version = VersionOf(id),
            ExecutablePath = exe,
            Endpoint = $"{apps} App(s)",
            License = license
        };
    }

    private static ModuleStatus Status(string id, string name, ManagedProcess module, string? exe, WebitSettings settings,
        string? version, string endpoint, string? url, string license)
    {
        var enabled = settings.IsModuleEnabled(id);
        return new ModuleStatus
        {
            Id = id,
            Name = name,
            Enabled = enabled,
            CanToggle = exe is not null || enabled,
            State = exe is null ? ModuleState.NotInstalled
                : !enabled ? ModuleState.Disabled
                : module.State,
            Version = version,
            ExecutablePath = exe,
            Endpoint = endpoint,
            Url = url,
            License = license,
            Message = enabled ? module.Message : null
        };
    }

    /// <summary>Downloaded VC++ runtime installer (data\modules\vcredist), if present.</summary>
    public string? VcRedistInstaller => FindModule(ModuleDownloader.VcRedistId, "vcredist", "vc_redist.x64");

    public static bool HasSystemVcRuntime() =>
        File.Exists(Path.Combine(Environment.SystemDirectory, "vcruntime140.dll"))
        && File.Exists(Path.Combine(Environment.SystemDirectory, "vcruntime140_1.dll"));

    private static bool HasVcRuntime(string exe) =>
        File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "vcruntime140.dll")) || HasSystemVcRuntime();

    /// <summary>
    /// Module executable, first match: downloaded (data\modules\&lt;id&gt;), provided manually or bundled
    /// next to the service (bin\&lt;folder&gt;), repository runtime\&lt;folder&gt; (development).
    /// </summary>
    private string? FindModule(string id, string folder, params string[] exeNames)
    {
        var roots = new List<string>
        {
            _paths.ModuleDir(id),
            Path.Combine(_paths.BaseDirectory, "bin", folder)
        };

        // Development only: runtime\<folder> of the repository (folder with WEBit.slnx). Never arbitrary
        // parent folders of an installation – a user could plant an executable there (C:\runtime\…).
        var dir = new DirectoryInfo(_paths.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "WEBit.slnx")))
            {
                roots.Add(Path.Combine(dir.FullName, "runtime", folder));
                break;
            }
        }

        foreach (var root in roots)
        {
            foreach (var name in exeNames)
            {
                var file = Path.Combine(root, OperatingSystem.IsWindows() ? name + ".exe" : name);
                if (File.Exists(file))
                {
                    return file;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Copy of the module executable named WEBit-&lt;Module&gt;.exe in the same folder, so the processes are easy to
    /// recognize in Task Manager. Falls back to the original when the folder is read-only.
    /// </summary>
    public string Alias(string exe, string alias)
    {
        if (!OperatingSystem.IsWindows())
        {
            return exe;
        }

        try
        {
            var target = Path.Combine(Path.GetDirectoryName(exe)!, alias + ".exe");
            var source = new FileInfo(exe);
            var current = new FileInfo(target);
            if (!current.Exists || current.Length != source.Length || current.LastWriteTimeUtc != source.LastWriteTimeUtc)
            {
                File.Copy(exe, target, overwrite: true);
                File.SetLastWriteTimeUtc(target, source.LastWriteTimeUtc);
            }

            return target;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "No process alias for {Exe}.", exe);
            return exe;
        }
    }

    /// <summary>.version written by the downloader (module folder, or next to the executable).</summary>
    private string? ReadVersionFile(string id, string exe)
    {
        foreach (var file in new[] { Path.Combine(_paths.ModuleDir(id), ".version"), Path.Combine(Path.GetDirectoryName(exe)!, ".version") })
        {
            try
            {
                if (exe.StartsWith(_paths.ModuleDir(id), StringComparison.OrdinalIgnoreCase) && File.Exists(file))
                {
                    return File.ReadAllText(file).Trim();
                }
            }
            catch (Exception)
            {
                // Unreadable: ask the executable.
            }
        }

        return null;
    }

    private string? ReadVersion(string exe, string argument, Func<string, bool> isVersion)
    {
        try
        {
            var run = ProcessRunner.Run(exe, [argument], TimeSpan.FromSeconds(10));
            return run.Output.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(isVersion);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not read version of {Exe}.", exe);
            return null;
        }
    }

    public void Dispose()
    {
        _mailpit.Dispose();
        _php.Dispose();
        StopPool(_ => true);
        _mariadb.Dispose();
    }
}
