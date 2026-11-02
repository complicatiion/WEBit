using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using WEBit.Contracts;
using WEBit.Service.Apps;
using WEBit.Core.Analysis;
using WEBit.Core.Diagnostics;
using WEBit.Core.Engine;
using WEBit.Core.Models;
using WEBit.Core.Net;
using WEBit.Core.Sites;
using WEBit.Core.Storage;
using WEBit.Core.Urls;
using WEBit.Service.Engine;
using WEBit.Service.Live;
using WEBit.Service.Logging;
using WEBit.Service.Modules;
using WEBit.Service.Pipe;

namespace WEBit.Service.Sites;

/// <summary>Core of the service: sites, settings, engine and modules; pushes status to all clients.</summary>
public sealed partial class SiteManager : BackgroundService
{
    private readonly WebitPaths _paths;
    private readonly CaddyEngine _engine;
    private readonly ModuleManager _modules;
    private readonly AppManager _apps;
    private readonly LiveReloadManager _live;
    private readonly ModuleDownloader _downloader;
    private readonly FileLoggerProvider _fileLog;
    private readonly EnvironmentChecker _checker;
    private readonly StatusBroadcaster _broadcaster;
    private readonly Tools.PhpTools _tools;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<SiteManager> _log;
    private readonly SiteRepository _siteRepository;
    private readonly SettingsRepository _settingsRepository;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _engineExited = new(0, int.MaxValue);
    private readonly Dictionary<string, SiteDefinition> _sites = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (SiteRunState State, LocalizedMessage? Message)> _runtime = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _stateLock = new();
    private WebitSettings _settings = new();
    private bool _engineRestartRequested;
    private FileSystemWatcher? _wwwWatcher;
    private Timer? _autoSiteTimer;
    private CancellationToken _stopping;

    public SiteManager(
        WebitPaths paths,
        CaddyEngine engine,
        ModuleManager modules,
        AppManager apps,
        Tools.PhpTools tools,
        LiveReloadManager live,
        ModuleDownloader downloader,
        FileLoggerProvider fileLog,
        EnvironmentChecker checker,
        StatusBroadcaster broadcaster,
        IHostApplicationLifetime lifetime,
        ILogger<SiteManager> log)
    {
        _paths = paths;
        _engine = engine;
        _modules = modules;
        _apps = apps;
        _tools = tools;
        _live = live;
        _downloader = downloader;
        _fileLog = fileLog;
        _checker = checker;
        _broadcaster = broadcaster;
        _lifetime = lifetime;
        _log = log;
        _siteRepository = new SiteRepository(paths);
        _settingsRepository = new SettingsRepository(paths);
        _engine.UnexpectedExit += () => _engineExited.Release();
        _modules.Changed += Broadcast;
        _apps.Changed += Broadcast;
        _modules.PhpAvailabilityChanged += () => _ = ReapplyAsync();
        _downloader.Changed += Broadcast;
        _modules.IsUpdating = _downloader.IsRunning;
        _downloader.Installing += OnModuleInstalling;
        _downloader.Installed += id => _ = AfterDownloadAsync(id);
    }

    public static string ServiceVersion =>
        typeof(SiteManager).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    public bool IsWindowsService { get; } = OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService();

    public string WwwRoot => _settings.WwwRoot ?? _paths.DefaultWwwDir;

    // ------------------------------------------------------------------ Lifecycle

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Load before the control pipe accepts requests.
        LoadConfiguration();
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _stopping = stoppingToken;
            _live.EnsureAssets();
            _downloader.CleanUp();

            // Modules first: PHP must accept requests once sites are online.
            _modules.Apply(_settings);
            await StartEngineAndApplyAsync(stoppingToken);
            StartAutoSites();
            StartBackupSchedule();

            var failures = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                await _engineExited.WaitAsync(stoppingToken);
                if (_engineRestartRequested || !EngineEnabled)
                {
                    continue;
                }

                failures++;
                MarkRunningSitesFailed(LocalizedMessage.Of(MessageCodes.EngineStopped, ""));
                Broadcast();

                var delay = failures switch
                {
                    1 => TimeSpan.FromSeconds(2),
                    2 => TimeSpan.FromSeconds(5),
                    3 => TimeSpan.FromSeconds(10),
                    _ => TimeSpan.FromSeconds(30)
                };
                _log.LogWarning("Restarting web server engine in {Delay} s (attempt {Attempt}).", delay.TotalSeconds, failures);
                await Task.Delay(delay, stoppingToken);

                if (await StartEngineAndApplyAsync(stoppingToken))
                {
                    _ = ResetFailuresLaterAsync(stoppingToken);
                }
                else if (_engine.State != EngineState.NotInstalled && EngineEnabled)
                {
                    // Failed start raises no exit event: schedule the next attempt.
                    _engineExited.Release();
                }

                async Task ResetFailuresLaterAsync(CancellationToken ct)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMinutes(2), ct);
                        if (_engine.State == EngineState.Running)
                        {
                            failures = 0;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Unexpected error in SiteManager.");
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        StopAutoSites();
        StopBackupSchedule();
        _live.Dispose();

        // In parallel: the shutdown budget is limited (MariaDB shuts down cleanly, apps are killed).
        await Task.WhenAll(Task.Run(_apps.StopAll), Task.Run(_modules.StopAll));
        await _engine.StopAsync();
    }

    private bool EngineEnabled => _settings.IsModuleEnabled(ModuleIds.WebServer);

    private void LoadConfiguration()
    {
        _paths.EnsureCreated();
        _settings = _settingsRepository.Load();
        ApplyLogSettings();
        if (!File.Exists(_paths.SettingsFile))
        {
            _settingsRepository.Save(_settings);
        }

        var loaded = _siteRepository.LoadAll((file, ex) => _log.LogError(ex, "Could not read site file {File}.", file));
        lock (_stateLock)
        {
            foreach (var site in loaded)
            {
                _sites[site.Id] = site;
            }
        }

        _log.LogInformation(
            "WEBit {Version} started ({Mode}). Data: {DataRoot}. Sites: {Count}.",
            ServiceVersion,
            IsWindowsService ? "Windows service" : "desktop mode",
            _paths.DataRoot,
            _sites.Count);
    }

    private async Task<bool> StartEngineAndApplyAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var started = EngineEnabled && await _engine.StartAsync(_settings, BuildConfig([]), ct);
            await ApplyCoreAsync(ct);
            return started;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ------------------------------------------------------------------ Apply

    /// <summary>Id of the internal Adminer site (127.0.0.1:AdminerPort, not a card).</summary>
    public const string AdminerSiteId = "webit-adminer";

    private System.Text.Json.Nodes.JsonObject BuildConfig(IReadOnlyCollection<SiteDefinition> sites)
    {
        var all = sites.ToList();
        if (_modules.IsAdminerAvailable(_settings) && _modules.AdminerFile is { } adminer)
        {
            var adminerSite = new SiteDefinition
            {
                Id = AdminerSiteId,
                Name = "Adminer",
                Type = SiteType.Php,
                Root = Path.GetDirectoryName(adminer)!,
                Color = "#118AB2",
                // Only on 127.0.0.1 unless "Adminer in the network" is on (independent of MariaDB's network access).
                Bindings = [new SiteBinding { Address = ModuleManager.AdminerAddress(_settings), Port = _settings.AdminerPort }],
                Content = new SiteContentOptions
                {
                    DefaultDocuments = ["index.php"],
                    HiddenPaths = [".version"],
                    Charset = CharsetMode.Utf8
                }
            };
            EnsureLogDirectory(adminerSite);
            all.Add(adminerSite);
        }

        return CaddyConfigBuilder.Build(_settings, _paths, all, new CaddyBuildOptions
        {
            PhpFastCgi = _modules.IsPhpAvailable(_settings) ? $"127.0.0.1:{_settings.PhpFastCgiPort}" : null,
            PhpUpstream = site => _modules.UpstreamFor(_settings, site),
            LiveDir = _paths.LiveDir,
            WildcardNames = _settings.HttpsEnabled ? WildcardNames() : [],
            DnsSuffix = _settings.DnsSuffix
        });
    }

    /// <summary>Names for HTTPS on 0.0.0.0: machine name, FQDN, IPv4 addresses and localhost.</summary>
    private static List<string> WildcardNames()
    {
        var names = new List<string> { HostNetwork.MachineName.ToLowerInvariant() };
        if (HostNetwork.GetFqdn() is { } fqdn)
        {
            names.Add(fqdn.ToLowerInvariant());
        }

        names.AddRange(HostNetwork.GetIPv4Addresses().Select(a => a.Address));
        names.Add("localhost");
        names.Add("127.0.0.1");
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private bool _pkiSecured;

    /// <summary>
    /// The CA keys must not be readable by "WEBit Admins" (the data folder grants them Modify): whoever holds the key
    /// can issue certificates every user of a PC that trusts the root accepts. Only SYSTEM, Administrators and the
    /// account the service runs as keep access.
    /// </summary>
    private void SecurePkiFolder(string pki)
    {
        if (_pkiSecured || !OperatingSystem.IsWindows())
        {
            return;
        }

        // Created (empty) before Caddy writes the CA: the key files inherit the restricted rights.
        Directory.CreateDirectory(pki);

        try
        {
            var self = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
            if (self is null)
            {
                return;
            }

            var run = WEBit.Core.Diagnostics.ProcessRunner.Run("icacls.exe",
                [pki, "/inheritance:r", "/grant:r", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F", $"*{self}:(OI)(CI)F", "/T", "/C", "/Q"],
                TimeSpan.FromMinutes(1));
            _pkiSecured = run.ExitCode == 0;
            if (!_pkiSecured)
            {
                _log.LogWarning("Could not restrict access to {Pki}: {Output}", pki, run.Output);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not restrict access to {Pki}.", pki);
        }
    }

    /// <summary>Root certificate of the local CA (created by Caddy once HTTPS is on).</summary>
    public CertificateInfo GetRootCertificate()
    {
        var file = Path.Combine(_paths.EngineRuntimeDir, "data", "pki", "authorities", "local", "root.crt");
        if (!File.Exists(file))
        {
            return new CertificateInfo { Available = false };
        }

        SecurePkiFolder(Path.Combine(_paths.EngineRuntimeDir, "data", "pki"));
        var pem = File.ReadAllText(file);
        using var cert = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(pem);
        return new CertificateInfo
        {
            Available = true,
            Pem = pem,
            Subject = cert.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false),
            Thumbprint = cert.Thumbprint,
            NotAfter = cert.NotAfter
        };
    }

    /// <summary>Re-applies the web server config outside a request (PHP module went up or down).</summary>
    private async Task ReapplyAsync()
    {
        try
        {
            await _gate.WaitAsync(_stopping);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            await ApplyCoreAsync(_stopping);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not re-apply the configuration.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Running PHP site without PHP module: still served, with a notice on the card.</summary>
    private LocalizedMessage? RunningNotice(SiteDefinition site)
    {
        if (site.Type != SiteType.Php || _modules.UpstreamFor(_settings, site) is not null)
        {
            return null;
        }

        if (site.PhpVersion is { } version && _modules.PhpExecutableFor(version) is null)
        {
            return LocalizedMessage.Of(MessageCodes.PhpVersionMissing, version);
        }

        return _modules.IsPoolOverflow(site) ? LocalizedMessage.Of(MessageCodes.PhpPoolFull) : LocalizedMessage.Of(MessageCodes.PhpUnavailable);
    }

    private async Task ApplyCoreAsync(CancellationToken ct)
    {
        List<SiteDefinition> all;
        lock (_stateLock)
        {
            all = _sites.Values.ToList();
        }

        var desired = all
            .Where(s => s.Enabled)
            .OrderBy(s => s.CreatedAt)
            .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        foreach (var site in all.Where(s => !s.Enabled))
        {
            SetRuntime(site.Id, SiteRunState.Stopped, null);
        }

        if (!EngineEnabled)
        {
            _live.Update([]);
            _apps.Sync([], _settings);
            foreach (var site in desired)
            {
                SetRuntime(site.Id, SiteRunState.Stopped, LocalizedMessage.Of(MessageCodes.EngineDisabled));
            }

            Broadcast();
            return;
        }

        if (_engine.State != EngineState.Running)
        {
            _live.Update([]);
            var reason = _engine.Message ?? LocalizedMessage.Of(MessageCodes.EngineNotAvailable);
            foreach (var site in desired)
            {
                SetRuntime(site.Id, SiteRunState.Error, reason);
            }

            Broadcast();
            return;
        }

        if (_settings.HttpsEnabled)
        {
            SecurePkiFolder(Path.Combine(_paths.EngineRuntimeDir, "data", "pki"));
        }

        var snapshot = _checker.TakeSnapshot();
        var enginePid = _engine.ProcessId;
        var candidates = new List<SiteDefinition>();

        foreach (var site in desired)
        {
            var issues = SiteValidator.Validate(site, candidates, _settings);
            issues.AddRange(_checker.CheckSite(site, snapshot, enginePid));
            var error = issues.FirstOrDefault(i => i.Level == IssueLevel.Error);
            if (error is not null)
            {
                SetRuntime(site.Id, SiteRunState.Error, error.Message);
                continue;
            }

            EnsureLogDirectory(site);
            candidates.Add(site);
            SetRuntime(site.Id, SiteRunState.Starting, null);
        }

        Broadcast();

        // Other PHP versions and Xdebug: one php-cgi per combination the sites use.
        _modules.SetPhpDemand(_settings, candidates);
        var result = await _engine.LoadAsync(BuildConfig(candidates), ct);
        if (result.Success)
        {
            foreach (var site in candidates)
            {
                SetRuntime(site.Id, SiteRunState.Running, RunningNotice(site));
            }

            _live.Update(candidates);
            _apps.Sync(candidates, _settings);
            _log.LogInformation("Configuration loaded: {Count} site(s) running.", candidates.Count);
        }
        else
        {
            // Combined load failed: add sites one by one to isolate the broken one.
            _log.LogWarning("Configuration rejected: {Error}. Checking sites one by one.", result.Error);
            var accepted = new List<SiteDefinition>();
            foreach (var site in candidates)
            {
                var attempt = await _engine.LoadAsync(BuildConfig([.. accepted, site]), ct);
                if (attempt.Success)
                {
                    accepted.Add(site);
                    SetRuntime(site.Id, SiteRunState.Running, RunningNotice(site));
                }
                else
                {
                    _log.LogWarning("Site {Site} could not be started: {Error}", site.Name, attempt.Error);
                    SetRuntime(site.Id, SiteRunState.Error, LocalizedMessage.Of(MessageCodes.EngineLoadFailed, attempt.Error));
                }
            }

            if (accepted.Count == 0)
            {
                await _engine.LoadAsync(BuildConfig([]), ct);
            }

            _live.Update(accepted);
            _apps.Sync(accepted, _settings);
        }

        Broadcast();
    }

    private void EnsureLogDirectory(SiteDefinition site)
    {
        try
        {
            Directory.CreateDirectory(_paths.SiteLogDir(site.Id));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not create log folder for {Site}.", site.Name);
        }
    }

    private void MarkRunningSitesFailed(LocalizedMessage message)
    {
        lock (_stateLock)
        {
            foreach (var id in _runtime.Keys.ToList())
            {
                if (_runtime[id].State is SiteRunState.Running or SiteRunState.Starting)
                {
                    _runtime[id] = (SiteRunState.Error, message);
                }
            }
        }
    }

    private void SetRuntime(string id, SiteRunState state, LocalizedMessage? message)
    {
        lock (_stateLock)
        {
            _runtime[id] = (state, message);
        }
    }

    // ------------------------------------------------------------------ Status

    public HelloResponse GetHello() => new()
    {
        ProtocolVersion = Protocol.Version,
        ServiceVersion = ServiceVersion,
        IsWindowsService = IsWindowsService,
        DataRoot = _paths.DataRoot,
        MachineName = HostNetwork.MachineName
    };

    public ServiceStatus GetStatus()
    {
        var context = new UrlContext(HostNetwork.GetFqdn(), _settings.DnsSuffix);
        List<SiteStatus> sites;
        lock (_stateLock)
        {
            sites = _sites.Values
                .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(s =>
                {
                    var runtime = _runtime.TryGetValue(s.Id, out var r)
                        ? r
                        : (State: s.Enabled ? SiteRunState.Starting : SiteRunState.Stopped, Message: (LocalizedMessage?)null);

                    // Node.js/Python: the web server route is up; the app process decides the state.
                    if (s.IsApp && runtime.State == SiteRunState.Running && _apps.StateOf(s.Id) is { } app)
                    {
                        runtime = app;
                    }

                    return new SiteStatus
                    {
                        Site = s.Clone(),
                        State = runtime.State,
                        Message = runtime.Message,
                        PrimaryUrl = UrlBuilder.Primary(s, context),
                        Urls = UrlBuilder.Build(s, context),
                        AppCommand = s.IsApp ? _apps.CommandOf(s.Id) : null
                    };
                })
                .ToList();
        }

        return new ServiceStatus
        {
            ServiceVersion = ServiceVersion,
            IsWindowsService = IsWindowsService,
            StartedAt = StartedAt,
            DataRoot = _paths.DataRoot,
            WwwRoot = WwwRoot,
            LogsDir = _paths.LogsDir,
            MachineName = HostNetwork.MachineName,
            Fqdn = context.MachineFqdn,
            Engine = _engine.Snapshot(),
            Sites = sites,
            Modules = [EngineModule(), .. _modules.Snapshot(_settings, _apps.CountFor).Select(WithDownload)],
            Components = GetComponents(),
            CheckingUpdates = _downloader.IsChecking,
            LastUpdateCheck = _downloader.LastCheck,
            PhpDefaultVersion = _modules.PhpDefaultBranch,
            PhpVersions = _modules.InstalledPhpBranches(),
            XdebugInstalled = _modules.IsXdebugInstalled
        };
    }

    /// <summary>Dependencies page: web server, PHP, Mailpit and the Visual C++ runtime.</summary>
    private List<ComponentStatus> GetComponents()
    {
        var engine = _engine.Snapshot();
        var caddyPath = engine.ExecutablePath is { } running && File.Exists(running) ? running : _engine.ResolveExecutable(_settings);
        var caddyLatest = _downloader.Latest(ModuleIds.WebServer);
        var webitLatest = _downloader.Latest(ModuleIds.Webit);
        var list = new List<ComponentStatus>
        {
            new()
            {
                Id = ModuleIds.Webit,
                Name = "WEBit",
                Kind = ComponentKind.Application,
                Installed = true,
                Version = ServiceVersion,
                LatestVersion = webitLatest,
                UpdateAvailable = webitLatest is not null && IsNewer(webitLatest, ServiceVersion),
                Path = Environment.ProcessPath,
                Source = _downloader.WebitReleaseUrl ?? "https://github.com/complicatiion/WEBit/releases",
                License = "MIT",
                CanDownload = false,
                CanRemove = false
            },
            new()
            {
                Id = ModuleIds.WebServer,
                Name = "Caddy",
                Kind = ComponentKind.Bundled,
                Installed = caddyPath is not null,
                Version = engine.Version,
                LatestVersion = caddyLatest,
                // The bundled copy is updated with WEBit itself; a downloaded copy can be updated here.
                UpdateAvailable = caddyPath is not null && caddyPath.StartsWith(_paths.ModuleDir(ModuleIds.WebServer), StringComparison.OrdinalIgnoreCase)
                    && caddyLatest is not null && engine.Version is not null && IsNewer(caddyLatest, engine.Version),
                Path = caddyPath,
                Source = "https://caddyserver.com",
                License = "Apache-2.0",

                // Normally bundled; downloadable when missing (source build without fetch) or for an update.
                CanDownload = true,
                CanRemove = Directory.Exists(_paths.ModuleDir(ModuleIds.WebServer)),
                Download = _downloader.Get(ModuleIds.WebServer)
            }
        };

        foreach (var (id, name, source, license) in new[]
        {
            (ModuleIds.Php, "PHP", "https://windows.php.net", "PHP License 3.01"),
            (ModuleIds.Mailpit, "Mailpit", "https://mailpit.axllent.org", "MIT"),
            (ModuleIds.MariaDb, "MariaDB", "https://mariadb.org", "GPL-2.0"),
            (ModuleIds.Node, "Node.js", "https://nodejs.org", "MIT"),
            (ModuleIds.Python, "Python (uv)", "https://docs.astral.sh/uv/", "uv: MIT / Apache-2.0 · Python: PSF"),
            (ModuleIds.Composer, "Composer", "https://getcomposer.org", "MIT"),
            (ModuleIds.WpCli, "WP-CLI", "https://wp-cli.org", "MIT"),
            (ModuleIds.Adminer, "Adminer", "https://www.adminer.org", "Apache-2.0 / GPL-2.0"),
            (ModuleIds.Xdebug, "Xdebug", "https://xdebug.org", "Xdebug License")
        })
        {
            var exe = _modules.ExecutableOf(id);
            var version = _modules.VersionOf(id);
            var latest = _downloader.Latest(id);
            list.Add(new ComponentStatus
            {
                Id = id,
                Name = name,
                Kind = ComponentKind.Download,
                Installed = exe is not null,
                Version = version,
                LatestVersion = latest,
                UpdateAvailable = exe is not null && latest is not null && version is not null && IsNewer(latest, version),
                Path = exe,
                Source = source,
                License = license,
                CanDownload = true,
                CanRemove = Directory.Exists(_paths.ModuleDir(id)),
                Download = _downloader.Get(id)
            });
        }

        // Additional PHP versions right after PHP: installed ones and those of the last update check.
        var phpIndex = list.FindIndex(c => c.Id == ModuleIds.Php) + 1;
        var defaultBranch = _modules.PhpDefaultBranch;
        var installedBranches = _modules.InstalledPhpBranches();
        var branches = installedBranches
            .Concat(_downloader.PhpBranches.Where(b => b != defaultBranch))
            .Distinct()
            .OrderByDescending(b => System.Version.Parse(b))
            .ToList();
        foreach (var branch in branches)
        {
            var id = ModuleIds.PhpVersion(branch);
            var exe = _modules.ExecutableOf(id);
            var version = _modules.VersionOf(id);
            var latest = _downloader.Latest(id);
            list.Insert(phpIndex++, new ComponentStatus
            {
                Id = id,
                Name = "PHP " + branch,
                Kind = ComponentKind.Download,
                Installed = exe is not null,
                Version = version,
                LatestVersion = latest,
                UpdateAvailable = exe is not null && latest is not null && version is not null && IsNewer(latest, version),
                Path = exe,
                Source = "https://windows.php.net",
                License = "PHP License 3.01",
                CanDownload = true,
                CanRemove = Directory.Exists(_paths.ModuleDir(id)),
                Download = _downloader.Get(id)
            });
        }

        list.Add(new ComponentStatus
        {
            Id = ModuleDownloader.VcRedistId,
            Name = "Microsoft Visual C++ Runtime (x64)",
            Kind = ComponentKind.SystemWide,
            Installed = OperatingSystem.IsWindows() && ModuleManager.HasSystemVcRuntime(),
            Version = VcRuntimeVersion(),
            Path = _modules.VcRedistInstaller,
            Source = "https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist",
            License = "Microsoft redistributable",
            CanDownload = OperatingSystem.IsWindows(),
            CanRemove = false,
            Download = _downloader.Get(ModuleDownloader.VcRedistId)
        });

        return list;
    }

    /// <summary>Compares dotted versions (8.5.12 vs 8.5.9); text after '-' is ignored.</summary>
    private static bool IsNewer(string latest, string current)
    {
        static Version? Parse(string v)
        {
            var core = v.TrimStart('v').Split('-', '+', ' ')[0];
            return System.Version.TryParse(core, out var parsed) ? parsed : null;
        }

        return Parse(latest) is { } l && Parse(current) is { } c && l > c;
    }

    private static string? VcRuntimeVersion()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64");
            return (key?.GetValue("Version") as string)?.TrimStart('v');
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Deletes the downloaded copy of a module (data\modules\&lt;id&gt;) after stopping it.</summary>
    public async Task RemoveModuleAsync(IdRequest request, CancellationToken ct)
    {
        var id = request.Id?.Trim().ToLowerInvariant() ?? "";
        if (id is not (ModuleIds.Php or ModuleIds.Mailpit or ModuleIds.WebServer or ModuleIds.Node or ModuleIds.Python or ModuleIds.MariaDb
            or ModuleIds.Composer or ModuleIds.WpCli or ModuleIds.Adminer or ModuleIds.Xdebug) && !ModuleIds.IsPhpVersion(id))
        {
            throw new NotSupportedException($"Module '{id}' cannot be removed.");
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_downloader.IsRunning(id))
            {
                throw new InvalidOperationException("A download of this module is running.");
            }

            StopForFileChange(id);
            var dir = _paths.ModuleDir(id);
            try
            {
                for (var attempt = 1; Directory.Exists(dir); attempt++)
                {
                    try
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                    catch (Exception) when (attempt < 10)
                    {
                        await Task.Delay(300, ct);
                    }
                }

                _log.LogInformation("Module {Module} removed ({Dir}).", id, dir);
            }
            finally
            {
                // Removed or not: module state, web server and PHP routing must match the files on disk.
                await RestartAfterFilesChangedAsync(id, ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Update check in the background; the result arrives with the status.</summary>
    public void CheckUpdates() => _ = _downloader.CheckUpdatesAsync(_stopping);

    private ModuleStatus WithDownload(ModuleStatus module)
    {
        if (ModuleDownloader.Supports(module.Id))
        {
            module.CanDownload = true;
            module.Download = _downloader.Get(module.Id);
        }

        return module;
    }

    // ------------------------------------------------------------------ Module downloads

    /// <summary>Starts the one-click download of a module (PHP, Mailpit); progress comes with the status.</summary>
    public void DownloadModule(IdRequest request)
    {
        var id = request.Id?.Trim().ToLowerInvariant() ?? "";
        _downloader.Start(id, _stopping);
    }

    /// <summary>Files of a module changed (download or removal): start it again and apply the sites. Caller holds the gate.</summary>
    private async Task RestartAfterFilesChangedAsync(string id, CancellationToken ct)
    {
        if (id == ModuleIds.WebServer)
        {
            // Only when the downloaded copy is (or was) the one in use, or no web server ran at all.
            var moduleDir = _paths.ModuleDir(ModuleIds.WebServer);
            var current = _engine.Snapshot().ExecutablePath;
            var resolved = _engine.ResolveExecutable(_settings);
            var involved = _engine.State != EngineState.Running
                           || (current?.StartsWith(moduleDir, StringComparison.OrdinalIgnoreCase) ?? false)
                           || (resolved?.StartsWith(moduleDir, StringComparison.OrdinalIgnoreCase) ?? false);
            if (involved)
            {
                _engineRestartRequested = true;
                try
                {
                    await _engine.StopAsync();
                    if (EngineEnabled)
                    {
                        await _engine.StartAsync(_settings, BuildConfig([]), ct);
                    }
                }
                finally
                {
                    _engineRestartRequested = false;
                }
            }
        }
        else
        {
            _modules.InvalidateVersion(id);
            _modules.Apply(_settings);
        }

        await ApplyCoreAsync(ct);
    }

    /// <summary>Before the files of a module are replaced: stop it (the web server too, if it runs from there).</summary>
    /// <summary>True while a download swap holds the gate (taken in Installing, released after Installed).</summary>
    private volatile bool _swapHoldsGate;

    /// <summary>
    /// Downloader is about to replace the files: take the gate for the whole swap, so no request can
    /// start the module (or the web server) from the old folder in between. Released in AfterDownloadAsync.
    /// </summary>
    private void OnModuleInstalling(string id)
    {
        _gate.Wait(_stopping);
        _swapHoldsGate = true;
        _freshInstall = Directory.Exists(_paths.ModuleDir(id)) || _modules.ExecutableOf(id) is not null ? null : id;
        try
        {
            StopForFileChange(id);
        }
        catch (Exception ex)
        {
            // The swap still runs; AfterDownloadAsync releases the gate in any case.
            _log.LogWarning(ex, "Could not stop {Module} before the update.", id);
        }
    }

    /// <summary>Module that was not installed before the running download: switched on once it is there.</summary>
    private string? _freshInstall;

    /// <summary>Stops a module, or the web server if it runs from data\modules\caddy. Caller holds the gate.</summary>
    private void StopForFileChange(string id)
    {
        if (id is ModuleIds.Node or ModuleIds.Python)
        {
            _apps.StopModule(id);
            return;
        }

        if (id is ModuleIds.Composer or ModuleIds.WpCli or ModuleIds.Adminer)
        {
            return;
        }

        if (id != ModuleIds.WebServer)
        {
            _modules.StopModule(id);
            return;
        }

        var runsFromModules = _engine.Snapshot().ExecutablePath is { } path
            && path.StartsWith(_paths.ModuleDir(ModuleIds.WebServer), StringComparison.OrdinalIgnoreCase);
        if (runsFromModules)
        {
            _engineRestartRequested = true;
            _engine.StopAsync().GetAwaiter().GetResult();
        }
    }

    /// <summary>New files are in place: start the module again (if switched on) and route PHP.</summary>
    private async Task AfterDownloadAsync(string id)
    {
        if (_swapHoldsGate)
        {
            // Gate taken in OnModuleInstalling.
            _swapHoldsGate = false;
        }
        else
        {
            try
            {
                await _gate.WaitAsync(_stopping);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        try
        {
            // First download of a module: switch it on (an update keeps the switch as it is).
            if (_freshInstall == id && id != ModuleIds.WebServer && id != ModuleIds.Xdebug && !ModuleIds.IsPhpVersion(id)
                && _modules.ExecutableOf(id) is not null && !_settings.IsModuleEnabled(id))
            {
                _settings.Modules = new Dictionary<string, bool>(_settings.Modules, StringComparer.OrdinalIgnoreCase) { [id] = true };
                _settingsRepository.Save(_settings);
                _log.LogInformation("Module {Module} installed and enabled.", id);
            }

            _freshInstall = null;
            await RestartAfterFilesChangedAsync(id, _stopping);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not start {Module} after the download.", id);
        }
        finally
        {
            _gate.Release();
        }
    }

    private ModuleStatus EngineModule()
    {
        var engine = _engine.Snapshot();
        return new ModuleStatus
        {
            Id = ModuleIds.WebServer,
            Name = "Caddy",
            Enabled = EngineEnabled,
            CanToggle = true,
            State = !EngineEnabled ? ModuleState.Disabled : engine.State switch
            {
                EngineState.Running => ModuleState.Running,
                EngineState.Starting => ModuleState.Starting,
                EngineState.NotInstalled => ModuleState.NotInstalled,
                EngineState.Error => ModuleState.Error,
                _ => ModuleState.Stopped
            },
            Version = engine.Version,
            ExecutablePath = engine.ExecutablePath ?? _engine.ResolveExecutable(_settings),
            Endpoint = $"Admin 127.0.0.1:{_settings.EngineAdminPort}",
            License = "Apache-2.0",
            Message = EngineEnabled ? engine.Message : null
        };
    }

    private static readonly TimeSpan BroadcastInterval = TimeSpan.FromMilliseconds(200);
    private readonly object _broadcastLock = new();
    private DateTime _lastBroadcast = DateTime.MinValue;
    private bool _broadcastScheduled;

    /// <summary>
    /// Pushes the status to all clients, at most every 200 ms: bursts (progress, many state changes)
    /// collapse into one trailing push with the newest state.
    /// </summary>
    private void Broadcast()
    {
        TimeSpan wait;
        lock (_broadcastLock)
        {
            if (_broadcastScheduled)
            {
                return;
            }

            wait = BroadcastInterval - (DateTime.UtcNow - _lastBroadcast);
            if (wait > TimeSpan.Zero)
            {
                _broadcastScheduled = true;
            }
            else
            {
                _lastBroadcast = DateTime.UtcNow;
            }
        }

        if (wait > TimeSpan.Zero)
        {
            _ = Task.Delay(wait).ContinueWith(_ =>
            {
                lock (_broadcastLock)
                {
                    _broadcastScheduled = false;
                    _lastBroadcast = DateTime.UtcNow;
                }

                SendStatus();
            }, TaskScheduler.Default);
            return;
        }

        SendStatus();
    }

    private readonly object _sendLock = new();

    private void SendStatus()
    {
        if (_broadcaster.Count == 0)
        {
            return;
        }

        try
        {
            // One send at a time: a slow snapshot can never overtake a newer one.
            lock (_sendLock)
            {
                _broadcaster.Broadcast(Events.StatusChanged, GetStatus());
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not push status.");
        }
    }

    // ------------------------------------------------------------------ Sites

    /// <param name="insertOnly">Templates and imports: never overwrite an existing site with the same id.</param>
    public async Task<SaveSiteResult> SaveSiteAsync(SiteDefinition site, CancellationToken ct, bool insertOnly = false)
    {
        site.Normalize();
        var formatIssues = SiteValidator.ValidateFormat(site);
        if (SiteValidator.HasErrors(formatIssues))
        {
            throw new InvalidOperationException(string.Join("; ", formatIssues.Where(i => i.Level == IssueLevel.Error).Select(i => i.Message.ToString())));
        }

        await _gate.WaitAsync(ct);
        try
        {
            site.Id = (site.Id ?? "").Trim().ToLowerInvariant();
            if (insertOnly || string.Equals(site.Id, AdminerSiteId, StringComparison.OrdinalIgnoreCase))
            {
                site.Id = SiteIds.CreateUnique(string.IsNullOrWhiteSpace(site.Id) || site.Id == AdminerSiteId ? site.Name : site.Id,
                    _sites.Keys.Append(AdminerSiteId));
                site.CreatedAt = DateTimeOffset.Now;
            }
            else if (string.IsNullOrWhiteSpace(site.Id) || !_sites.ContainsKey(site.Id))
            {
                site.Id = SiteIds.CreateUnique(string.IsNullOrWhiteSpace(site.Id) ? site.Name : site.Id, _sites.Keys);
                site.CreatedAt = DateTimeOffset.Now;
            }
            else
            {
                site.CreatedAt = _sites[site.Id].CreatedAt;
            }

            site.SchemaVersion = SiteDefinition.CurrentSchemaVersion;
            site.ModifiedAt = DateTimeOffset.Now;
            AssignAppPort(site);
            if (SiteColors.IsRandom(site.Color))
            {
                lock (_stateLock)
                {
                    site.Color = SiteColors.Pick(_sites.Values.Where(o => o.Id != site.Id).Select(o => o.Color));
                }
            }

            PrepareWwwFolder(site);

            _siteRepository.Save(site);
            lock (_stateLock)
            {
                _sites[site.Id] = site;
            }
            _log.LogInformation("Site saved: {Name} ({Id}).", site.Name, site.Id);

            var issues = SiteValidator.Validate(site, _sites.Values, _settings);
            issues.AddRange(_checker.CheckSite(site, _checker.TakeSnapshot(), _engine.ProcessId).Where(i => i.Level != IssueLevel.Ok));

            await ApplyCoreAsync(ct);
            return new SaveSiteResult { Site = site.Clone(), Issues = issues };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Node.js/Python sites keep their internal port unless another app or module uses it (duplicate, settings);
    /// new ones get the first free port from the internal range (start + 10). Caller holds the gate.
    /// </summary>
    private void AssignAppPort(SiteDefinition site)
    {
        if (!site.IsApp)
        {
            return;
        }

        var used = new HashSet<int>
        {
            _settings.EngineAdminPort, _settings.MailpitSmtpPort, _settings.MailpitUiPort, _settings.PhpFastCgiPort, _settings.MariaDbPort,
            _settings.AdminerPort
        };
        used.UnionWith(_settings.PhpPoolPorts);
        lock (_stateLock)
        {
            foreach (var other in _sites.Values.Where(o => o.IsApp && !string.Equals(o.Id, site.Id, StringComparison.OrdinalIgnoreCase)))
            {
                used.Add(other.App.Port);
            }

            foreach (var other in _sites.Values.Where(o => !string.Equals(o.Id, site.Id, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var binding in other.Bindings)
                {
                    used.Add(binding.Port);
                }
            }
        }

        foreach (var binding in site.Bindings)
        {
            used.Add(binding.Port);
        }

        if (PortPolicy.IsValidPort(site.App.Port) && !used.Contains(site.App.Port))
        {
            return;
        }

        var start = Math.Min(_settings.InternalPortEnd, _settings.InternalPortStart + 10);
        for (var port = start; port <= _settings.InternalPortEnd; port++)
        {
            if (!used.Contains(port) && PortProbe.CanBind("127.0.0.1", port, out _))
            {
                site.App.Port = port;
                _log.LogInformation("App {Site}: internal port 127.0.0.1:{Port}.", site.Name, port);
                return;
            }
        }

        site.App.Port = 0;
        _log.LogWarning("App {Site}: no free port in {Start}–{End}.", site.Name, start, _settings.InternalPortEnd);
    }

    /// <summary>New sites inside the www folder get a folder with a placeholder page.</summary>
    private void PrepareWwwFolder(SiteDefinition site)
    {
        if (!site.ServesFiles || string.IsNullOrWhiteSpace(site.Root) || Directory.Exists(site.Root))
        {
            return;
        }

        try
        {
            var www = Path.GetFullPath(WwwRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var root = Path.GetFullPath(site.Root);
            if (!root.StartsWith(www, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Directory.CreateDirectory(root);
            var index = Path.Combine(root, "index.html");
            var name = System.Net.WebUtility.HtmlEncode(site.Name);
            File.WriteAllText(index, $$"""
                <!doctype html>
                <html lang="en">
                <head>
                  <meta charset="utf-8">
                  <meta name="viewport" content="width=device-width, initial-scale=1">
                  <title>{{name}}</title>
                  <style>
                    body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: #1B1D2E; color: #F2F2ED; font-family: "Segoe UI", system-ui, sans-serif; }
                    main { text-align: center; padding: 32px; border-radius: 12px; background: #2A2D45; border-top: 4px solid #118AB2; }
                    h1 { margin: 0 0 8px; font-size: 28px; }
                    p { margin: 0; color: #B9BACB; }
                  </style>
                </head>
                <body>
                  <main>
                    <h1>{{name}}</h1>
                    <p>This site is ready. Copy your content into this folder and replace index.html.</p>
                  </main>
                </body>
                </html>
                """, WebitJsonEncoding.Utf8NoBom);
            _log.LogInformation("Created {Root} with placeholder page.", root);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not create {Root}.", site.Root);
        }
    }

    public async Task DeleteSiteAsync(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            SiteDefinition? site;
            lock (_stateLock)
            {
                if (_sites.Remove(id, out site))
                {
                    _runtime.Remove(id);
                }
            }

            if (site is not null)
            {
                _siteRepository.Delete(site.Id);
                if (site.AutoCreated && !string.IsNullOrWhiteSpace(site.Root))
                {
                    // Deleted on purpose: do not recreate it from the www folder.
                    var folder = Path.GetFileName(site.Root.TrimEnd('\\', '/'));
                    if (!_settings.IgnoredAutoFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                    {
                        _settings.IgnoredAutoFolders = [.. _settings.IgnoredAutoFolders, folder];
                        _settingsRepository.Save(_settings);
                    }
                }

                _log.LogInformation("Site deleted: {Name} ({Id}).", site.Name, site.Id);
            }

            await ApplyCoreAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetEnabledAsync(string? id, bool enabled, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var targets = new List<SiteDefinition>();
            if (id is null)
            {
                targets.AddRange(_sites.Values);
            }
            else if (_sites.TryGetValue(id, out var single))
            {
                targets.Add(single);
            }

            foreach (var site in targets.Where(t => t.Enabled != enabled))
            {
                site.Enabled = enabled;
                site.ModifiedAt = DateTimeOffset.Now;
                _siteRepository.Save(site);
            }

            await ApplyCoreAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestartEngineAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _engineRestartRequested = true;
            await _engine.StopAsync();
            if (EngineEnabled)
            {
                await _engine.StartAsync(_settings, BuildConfig([]), ct);
            }

            await ApplyCoreAsync(ct);
        }
        finally
        {
            _engineRestartRequested = false;
            _gate.Release();
        }
    }

    // ------------------------------------------------------------------ Modules

    public async Task SetModuleEnabledAsync(ModuleToggleRequest request, CancellationToken ct)
    {
        var id = request.Id?.Trim().ToLowerInvariant() ?? "";
        if (id is not (ModuleIds.WebServer or ModuleIds.Mailpit or ModuleIds.Php or ModuleIds.Node or ModuleIds.Python or ModuleIds.MariaDb
            or ModuleIds.Adminer))
        {
            throw new NotSupportedException($"Module '{id}' cannot be switched.");
        }

        await _gate.WaitAsync(ct);
        try
        {
            // Copy-on-write: status readers may enumerate the dictionary concurrently.
            _settings.Modules = new Dictionary<string, bool>(_settings.Modules, StringComparer.OrdinalIgnoreCase) { [id] = request.Enabled };
            _settingsRepository.Save(_settings);
            _log.LogInformation("Module {Module} {State}.", id, request.Enabled ? "enabled" : "disabled");

            if (id == ModuleIds.WebServer)
            {
                _engineRestartRequested = true;
                try
                {
                    if (request.Enabled)
                    {
                        await _engine.StartAsync(_settings, BuildConfig([]), ct);
                    }
                    else
                    {
                        await _engine.StopAsync();
                    }
                }
                finally
                {
                    _engineRestartRequested = false;
                }

                await ApplyCoreAsync(ct);
            }
            else if (id is ModuleIds.Php or ModuleIds.Node or ModuleIds.Python or ModuleIds.Adminer)
            {
                // PHP: routing of *.php changes; Node.js/Python: the app processes start or stop.
                _modules.Apply(_settings);
                await ApplyCoreAsync(ct);
            }
            else
            {
                _modules.Apply(_settings);
                Broadcast();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestartModuleAsync(IdRequest request, CancellationToken ct)
    {
        if (string.Equals(request.Id, ModuleIds.WebServer, StringComparison.OrdinalIgnoreCase))
        {
            await RestartEngineAsync(ct);
            return;
        }

        if (request.Id?.ToLowerInvariant() is ModuleIds.Node or ModuleIds.Python)
        {
            _apps.RestartModule(request.Id.ToLowerInvariant());
            return;
        }

        if (request.Id?.ToLowerInvariant() is ModuleIds.Adminer)
        {
            // Served by Caddy: apply the configuration again.
            await ReapplyAsync();
            return;
        }

        await _gate.WaitAsync(ct);
        try
        {
            _modules.Restart(_settings, request.Id ?? "");
        }
        finally
        {
            _gate.Release();
        }
    }

    // ------------------------------------------------------------------ Apps and databases

    /// <summary>Restarts the process of a Node.js/Python site.</summary>
    public void RestartApp(IdRequest request)
    {
        if (!_apps.Restart(request.Id ?? ""))
        {
            throw new InvalidOperationException("The app is not running – start the site first.");
        }
    }

    /// <summary>npm install / uv sync for a running app site; the app starts again afterwards.</summary>
    public async Task<bool> InstallAppAsync(IdRequest request, CancellationToken ct)
    {
        SiteDefinition? site;
        lock (_stateLock)
        {
            _sites.TryGetValue(request.Id ?? "", out site);
        }

        // PHP sites with composer.json: composer install.
        if (site is { Type: SiteType.Php } && ProjectDir(site) is not null)
        {
            return await ComposerInstallAsync(site, ct);
        }

        if (site is not { IsApp: true })
        {
            throw new InvalidOperationException("Nothing to install for this site (no package.json, pyproject.toml or composer.json).");
        }

        if (_apps.StateOf(site.Id) is null)
        {
            throw new InvalidOperationException("The app is not running – start the site first.");
        }

        if (Core.Apps.AppCommandResolver.ResolveInstall(site) is null)
        {
            throw new InvalidOperationException("Nothing to install (no package.json / pyproject.toml).");
        }

        return await _apps.InstallAsync(site.Id, ct)
               ?? throw new InvalidOperationException("Packages are already being installed.");
    }

    /// <summary>Installed PHP: version and the extensions available in ext\.</summary>
    public PhpInfo GetPhpInfo() => new()
    {
        Version = _modules.VersionOf(ModuleIds.Php),
        Available = _modules.PhpExecutable is { } exe ? PhpIni.AvailableExtensions(exe) : [],
        Defaults = [.. PhpSettings.DefaultExtensions]
    };

    public DbCredentials GetDbCredentials() => new()
    {
        Host = _settings.MariaDbNetworkAccess ? HostNetwork.FirstIPv4() ?? "127.0.0.1" : "127.0.0.1",
        Port = _settings.MariaDbPort,
        User = "root",
        Password = _modules.Database.RootPassword,
        NetworkAccess = _settings.MariaDbNetworkAccess
    };

    public Task<CreateDatabaseResult> CreateDatabaseAsync(CreateDatabaseRequest request, CancellationToken ct)
    {
        var server = RunningMariaDb();
        var name = request.Name?.Trim() ?? "";
        return Task.Run(() => _modules.Database.CreateDatabase(server, _settings, name), ct);
    }

    public async Task<BackupResult> BackupDatabaseAsync(BackupRequest? request, CancellationToken ct)
    {
        var server = RunningMariaDb();
        var name = string.IsNullOrWhiteSpace(request?.Name) ? null : request.Name.Trim();
        return new BackupResult { Path = await Task.Run(() => _modules.Database.Backup(server, _settings, name), ct) };
    }

    private string RunningMariaDb() =>
        _modules.MariaDbExecutable is { } exe && _modules.IsMariaDbRunning
            ? exe
            : throw new InvalidOperationException("MariaDB is not running.");

    // ------------------------------------------------------------------ Quick test: automatic sites

    /// <summary>Every new folder in the www root becomes a site with live reload (Laragon style).</summary>
    private void StartAutoSites()
    {
        StopAutoSites();
        if (!_settings.AutoSites)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(WwwRoot);
            _autoSiteTimer = new Timer(_ => _ = CreateAutoSitesAsync());
            _wwwWatcher = new FileSystemWatcher(WwwRoot)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.DirectoryName
            };
            _wwwWatcher.Created += (_, _) => ScheduleAutoSites();
            _wwwWatcher.Renamed += (_, e) => _ = RenameAutoSiteAsync(e.OldFullPath, e.FullPath);
            _wwwWatcher.EnableRaisingEvents = true;
            ScheduleAutoSites();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Automatic sites: cannot watch {Www}.", WwwRoot);
        }
    }

    private void StopAutoSites()
    {
        _wwwWatcher?.Dispose();
        _wwwWatcher = null;
        _autoSiteTimer?.Dispose();
        _autoSiteTimer = null;
    }

    // Folders are often copied in several steps: wait until it is quiet.
    private void ScheduleAutoSites(TimeSpan? delay = null) =>
        _autoSiteTimer?.Change(delay ?? TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);

    /// <summary>Contains .php files (stops at the first hit; skips vendor/node_modules).</summary>
    private static bool ContainsPhp(string dir)
    {
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, MaxRecursionDepth = 6 };
            return Directory.EnumerateFiles(dir, "*.php", options)
                .Any(f => !f.Contains(@"\vendor\", StringComparison.OrdinalIgnoreCase)
                          && !f.Contains(@"\node_modules\", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsEmpty(string dir)
    {
        try
        {
            return !Directory.EnumerateFileSystemEntries(dir).Any();
        }
        catch (Exception)
        {
            return true;
        }
    }

    private async Task CreateAutoSitesAsync()
    {
        if (!_settings.AutoSites || !Directory.Exists(WwwRoot))
        {
            return;
        }

        List<SiteDefinition> existing;
        lock (_stateLock)
        {
            existing = _sites.Values.ToList();
        }

        // Scan the file system before taking the lock: big folders must not block requests.
        var usedRoots = new HashSet<string>(
            existing.Where(s => !string.IsNullOrWhiteSpace(s.Root)).Select(s => NormalizePath(s.Root)),
            StringComparer.OrdinalIgnoreCase);
        var candidates = new List<(string Dir, bool Php)>();
        var waitForContent = false;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(WwwRoot).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var folder = Path.GetFileName(dir);
                // Also folders that contain a site root (Laravel: www\Name\public) or are being set up (template, import).
                var normalized = NormalizePath(dir);
                if (folder.StartsWith('.') || folder.StartsWith('_')
                    || usedRoots.Contains(normalized)
                    || usedRoots.Any(r => r.StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    || IsBusyFolder(normalized)
                    || _settings.IgnoredAutoFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                // A just created, still empty folder ("New folder" in Explorer): wait for name and content.
                if (IsEmpty(dir) && DateTime.Now - Directory.GetCreationTime(dir) < TimeSpan.FromSeconds(30))
                {
                    waitForContent = true;
                    continue;
                }

                candidates.Add((dir, ContainsPhp(dir)));
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Automatic sites: cannot read {Www}.", WwwRoot);
            return;
        }

        // Static auto sites that received PHP files later (copy, git clone) become PHP sites.
        var upgrades = existing
            .Where(s => s.AutoCreated && s.Type == SiteType.Static && Directory.Exists(s.Root) && ContainsPhp(s.Root))
            .Select(s => s.Id)
            .ToList();

        if (waitForContent)
        {
            ScheduleAutoSites(TimeSpan.FromSeconds(10));
        }

        if (candidates.Count == 0 && upgrades.Count == 0)
        {
            return;
        }

        try
        {
            await _gate.WaitAsync(_stopping);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var changed = 0;
        try
        {
            foreach (var id in upgrades)
            {
                if (_sites.TryGetValue(id, out var site) && site.Type == SiteType.Static)
                {
                    site.Type = SiteType.Php;
                    site.Description = "PHP · quick test";
                    site.ModifiedAt = DateTimeOffset.Now;
                    _siteRepository.Save(site);
                    changed++;
                    _log.LogInformation("Automatic site {Name} switched to PHP.", site.Name);
                }
            }

            var reserved = new List<int>();
            foreach (var (dir, php) in candidates)
            {
                try
                {
                    if (!Directory.Exists(dir) || _sites.Values.Any(s => string.Equals(NormalizePath(s.Root), NormalizePath(dir), StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var folder = Path.GetFileName(dir);
                    var address = _settings.DefaultBindAddress ?? HostNetwork.FirstIPv4() ?? "127.0.0.1";
                    var port = SuggestPort(new SuggestPortRequest { Address = address, Reserved = reserved }).Port;
                    if (port is null)
                    {
                        _log.LogWarning("Automatic sites: no free port for {Folder}.", folder);
                        break;
                    }

                    reserved.Add(port.Value);
                    var type = php ? SiteType.Php : SiteType.Static;
                    var site = new SiteDefinition
                    {
                        Name = folder,
                        Description = php ? "PHP · quick test" : "Quick test",
                        Type = type,
                        Root = dir,
                        LiveReload = true,
                        AutoCreated = true,
                        Color = SiteColors.Pick(_sites.Values.Select(s => s.Color)),
                        Bindings = [new SiteBinding { Address = address, Port = port.Value }]
                    };
                    site.Normalize();
                    site.Id = SiteIds.CreateUnique(folder, _sites.Keys);
                    site.CreatedAt = DateTimeOffset.Now;
                    site.ModifiedAt = site.CreatedAt;

                    _siteRepository.Save(site);
                    lock (_stateLock)
                    {
                        _sites[site.Id] = site;
                    }

                    changed++;
                    _log.LogInformation("Automatic site {Name} ({Type}) on {Address}:{Port}.", site.Name, site.Type, address, port);
                }
                catch (Exception ex)
                {
                    // One broken folder must not block the others.
                    _log.LogWarning(ex, "Automatic site for {Dir} failed.", dir);
                }
            }

            if (changed > 0)
            {
                await ApplyCoreAsync(_stopping);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Automatic sites failed.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Folder renamed in www: the automatic site follows (name and root).</summary>
    private async Task RenameAutoSiteAsync(string oldPath, string newPath)
    {
        try
        {
            await _gate.WaitAsync(_stopping);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var site = _sites.Values.FirstOrDefault(s => s.AutoCreated
                && string.Equals(NormalizePath(s.Root), NormalizePath(oldPath), StringComparison.OrdinalIgnoreCase));
            if (site is not null)
            {
                var oldName = Path.GetFileName(oldPath);
                site.Root = newPath;
                if (string.Equals(site.Name, oldName, StringComparison.OrdinalIgnoreCase))
                {
                    site.Name = Path.GetFileName(newPath);
                }

                site.ModifiedAt = DateTimeOffset.Now;
                _siteRepository.Save(site);
                _log.LogInformation("Automatic site {Name} follows renamed folder {Root}.", site.Name, newPath);
                await ApplyCoreAsync(_stopping);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Automatic site rename failed.");
        }
        finally
        {
            _gate.Release();
        }

        ScheduleAutoSites();
    }

    /// <summary>Folders a template or import is filling right now: never an automatic site.</summary>
    private readonly HashSet<string> _busyFolders = new(StringComparer.OrdinalIgnoreCase);

    private bool IsBusyFolder(string normalized)
    {
        lock (_busyFolders)
        {
            return _busyFolders.Contains(normalized);
        }
    }

    private void SetBusyFolder(string folder, bool busy)
    {
        lock (_busyFolders)
        {
            if (busy)
            {
                _busyFolders.Add(NormalizePath(folder));
            }
            else
            {
                _busyFolders.Remove(NormalizePath(folder));
            }
        }
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd('\\', '/');
        }
        catch
        {
            return path.TrimEnd('\\', '/');
        }
    }

    // ------------------------------------------------------------------ Checks

    public List<ValidationIssue> CheckSite(SiteDefinition site)
    {
        site.Normalize();
        List<SiteDefinition> others;
        lock (_stateLock)
        {
            others = _sites.Values.ToList();
        }

        var issues = SiteValidator.Validate(site, others, _settings);
        issues.AddRange(_checker.CheckSite(site, _checker.TakeSnapshot(), _engine.ProcessId));
        return issues;
    }

    public List<ValidationIssue> CheckEndpoint(EndpointCheckRequest request)
    {
        var probe = new SiteDefinition
        {
            Id = string.IsNullOrWhiteSpace(request.ExcludeSiteId) ? "__probe__" : request.ExcludeSiteId,
            Name = "probe",
            Type = SiteType.Redirect,
            RedirectTarget = "http://probe/",
            Bindings = [new SiteBinding { Address = request.Address, Port = request.Port }]
        };

        List<SiteDefinition> others;
        lock (_stateLock)
        {
            others = _sites.Values.ToList();
        }

        var issues = SiteValidator.Validate(probe, others, _settings)
            .Where(i => i.Field?.StartsWith("binding", StringComparison.Ordinal) == true)
            .ToList();
        issues.AddRange(_checker.CheckEndpoint(request.Address, request.Port, _checker.TakeSnapshot(), _engine.ProcessId, "binding:0"));
        return issues;
    }

    public SuggestPortResponse SuggestPort(SuggestPortRequest request)
    {
        var used = new HashSet<int>(request.Reserved);
        lock (_stateLock)
        {
            foreach (var site in _sites.Values.Where(s => !string.Equals(s.Id, request.ExcludeSiteId, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var b in site.Bindings)
                {
                    used.Add(b.Port);
                }
            }
        }

        used.Add(_settings.EngineAdminPort);
        used.Add(_settings.MailpitSmtpPort);
        used.Add(_settings.MailpitUiPort);
        used.Add(_settings.PhpFastCgiPort);
        used.UnionWith(_settings.PhpPoolPorts);
        used.Add(_settings.MariaDbPort);
        used.Add(_settings.AdminerPort);
        lock (_stateLock)
        {
            // Internal ports of Node.js/Python apps.
            foreach (var app in _sites.Values.Where(s => s.IsApp))
            {
                used.Add(app.App.Port);
            }
        }

        var snapshot = _checker.TakeSnapshot();
        var enginePid = _engine.ProcessId;
        var port = PortPolicy.Suggest(
            _settings.SitePortStart,
            _settings.SitePortEnd,
            p => !used.Contains(p) && _checker.IsPortFree(p, request.Address, snapshot, enginePid));

        return new SuggestPortResponse { Port = port };
    }

    public NetworkInfo GetNetworkInfo() => _checker.GetNetworkInfo(_engine.ProcessId);

    public FolderAnalysis AnalyzeFolder(PathRequest request) => FolderAnalyzer.Analyze(request.Path, request.DefaultDocuments);

    public LogResponse GetLog(LogRequest request)
    {
        string? file = request.Source switch
        {
            "service" => _paths.ServiceLogFile,
            "engine" => _paths.EngineLogFile,
            "module:mailpit" => _modules.MailpitLogFile,
            "module:php" => _modules.PhpLogFile,
            "module:mariadb" => _modules.MariaDbLogFile,
            _ when request.Source.StartsWith("app:", StringComparison.Ordinal) => SiteAppLogFile(request.Source[4..]),
            _ when request.Source.StartsWith("site:", StringComparison.Ordinal) => SiteLogFile(request.Source[5..]),
            _ => null
        };

        List<string> lines = file is null
            ? []
            : LogReader.Tail(file, Math.Clamp(request.Lines, 10, 5000)).Select(LogReader.Format).ToList();

        return new LogResponse { Source = request.Source, FilePath = file, Lines = lines };
    }

    private string? SiteAppLogFile(string id)
    {
        try
        {
            return _paths.SiteAppLog(SiteIds.EnsureSafe(id));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private string? SiteLogFile(string id)
    {
        try
        {
            return _paths.SiteAccessLog(SiteIds.EnsureSafe(id));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ Settings

    public WebitSettings GetSettings()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(_settings, WEBit.Core.Serialization.WebitJson.Wire);
        return System.Text.Json.JsonSerializer.Deserialize<WebitSettings>(json, WEBit.Core.Serialization.WebitJson.Wire)!;
    }

    public async Task<WebitSettings> SaveSettingsAsync(WebitSettings settings, CancellationToken ct)
    {
        settings.Normalize();
        await _gate.WaitAsync(ct);
        try
        {
            // Pipe access list and module switches are not edited through this request.
            settings.AllowedUserSids = _settings.AllowedUserSids;
            settings.Modules = _settings.Modules;
            settings.IgnoredAutoFolders = _settings.IgnoredAutoFolders;
            var phpPortChanged = settings.PhpFastCgiPort != _settings.PhpFastCgiPort || settings.MailpitSmtpPort != _settings.MailpitSmtpPort
                || settings.Logging.ModuleLogs != _settings.Logging.ModuleLogs
                || System.Text.Json.JsonSerializer.Serialize(settings.Php, WEBit.Core.Serialization.WebitJson.Wire)
                   != System.Text.Json.JsonSerializer.Serialize(_settings.Php, WEBit.Core.Serialization.WebitJson.Wire);
            var autoSitesChanged = settings.AutoSites != _settings.AutoSites
                || !string.Equals(settings.WwwRoot, _settings.WwwRoot, StringComparison.OrdinalIgnoreCase);
            var mailpitPortsChanged = settings.MailpitSmtpPort != _settings.MailpitSmtpPort || settings.MailpitUiPort != _settings.MailpitUiPort;
            var adminPortChanged = settings.EngineAdminPort != _settings.EngineAdminPort;
            var enginePathChanged = !string.Equals(settings.EnginePath, _settings.EnginePath, StringComparison.OrdinalIgnoreCase);
            var mariaDbChanged = settings.MariaDbPort != _settings.MariaDbPort || settings.MariaDbNetworkAccess != _settings.MariaDbNetworkAccess;
            _settings = settings;
            _settingsRepository.Save(_settings);
            ApplyLogSettings();
            _modules.ConfigureLogs(_settings.Logging);
            _apps.ConfigureLogs(_settings.Logging);
            _log.LogInformation("Settings saved.");

            if (EngineEnabled && (adminPortChanged || enginePathChanged || _engine.State != EngineState.Running))
            {
                _engineRestartRequested = true;
                try
                {
                    await _engine.StopAsync();
                    await _engine.StartAsync(_settings, BuildConfig([]), ct);
                }
                finally
                {
                    _engineRestartRequested = false;
                }
            }

            if (phpPortChanged)
            {
                // php.ini carries the Mailpit SMTP port.
                _modules.Restart(_settings, ModuleIds.Php);
            }

            await ApplyCoreAsync(ct);
            if (mailpitPortsChanged)
            {
                _modules.Restart(_settings, ModuleIds.Mailpit);
            }

            if (mariaDbChanged && _settings.IsModuleEnabled(ModuleIds.MariaDb))
            {
                // Port or bind address (network access) changed: my.ini is written at start.
                await Task.Run(() => _modules.Restart(_settings, ModuleIds.MariaDb), ct);
            }

            if (autoSitesChanged)
            {
                StartAutoSites();
            }

            return GetSettings();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ApplyLogSettings()
    {
        var logging = _settings.Logging;
        var level = logging.Level switch
        {
            "Debug" => LogLevel.Debug,
            "Warning" => LogLevel.Warning,
            "Error" => LogLevel.Error,
            _ => LogLevel.Information
        };
        _fileLog.Configure(logging.ServiceLog, level, logging.MaxSizeMb, logging.Keep);
    }

    /// <summary>Empties all log files (open files are truncated, rolled-over archives deleted).</summary>
    public int ClearLogs()
    {
        var cleared = 0;
        if (!Directory.Exists(_paths.LogsDir))
        {
            return 0;
        }

        foreach (var file in Directory.EnumerateFiles(_paths.LogsDir, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("setup.log", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (name.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                {
                    // Caddy and the modules keep their files open: truncate instead of deleting.
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    stream.SetLength(0);
                }
                else
                {
                    File.Delete(file);
                }

                cleared++;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Could not clear {File}.", file);
            }
        }

        _log.LogInformation("Logs cleared ({Count} files).", cleared);
        return cleared;
    }

    public void Shutdown()
    {
        if (IsWindowsService)
        {
            throw new InvalidOperationException("The Windows service is stopped through the service manager.");
        }

        _log.LogInformation("Desktop mode shutdown requested.");
        _lifetime.StopApplication();
    }
}

/// <summary>Shortcut to UTF-8 without BOM.</summary>
internal static class WebitJsonEncoding
{
    public static System.Text.Encoding Utf8NoBom => WEBit.Core.Serialization.WebitJson.Utf8NoBom;
}
