using System.Diagnostics;
using Microsoft.Extensions.Logging;
using WEBit.Contracts;
using WEBit.Core.Models;
using WEBit.Core.Net;

namespace WEBit.Service.Modules;

/// <summary>PHP process of a site: version branch ("8.2") and whether Xdebug is loaded.</summary>
public readonly record struct PhpProcessKey(string Branch, bool Xdebug)
{
    public override string ToString() => Xdebug ? Branch + "-xdebug" : Branch;
}

/// <summary>
/// Additional php-cgi processes next to the default PHP module: other PHP versions (data\modules\php-8.2 …) and
/// Xdebug variants. Started on demand for the running sites, each on a port of <see cref="WebitSettings.PhpPoolPorts"/>.
/// </summary>
public sealed partial class ModuleManager
{
    private sealed record PoolEntry(PhpProcessKey Key, int Port, ManagedProcess Process);

    private readonly object _poolLock = new();
    private readonly Dictionary<PhpProcessKey, PoolEntry> _pool = new();

    /// <summary>Keys that did not get a port (all pool ports busy).</summary>
    private readonly HashSet<PhpProcessKey> _poolOverflow = new();

    /// <summary>Branch of the default PHP module ("8.4"), null when not installed.</summary>
    public string? PhpDefaultBranch => ModuleIds.BranchOfVersion(VersionOf(ModuleIds.Php));

    /// <summary>Additional PHP versions on disk (data\modules\php-*), sorted.</summary>
    public List<string> InstalledPhpBranches()
    {
        if (!Directory.Exists(_paths.ModulesDir))
        {
            return [];
        }

        return Directory.EnumerateDirectories(_paths.ModulesDir, ModuleIds.PhpVersionPrefix + "*")
            .Select(d => Path.GetFileName(d))
            .Select(name => ModuleIds.PhpBranchOf(name ?? ""))
            .OfType<string>()
            .Where(branch => ExecutableOf(ModuleIds.PhpVersion(branch)) is not null)
            .OrderBy(branch => System.Version.Parse(branch))
            .ToList();
    }

    /// <summary>php-cgi of a branch: the default module or an additional version.</summary>
    public string? PhpExecutableFor(string? branch)
    {
        if (branch is null || branch == PhpDefaultBranch)
        {
            return PhpExecutable;
        }

        return ExecutableOf(ModuleIds.PhpVersion(branch));
    }

    /// <summary>Downloaded Xdebug DLLs (php_xdebug-8.4.dll …).</summary>
    private IEnumerable<string> XdebugFiles()
    {
        var dir = _paths.ModuleDir(ModuleIds.Xdebug);
        return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "php_xdebug-*.dll").Order(StringComparer.OrdinalIgnoreCase) : [];
    }

    /// <summary>Xdebug for a PHP branch, null when not downloaded.</summary>
    public string? XdebugDll(string? branch)
    {
        if (branch is null)
        {
            return null;
        }

        var file = Path.Combine(_paths.ModuleDir(ModuleIds.Xdebug), $"php_xdebug-{branch}.dll");
        return File.Exists(file) ? file : null;
    }

    public bool IsXdebugInstalled => XdebugFiles().Any();

    /// <summary>Process key of a PHP site; null when no PHP is installed at all.</summary>
    public PhpProcessKey? KeyFor(SiteDefinition site)
    {
        var branch = site.PhpVersion ?? PhpDefaultBranch;
        if (branch is null)
        {
            return null;
        }

        return new PhpProcessKey(branch, site.Xdebug && XdebugDll(branch) is not null);
    }

    private bool IsDefaultKey(PhpProcessKey key) => !key.Xdebug && key.Branch == PhpDefaultBranch;

    /// <summary>FastCGI address for a site's PHP; null = not installed, not running or no free port.</summary>
    public string? UpstreamFor(WebitSettings settings, SiteDefinition site)
    {
        if (KeyFor(site) is not { } key)
        {
            return null;
        }

        if (IsDefaultKey(key))
        {
            return IsPhpAvailable(settings) ? $"127.0.0.1:{settings.PhpFastCgiPort}" : null;
        }

        lock (_poolLock)
        {
            return _pool.TryGetValue(key, out var entry) && entry.Process.State is ModuleState.Running or ModuleState.Starting
                ? $"127.0.0.1:{entry.Port}"
                : null;
        }
    }

    /// <summary>
    /// Starts the processes the running PHP sites need and stops the others. Running processes keep their port,
    /// so the web server config of other sites does not change. A failed process is not retried here
    /// (Modules → PHP → Restart does).
    /// </summary>
    public void SetPhpDemand(WebitSettings settings, IEnumerable<SiteDefinition> sites)
    {
        var wanted = settings.IsModuleEnabled(ModuleIds.Php) && !Updating(ModuleIds.Php)
            ? sites.Where(s => s.Type == SiteType.Php)
                .Select(KeyFor)
                .OfType<PhpProcessKey>()
                .Where(k => !IsDefaultKey(k) && !Updating(ModuleIds.PhpVersion(k.Branch)) && !(k.Xdebug && Updating(ModuleIds.Xdebug)))
                .ToHashSet()
            : new HashSet<PhpProcessKey>();

        // Only the dictionaries under the lock: Stop/Start raise StateChanged → status broadcast, which reads the pool.
        var stop = new List<PoolEntry>();
        var start = new List<PoolEntry>();
        lock (_poolLock)
        {
            foreach (var entry in _pool.Values.Where(e => !wanted.Contains(e.Key)).ToList())
            {
                _pool.Remove(entry.Key);
                stop.Add(entry);
            }

            _poolOverflow.Clear();
            foreach (var key in wanted.OrderBy(k => k.ToString(), StringComparer.Ordinal))
            {
                if (_pool.ContainsKey(key))
                {
                    continue;
                }

                var port = settings.PhpPoolPorts.FirstOrDefault(p => _pool.Values.All(e => e.Port != p));
                if (port == 0)
                {
                    _poolOverflow.Add(key);
                    _log.LogWarning("No free port for PHP {Key}: at most {Count} additional PHP processes.", key, settings.PhpPoolPorts.Count);
                    continue;
                }

                var process = new ManagedProcess("PHP " + key, Path.Combine(_paths.LogsDir, $"php-{key}.log"), _job, _log)
                {
                    LogEnabled = settings.Logging.ModuleLogs,
                    MaxLogBytes = settings.Logging.MaxSizeMb * 1024L * 1024
                };
                process.StateChanged += () => Changed?.Invoke();
                var created = new PoolEntry(key, port, process);
                _pool[key] = created;
                start.Add(created);
            }
        }

        foreach (var entry in stop)
        {
            entry.Process.Stop();
            entry.Process.Dispose();
        }

        foreach (var entry in start)
        {
            StartPoolProcess(settings, entry.Key, entry.Port, entry.Process);
        }

        if (stop.Count > 0 || start.Count > 0)
        {
            Changed?.Invoke();
        }
    }

    private void StartPoolProcess(WebitSettings settings, PhpProcessKey key, int port, ManagedProcess process)
    {
        var exe = PhpExecutableFor(key.Branch);
        if (exe is null)
        {
            process.Fail(LocalizedMessage.Of(MessageCodes.ModuleNotInstalled, "PHP " + key.Branch));
            return;
        }

        if (OperatingSystem.IsWindows() && !HasVcRuntime(exe))
        {
            process.Fail(LocalizedMessage.Of(MessageCodes.VcRuntimeMissing, VcRedistInstaller ?? "vc_redist.x64.exe"));
            return;
        }

        if (!PortProbe.CanBind("127.0.0.1", port, out _))
        {
            process.Fail(LocalizedMessage.Of(MessageCodes.PortInUse, $"127.0.0.1:{port}", "?", "?"));
            return;
        }

        string ini;
        try
        {
            ini = PhpIni.Write(_paths, settings, exe, key.Xdebug ? XdebugDll(key.Branch) : null, $"php-{key}.ini");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not write php.ini for PHP {Key}.", key);
            process.Fail(LocalizedMessage.Of(MessageCodes.ModuleStartFailed, ex.Message));
            return;
        }

        var alias = "WEBit-PHP-" + key.Branch.Replace('.', '_') + (key.Xdebug ? "-Xdebug" : "");
        var psi = new ProcessStartInfo(Alias(exe, alias)) { WorkingDirectory = Path.GetDirectoryName(exe)! };
        psi.ArgumentList.Add("-b");
        psi.ArgumentList.Add($"127.0.0.1:{port}");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(ini);
        psi.Environment["PHP_FCGI_MAX_REQUESTS"] = "0";
        psi.Environment["PHPRC"] = Path.GetDirectoryName(ini)!;
        _log.LogInformation("Starting PHP {Key} on 127.0.0.1:{Port}.", key, port);
        process.Start(psi);
    }

    /// <summary>Stops pool processes (before their files change, PHP switched off, restart).</summary>
    private void StopPool(Func<PhpProcessKey, bool> match)
    {
        List<PoolEntry> stop;
        lock (_poolLock)
        {
            stop = _pool.Values.Where(e => match(e.Key)).ToList();
            foreach (var entry in stop)
            {
                _pool.Remove(entry.Key);
            }
        }

        foreach (var entry in stop)
        {
            entry.Process.Stop();
            entry.Process.Dispose();
        }

        if (stop.Count > 0)
        {
            Changed?.Invoke();
        }
    }

    private List<ManagedProcess> PoolProcesses()
    {
        lock (_poolLock)
        {
            return _pool.Values.Select(e => e.Process).ToList();
        }
    }

    /// <summary>" · 8.2 :47206 · 8.4-xdebug :47207" for the PHP module card.</summary>
    private string PoolEndpoints()
    {
        lock (_poolLock)
        {
            return string.Concat(_pool.Values.OrderBy(e => e.Port).Select(e => $" · {e.Key} :{e.Port}"));
        }
    }

    /// <summary>A PHP site's process could not get a port.</summary>
    public bool IsPoolOverflow(SiteDefinition site)
    {
        lock (_poolLock)
        {
            return KeyFor(site) is { } key && _poolOverflow.Contains(key);
        }
    }
}
