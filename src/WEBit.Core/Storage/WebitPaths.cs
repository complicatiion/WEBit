namespace WEBit.Core.Storage;

/// <summary>
/// All WEBit paths. Installed: data in %ProgramData%\WEBit.
/// Portable (marker file WEBit.portable next to the exe): data in .\data.
/// </summary>
public sealed class WebitPaths
{
    public const string PortableMarker = "WEBit.portable";

    private WebitPaths(string baseDirectory, string dataRoot, bool isPortable)
    {
        BaseDirectory = baseDirectory;
        DataRoot = dataRoot;
        IsPortable = isPortable;
    }

    /// <summary>Program folder (executables and bin\*).</summary>
    public string BaseDirectory { get; }

    /// <summary>Root of all runtime data.</summary>
    public string DataRoot { get; }

    public bool IsPortable { get; }

    public string ConfigDir => Path.Combine(DataRoot, "config");

    public string SitesDir => Path.Combine(ConfigDir, "sites");

    public string SettingsFile => Path.Combine(ConfigDir, "settings.json");

    public string RuntimeDir => Path.Combine(DataRoot, "runtime");

    public string EngineRuntimeDir => Path.Combine(RuntimeDir, "caddy");

    public string EngineConfigFile => Path.Combine(EngineRuntimeDir, "caddy.json");

    /// <summary>Live reload: shared scripts in _assets, one state.json per site.</summary>
    public string LiveDir => Path.Combine(RuntimeDir, "live");

    public string LiveAssetsDir => Path.Combine(LiveDir, "_assets");

    public string LiveStateFile(string siteId) => Path.Combine(LiveDir, siteId, "state.json");

    /// <summary>Downloaded modules (PHP, Mailpit, VC++ installer): one folder per module.</summary>
    public string ModulesDir => Path.Combine(DataRoot, "modules");

    public string ModuleDir(string id) => Path.Combine(ModulesDir, id);

    public string PhpRuntimeDir => Path.Combine(RuntimeDir, "php");

    public string PhpIniFile => Path.Combine(PhpRuntimeDir, "php.ini");

    /// <summary>MariaDB: data folder, root password (DPAPI) and version marker; survive module updates.</summary>
    public string MariaDbDir => Path.Combine(DataRoot, "mariadb");

    public string MariaDbDataDir => Path.Combine(MariaDbDir, "data");

    public string MariaDbSecretFile => Path.Combine(MariaDbDir, "root.secret");

    public string MariaDbIniFile => Path.Combine(RuntimeDir, "mariadb", "my.ini");

    public string BackupsDir => Path.Combine(DataRoot, "backups");

    /// <summary>Scheduled MariaDB backups (oldest removed beyond the configured count).</summary>
    public string AutoBackupsDir => Path.Combine(BackupsDir, "auto");

    /// <summary>Site exports created by the service (the app copies them to the chosen place).</summary>
    public string ExportsDir => Path.Combine(DataRoot, "exports");

    /// <summary>ZIPs to import, copied here by the app (the service account cannot read user folders).</summary>
    public string ImportsDir => Path.Combine(DataRoot, "imports");

    /// <summary>Package caches of npm and uv (shared by all apps).</summary>
    public string CacheDir => Path.Combine(DataRoot, "cache");

    /// <summary>Python versions installed by uv.</summary>
    public string PythonInstallDir => Path.Combine(DataRoot, "runtimes", "python");

    public string LogsDir => Path.Combine(DataRoot, "logs");

    public string SiteLogsDir => Path.Combine(LogsDir, "sites");

    public string ServiceLogFile => Path.Combine(LogsDir, "service.log");

    public string EngineLogFile => Path.Combine(LogsDir, "engine.log");

    public string SetupLogFile => Path.Combine(LogsDir, "setup.log");

    public string PhpErrorLogFile => Path.Combine(LogsDir, "php-error.log");

    public string DefaultWwwDir => Path.Combine(DataRoot, "www");

    public string SiteLogDir(string siteId) => Path.Combine(SiteLogsDir, siteId);

    public string SiteAccessLog(string siteId) => Path.Combine(SiteLogDir(siteId), "access.log");

    /// <summary>Output of the Node.js or Python process of a site.</summary>
    public string SiteAppLog(string siteId) => Path.Combine(SiteLogDir(siteId), "app.log");

    /// <summary>Bundled engine path.</summary>
    /// <summary>Bundled web server; shipped as WEBit-Caddy.exe so it is easy to recognize in Task Manager.</summary>
    public string BundledEnginePath =>
        Path.Combine(BaseDirectory, "bin", "caddy", OperatingSystem.IsWindows() ? "WEBit-Caddy.exe" : "caddy");

    /// <summary>Original name (manual copies, older packages).</summary>
    public string BundledEngineLegacyPath =>
        Path.Combine(BaseDirectory, "bin", "caddy", OperatingSystem.IsWindows() ? "caddy.exe" : "caddy");

    public static WebitPaths Resolve(string? baseDirectory = null)
    {
        var baseDir = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        var portable = File.Exists(Path.Combine(baseDir, PortableMarker));
        var data = portable
            ? Path.Combine(baseDir, "data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WEBit");
        return new WebitPaths(baseDir, data, portable);
    }

    /// <summary>Tests only: custom data folder.</summary>
    public static WebitPaths ForDataRoot(string baseDirectory, string dataRoot) =>
        new(Path.GetFullPath(baseDirectory), Path.GetFullPath(dataRoot), isPortable: true);

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(SitesDir);
        Directory.CreateDirectory(RuntimeDir);
        Directory.CreateDirectory(EngineRuntimeDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(SiteLogsDir);
        Directory.CreateDirectory(DefaultWwwDir);
    }
}
