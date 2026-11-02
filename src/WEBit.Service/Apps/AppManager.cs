using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WEBit.Contracts;
using WEBit.Core.Apps;
using WEBit.Core.Models;
using WEBit.Core.Net;
using WEBit.Core.Serialization;
using WEBit.Core.Storage;
using WEBit.Service.Engine;
using WEBit.Service.Modules;

namespace WEBit.Service.Apps;

/// <summary>
/// One process per Node.js/Python site: started from the detected or own command on the site's internal
/// port, supervised (dev mode: stays down after a crash; continuous operation: restarts), optionally
/// restarted on file changes. Caddy proxies the site to 127.0.0.1:port.
/// </summary>
public sealed class AppManager : IDisposable
{
    private readonly WebitPaths paths;
    private readonly ProcessJob job;
    private readonly ModuleManager modules;
    private readonly ILogger<AppManager> log;
    private readonly object _sync = new();
    private readonly Dictionary<string, AppInstance> _apps = new(StringComparer.OrdinalIgnoreCase);
    private LogSettings _logging = new();

    public AppManager(WebitPaths paths, ProcessJob job, ModuleManager modules, ILogger<AppManager> log)
    {
        this.paths = paths;
        this.job = job;
        this.modules = modules;
        this.log = log;
    }

    /// <summary>App state changed (started, ready, crashed, installing).</summary>
    public event Action? Changed;

    /// <summary>
    /// Runs exactly the given app sites (enabled and accepted by the web server); others are stopped.
    /// Changed definitions or runtimes restart the app. Caller holds the SiteManager gate.
    /// </summary>
    public void Sync(IEnumerable<SiteDefinition> sites, WebitSettings settings)
    {
        _logging = settings.Logging;
        var wanted = sites.Where(s => s.IsApp).ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        var fingerprints = wanted.Values.ToDictionary(s => s.Id, s => Fingerprint(s, settings), StringComparer.OrdinalIgnoreCase);

        List<AppInstance> stale;
        var created = new List<AppInstance>();
        lock (_sync)
        {
            stale = _apps.Values.Where(a => !fingerprints.TryGetValue(a.SiteId, out var f) || f != a.Fingerprint).ToList();
            foreach (var app in stale)
            {
                _apps.Remove(app.SiteId);
            }

            foreach (var site in wanted.Values)
            {
                if (!_apps.ContainsKey(site.Id))
                {
                    var app = new AppInstance(this, site.Clone(), fingerprints[site.Id], settings);
                    _apps[site.Id] = app;
                    created.Add(app);
                }
            }
        }

        foreach (var app in stale)
        {
            app.Dispose();
        }

        foreach (var app in created)
        {
            app.Start();
        }

        if (stale.Count > 0 || created.Count > 0)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>State for the site card; null = no app (not an app site or not wanted).</summary>
    public (SiteRunState State, LocalizedMessage? Message)? StateOf(string siteId)
    {
        lock (_sync)
        {
            return _apps.TryGetValue(siteId, out var app) ? app.State : null;
        }
    }

    public string? CommandOf(string siteId)
    {
        lock (_sync)
        {
            return _apps.TryGetValue(siteId, out var app) ? app.Command : null;
        }
    }

    /// <summary>Apps of a runtime (node, python) that are running or starting.</summary>
    public int CountFor(string moduleId)
    {
        lock (_sync)
        {
            return _apps.Values.Count(a => a.ModuleId == moduleId && a.State.State is SiteRunState.Running or SiteRunState.Starting);
        }
    }

    public bool Restart(string siteId)
    {
        AppInstance? app;
        lock (_sync)
        {
            _apps.TryGetValue(siteId, out app);
        }

        app?.Restart();
        return app is not null;
    }

    public void RestartModule(string moduleId)
    {
        foreach (var app in Of(moduleId))
        {
            app.Restart();
        }
    }

    /// <summary>Stops and forgets the apps of a runtime (its files are replaced); the next Sync starts them again.</summary>
    public void StopModule(string moduleId)
    {
        List<AppInstance> stopped;
        lock (_sync)
        {
            stopped = _apps.Values.Where(a => a.ModuleId == moduleId).ToList();
            foreach (var app in stopped)
            {
                _apps.Remove(app.SiteId);
            }
        }

        foreach (var app in stopped)
        {
            app.Dispose();
        }

        Changed?.Invoke();
    }

    /// <summary>npm install / uv sync for a running app site, then the app starts again. Null = already installing.</summary>
    public async Task<bool?> InstallAsync(string siteId, CancellationToken ct)
    {
        AppInstance? app;
        lock (_sync)
        {
            _apps.TryGetValue(siteId, out app);
        }

        if (app is null)
        {
            return false;
        }

        return await app.InstallAsync(ct);
    }

    public void ConfigureLogs(LogSettings logging)
    {
        _logging = logging;
        lock (_sync)
        {
            foreach (var app in _apps.Values)
            {
                app.ConfigureLogs(logging);
            }
        }
    }

    public void StopAll()
    {
        List<AppInstance> all;
        lock (_sync)
        {
            all = _apps.Values.ToList();
            _apps.Clear();
        }

        // Each stop may wait for the process tree: stop them together.
        Parallel.ForEach(all, app => app.Dispose());
    }

    public void Dispose() => StopAll();

    private List<AppInstance> Of(string moduleId)
    {
        lock (_sync)
        {
            return _apps.Values.Where(a => a.ModuleId == moduleId).ToList();
        }
    }

    private void OnChanged() => Changed?.Invoke();

    /// <summary>Everything that requires a new process when it changes.</summary>
    private string Fingerprint(SiteDefinition site, WebitSettings settings)
    {
        var module = site.AppModule!;
        var exe = modules.ExecutableOf(module);
        var stamp = exe is not null && File.Exists(exe) ? File.GetLastWriteTimeUtc(exe).Ticks : 0;
        return JsonSerializer.Serialize(new
        {
            site.Type,
            site.Root,
            site.App,
            Enabled = settings.IsModuleEnabled(module),
            Exe = exe,
            Stamp = stamp
        }, WebitJson.Wire);
    }

    // ================================================================== One app

    private sealed class AppInstance : IDisposable
    {
        private static readonly string[] IgnoredSegments =
        [
            "node_modules", ".git", ".svn", ".venv", "venv", "__pycache__", ".next", ".nuxt", ".svelte-kit", ".astro",
            "dist", "build", ".cache", "coverage", ".pytest_cache", ".mypy_cache", ".ruff_cache", ".idea", ".vs", ".vscode", ".turbo",
            "uploads", "media", "storage", "logs", "tmp", "instance"
        ];

        /// <summary>Data files the app writes itself (SQLite, journals): no restart.</summary>
        private static readonly string[] IgnoredSuffixes =
        [
            ".log", ".tmp", ".swp", "~", ".sqlite", ".sqlite3", ".db", "-journal", "-wal", "-shm", ".pyc", "next-env.d.ts"
        ];

        /// <summary>An app that never answers on its port is reported after this time.</summary>
        private static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(5);

        private readonly AppManager _owner;
        private readonly SiteDefinition _site;
        private readonly WebitSettings _settings;
        private readonly ManagedProcess _process;
        private readonly CancellationTokenSource _cts = new();
        private readonly object _lock = new();
        private FileSystemWatcher? _watcher;
        private Timer? _debounce;
        private LocalizedMessage? _problem;
        private volatile bool _ready;
        private bool _installing;
        private bool _disposed;
        private int _generation;
        private int _probingGeneration;
        private volatile bool _notListening;

        public AppInstance(AppManager owner, SiteDefinition site, string fingerprint, WebitSettings settings)
        {
            _owner = owner;
            _site = site;
            _settings = settings;
            Fingerprint = fingerprint;
            _process = new ManagedProcess(site.Name, owner.paths.SiteAppLog(site.Id), owner.job, owner.log)
            {
                AutoRestart = site.App.KeepAlive
            };
            _process.StateChanged += OnProcessStateChanged;
            ConfigureLogs(owner._logging);
        }

        public string SiteId => _site.Id;

        public string ModuleId => _site.AppModule!;

        public string Fingerprint { get; }

        public string? Command { get; private set; }

        public (SiteRunState State, LocalizedMessage? Message) State
        {
            get
            {
                if (_problem is not null)
                {
                    return (SiteRunState.Error, _problem);
                }

                if (_installing)
                {
                    return (SiteRunState.Starting, LocalizedMessage.Of(MessageCodes.AppInstalling));
                }

                if (_notListening && _process.State == ModuleState.Running)
                {
                    return (SiteRunState.Error, LocalizedMessage.Of(MessageCodes.AppNotListening, $"127.0.0.1:{_site.App.Port}"));
                }

                return _process.State switch
                {
                    ModuleState.Error => (SiteRunState.Error, _process.Message is { Code: MessageCodes.ModuleExited } exited
                        ? LocalizedMessage.Of(MessageCodes.AppExited, exited.Args.FirstOrDefault() ?? "?")
                        : _process.Message),
                    ModuleState.Running when _ready => (SiteRunState.Running, null),
                    ModuleState.Running or ModuleState.Starting => (SiteRunState.Starting, null),
                    _ => (SiteRunState.Stopped, null)
                };
            }
        }

        public void ConfigureLogs(LogSettings logging)
        {
            _process.LogEnabled = logging.ModuleLogs;
            _process.MaxLogBytes = logging.MaxSizeMb * 1024L * 1024;
        }

        public void Start()
        {
            var modules = _owner.modules;
            if (!modules.IsRuntimeReady(_settings, ModuleId))
            {
                Fail(LocalizedMessage.Of(MessageCodes.AppModuleMissing, ModuleId == ModuleIds.Node ? "Node.js" : "Python (uv)"));
                return;
            }

            if (_site.App.Port is < 1 or > 65535)
            {
                Fail(LocalizedMessage.Of(MessageCodes.AppNoPort));
                return;
            }

            var launch = AppCommandResolver.Resolve(_site, _site.App.Port);
            if (launch is null)
            {
                Fail(LocalizedMessage.Of(MessageCodes.AppNoEntry, _site.Root));
                return;
            }

            Command = launch.Display;
            StartWatcher();

            // Quick test: a Node.js project without node_modules gets npm install first.
            if (AppCommandResolver.NeedsNodeInstall(_site))
            {
                _ = InstallAsync(_cts.Token);
                return;
            }

            _ = LaunchSafeAsync(launch);
        }

        public void Restart()
        {
            if (_disposed || _installing)
            {
                return;
            }

            // Could not start before (folder, port, runtime): try again from the beginning.
            if (_problem is not null || Command is null)
            {
                _problem = null;
                Start();
                return;
            }

            _process.Stop();
            if (AppCommandResolver.Resolve(_site, _site.App.Port) is { } launch)
            {
                Command = launch.Display;
                _ = LaunchSafeAsync(launch);
            }
        }

        public async Task<bool?> InstallAsync(CancellationToken ct)
        {
            var install = AppCommandResolver.ResolveInstall(_site);
            if (install is null || _disposed)
            {
                return false;
            }

            lock (_lock)
            {
                if (_installing)
                {
                    return null;
                }

                _installing = true;
            }

            var ok = false;
            _process.Stop();
            _owner.OnChanged();
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
                linked.CancelAfter(TimeSpan.FromMinutes(20));
                ok = await RunToEndAsync(install, linked.Token);
            }
            catch (OperationCanceledException)
            {
                _process.Log("■ cancelled");
            }
            catch (Exception ex)
            {
                _process.Log("■ " + ex.Message);
                _owner.log.LogWarning(ex, "Installing packages for {Site} failed.", _site.Name);
            }
            finally
            {
                lock (_lock)
                {
                    _installing = false;
                }
            }

            if (!_disposed && AppCommandResolver.Resolve(_site, _site.App.Port) is { } launch)
            {
                Command = launch.Display;
                await LaunchSafeAsync(launch);
            }

            _owner.OnChanged();
            return ok;
        }

        /// <summary>Fire-and-forget launches: an error (runtime removed meanwhile …) ends up on the card.</summary>
        private async Task LaunchSafeAsync(AppLaunch launch)
        {
            try
            {
                await LaunchAsync(launch);
            }
            catch (Exception ex)
            {
                _owner.log.LogWarning(ex, "App {Site} could not be started.", _site.Name);
                if (!_disposed)
                {
                    Fail(LocalizedMessage.Of(MessageCodes.ModuleStartFailed, ex.Message));
                }
            }
        }

        private async Task LaunchAsync(AppLaunch launch)
        {
            var generation = Interlocked.Increment(ref _generation);
            _ready = false;
            _notListening = false;

            // A just stopped process may still hold the port for a moment.
            for (var attempt = 0; !PortProbe.CanBind(AppCommandResolver.Loopback, _site.App.Port, out _); attempt++)
            {
                if (attempt >= 10 || _disposed)
                {
                    Fail(LocalizedMessage.Of(MessageCodes.PortInUse, $"127.0.0.1:{_site.App.Port}", "?", "?"));
                    return;
                }

                await Task.Delay(500);
            }

            if (_disposed || generation != _generation)
            {
                return;
            }

            var psi = StartInfo(launch);
            _process.Log("▶ " + launch.Display);
            // The readiness probe starts from OnProcessStateChanged (Running).
            _process.Start(psi);
            _owner.OnChanged();
        }

        /// <summary>Running means: the app accepts connections on its port.</summary>
        private async Task ProbeAsync(int generation)
        {
            // One probe per launch.
            if (Interlocked.Exchange(ref _probingGeneration, generation) == generation)
            {
                return;
            }

            var until = DateTime.UtcNow + ReadyTimeout;
            try
            {
                while (!_cts.IsCancellationRequested && generation == _generation && !_ready)
                {
                    if (!_notListening && DateTime.UtcNow > until)
                    {
                        // Reported on the card; probing goes on slowly (first starts can take long).
                        _notListening = true;
                        _owner.log.LogWarning("App {Site} does not answer on 127.0.0.1:{Port}.", _site.Name, _site.App.Port);
                        _owner.OnChanged();
                    }

                    if (_process.State is not (ModuleState.Running or ModuleState.Starting))
                    {
                        return;
                    }

                    try
                    {
                        using var client = new TcpClient();
                        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                        attempt.CancelAfter(500);
                        await client.ConnectAsync(AppCommandResolver.Loopback, _site.App.Port, attempt.Token);
                        if (generation == _generation)
                        {
                            _notListening = false;
                            _ready = true;
                            _owner.log.LogInformation("App {Site} ready on 127.0.0.1:{Port}.", _site.Name, _site.App.Port);
                            _owner.OnChanged();
                        }

                        return;
                    }
                    catch (Exception) when (!_cts.IsCancellationRequested)
                    {
                        await Task.Delay(_notListening ? 5000 : 700, _cts.Token);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                Interlocked.CompareExchange(ref _probingGeneration, 0, generation);
            }
        }

        private void OnProcessStateChanged()
        {
            if (_process.State is not (ModuleState.Running or ModuleState.Starting))
            {
                _ready = false;
            }
            else if (_process.State == ModuleState.Running && !_ready)
            {
                // Restart after a crash (continuous operation): wait for the port again.
                _ = ProbeAsync(_generation);
            }

            _owner.OnChanged();
        }

        private void Fail(LocalizedMessage message)
        {
            _problem = message;
            _process.Log("■ " + message);
            _owner.OnChanged();
        }

        private async Task<bool> RunToEndAsync(AppLaunch launch, CancellationToken ct)
        {
            var psi = StartInfo(launch);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            _process.Log("▶ " + launch.Display);
            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _process.Log(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _process.Log(e.Data); };
            process.Start();
            _owner.job.Assign(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Already gone.
                }

                throw;
            }

            _process.Log($"■ exit {process.ExitCode}");
            return process.ExitCode == 0;
        }

        /// <summary>Program, arguments and environment: port, caches in the WEBit data folder, own variables last.</summary>
        private ProcessStartInfo StartInfo(AppLaunch launch)
        {
            var modules = _owner.modules;
            var paths = _owner.paths;
            ProcessStartInfo psi;
            string? toolDir;
            switch (launch.Program)
            {
                case AppProgram.Uv:
                {
                    var uv = modules.UvExecutable!;
                    toolDir = Path.GetDirectoryName(uv);
                    psi = new ProcessStartInfo(uv);
                    foreach (var arg in launch.Arguments)
                    {
                        psi.ArgumentList.Add(arg);
                    }

                    break;
                }

                case AppProgram.Shell:
                {
                    toolDir = Path.GetDirectoryName(modules.NodeExecutable!);
                    var shell = Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comspec ? comspec : "cmd.exe";
                    // Raw arguments: cmd.exe does not understand .NET argument escaping; /s keeps the quotes as written.
                    psi = new ProcessStartInfo(shell) { Arguments = $"/d /s /c \"{launch.CommandLine}\"" };
                    break;
                }

                default:
                {
                    var node = modules.NodeExecutable!;
                    toolDir = Path.GetDirectoryName(node);
                    psi = new ProcessStartInfo(modules.Alias(node, "WEBit-Node"));
                    if (launch.Program == AppProgram.Npm)
                    {
                        psi.ArgumentList.Add(Path.Combine(toolDir!, "node_modules", "npm", "bin", "npm-cli.js"));
                    }

                    foreach (var arg in launch.Arguments)
                    {
                        psi.ArgumentList.Add(arg);
                    }

                    break;
                }
            }

            psi.WorkingDirectory = _site.Root;
            var env = psi.Environment;
            var port = _site.App.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            env["PORT"] = port;
            env["HOST"] = AppCommandResolver.Loopback;
            env["NO_COLOR"] = "1";
            env["FORCE_COLOR"] = "0";
            env["BROWSER"] = "none";
            if (toolDir is not null)
            {
                env["PATH"] = toolDir + Path.PathSeparator + (env.TryGetValue("PATH", out var path) ? path : "");
            }

            if (ModuleId == ModuleIds.Node)
            {
                env["npm_config_cache"] = Path.Combine(paths.CacheDir, "npm");
                env["npm_config_update_notifier"] = "false";
                env["npm_config_fund"] = "false";
                env["npm_config_audit"] = "false";
            }
            else
            {
                env.Remove("VIRTUAL_ENV");
                env["UV_CACHE_DIR"] = Path.Combine(paths.CacheDir, "uv");
                env["UV_PYTHON_INSTALL_DIR"] = paths.PythonInstallDir;
                env["UV_NO_PROGRESS"] = "1";
                env["UV_LINK_MODE"] = "copy";
                env["PYTHONUNBUFFERED"] = "1";
                env["PYTHONIOENCODING"] = "utf-8";
            }

            foreach (var pair in _site.App.Environment)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key))
                {
                    env[pair.Key.Trim()] = pair.Value;
                }
            }

            return psi;
        }

        // ------------------------------------------------------------------ Restart on file changes

        private void StartWatcher()
        {
            if (_watcher is not null || !_site.App.RestartOnChange || !Directory.Exists(_site.Root))
            {
                return;
            }

            try
            {
                _debounce = new Timer(_ => Restart());
                _watcher = new FileSystemWatcher(_site.Root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                    InternalBufferSize = 64 * 1024
                };
                _watcher.Changed += OnFileEvent;
                _watcher.Created += OnFileEvent;
                _watcher.Deleted += OnFileEvent;
                _watcher.Renamed += OnFileEvent;
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                _owner.log.LogWarning(ex, "App {Site}: cannot watch {Root}.", _site.Name, _site.Root);
            }
        }

        private void OnFileEvent(object sender, FileSystemEventArgs e)
        {
            var relative = Path.GetRelativePath(_site.Root, e.FullPath);
            var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(s => IgnoredSegments.Contains(s, StringComparer.OrdinalIgnoreCase))
                || IgnoredSuffixes.Any(suffix => relative.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            // Editors save in several steps: restart once it is quiet.
            _debounce?.Change(TimeSpan.FromMilliseconds(800), Timeout.InfiniteTimeSpan);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cts.Cancel();
            _watcher?.Dispose();
            _debounce?.Dispose();
            _process.StateChanged -= OnProcessStateChanged;
            _process.Dispose();
        }
    }
}
