namespace WEBit.Core.Models;

/// <summary>Known module ids.</summary>
public static class ModuleIds
{
    /// <summary>WEBit itself (update check against the GitHub releases).</summary>
    public const string Webit = "webit";

    public const string WebServer = "caddy";
    public const string Mailpit = "mailpit";
    public const string Php = "php";
    public const string Node = "node";
    public const string Python = "python";
    public const string MariaDb = "mariadb";

    /// <summary>Tools (no own process): Composer, WP-CLI (PHP archives) and Adminer (served by Caddy + PHP).</summary>
    public const string Composer = "composer";
    public const string WpCli = "wpcli";
    public const string Adminer = "adminer";

    /// <summary>Xdebug DLLs for every installed PHP version (no own process; loaded by a separate php-cgi).</summary>
    public const string Xdebug = "xdebug";

    /// <summary>Additional PHP versions: "php-8.2" (data\modules\php-8.2) next to the default "php".</summary>
    public const string PhpVersionPrefix = "php-";

    public static string PhpVersion(string branch) => PhpVersionPrefix + branch;

    /// <summary>"php-8.2" → "8.2"; null for anything else.</summary>
    public static string? PhpBranchOf(string id) =>
        id.StartsWith(PhpVersionPrefix, StringComparison.OrdinalIgnoreCase) && IsPhpBranch(id[PhpVersionPrefix.Length..])
            ? id[PhpVersionPrefix.Length..]
            : null;

    public static bool IsPhpVersion(string id) => PhpBranchOf(id) is not null;

    /// <summary>"8.2": major.minor, digits only.</summary>
    public static bool IsPhpBranch(string? branch)
    {
        var parts = branch?.Split('.') ?? [];
        return parts.Length == 2 && parts.All(p => p.Length is > 0 and <= 2 && p.All(char.IsAsciiDigit));
    }

    /// <summary>"8.4.14" → "8.4".</summary>
    public static string? BranchOfVersion(string? version)
    {
        var parts = version?.Split('.', '-', ' ') ?? [];
        return parts.Length >= 2 && IsPhpBranch(parts[0] + "." + parts[1]) ? parts[0] + "." + parts[1] : null;
    }
}

/// <summary>PHP settings shown in the app (php.ini is generated from them).</summary>
public sealed partial class PhpSettings
{
    /// <summary>Extensions loaded when their DLL exists; null = WEBit defaults.</summary>
    public List<string>? Extensions { get; set; }

    public string MemoryLimit { get; set; } = "512M";

    public string UploadMaxFilesize { get; set; } = "128M";

    public string PostMaxSize { get; set; } = "128M";

    public int MaxExecutionTime { get; set; } = 120;

    public bool DisplayErrors { get; set; } = true;

    /// <summary>OPcache (revalidates on every request, so edits stay visible).</summary>
    public bool Opcache { get; set; } = true;

    /// <summary>Port of the IDE (Xdebug step debugging) on 127.0.0.1.</summary>
    public int XdebugPort { get; set; } = 9003;

    public static readonly string[] DefaultExtensions =
    [
        "curl", "fileinfo", "gd", "gettext", "intl", "mbstring", "exif", "openssl",
        "mysqli", "pdo_mysql", "pdo_sqlite", "sqlite3", "sodium", "zip", "bz2"
    ];

    [System.Text.RegularExpressions.GeneratedRegex(@"^(-1|\d{1,5}[KMG]?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SizeRegex();

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> EffectiveExtensions => (IReadOnlyList<string>?)Extensions ?? DefaultExtensions;

    public void Normalize()
    {
        MemoryLimit = Size(MemoryLimit, "512M");
        UploadMaxFilesize = Size(UploadMaxFilesize, "128M");
        PostMaxSize = Size(PostMaxSize, "128M");
        MaxExecutionTime = Math.Clamp(MaxExecutionTime, 0, 3600);
        if (XdebugPort is < 1024 or > 65535) XdebugPort = 9003;
        Extensions = Extensions?
            .Select(e => e.Trim().ToLowerInvariant())
            .Where(e => e.Length > 0 && e.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            .Distinct()
            .ToList();
    }

    private static string Size(string? value, string fallback) =>
        value is not null && SizeRegex().IsMatch(value.Trim()) ? value.Trim().ToUpperInvariant() : fallback;
}

/// <summary>Scheduled MariaDB backups (all databases, one .sql per database) into data\backups\mariadb.</summary>
public sealed class DbBackupSettings
{
    public bool Enabled { get; set; }

    public int IntervalHours { get; set; } = 24;

    /// <summary>Automatic backups kept per database (oldest removed); manual backups are never removed.</summary>
    public int Keep { get; set; } = 7;

    public void Normalize()
    {
        IntervalHours = Math.Clamp(IntervalHours, 1, 24 * 30);
        Keep = Math.Clamp(Keep, 1, 365);
    }
}

/// <summary>What the service logs and how much it keeps.</summary>
public sealed class LogSettings
{
    /// <summary>Service log (logs\service.log).</summary>
    public bool ServiceLog { get; set; } = true;

    /// <summary>Debug, Information, Warning or Error.</summary>
    public string Level { get; set; } = "Information";

    /// <summary>Access log per site (logs\sites\&lt;id&gt;\access.log).</summary>
    public bool AccessLogs { get; set; } = true;

    /// <summary>Output of the modules and apps (php.log, mailpit.log, mariadb.log, app.log per site).</summary>
    public bool ModuleLogs { get; set; } = true;

    /// <summary>Size of one log file before it is rolled over.</summary>
    public int MaxSizeMb { get; set; } = 10;

    /// <summary>Rolled-over files kept per log.</summary>
    public int Keep { get; set; } = 5;

    public void Normalize()
    {
        Level = Level is "Debug" or "Information" or "Warning" or "Error" ? Level : "Information";
        MaxSizeMb = Math.Clamp(MaxSizeMb, 1, 500);
        Keep = Math.Clamp(Keep, 0, 50);
    }
}

/// <summary>Service settings (config/settings.json).</summary>
public sealed class WebitSettings
{
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public int SitePortStart { get; set; } = 47100;

    public int SitePortEnd { get; set; } = 47199;

    /// <summary>Loopback range for modules and backends; apps get ports from InternalPortStart + 10.</summary>
    public int InternalPortStart { get; set; } = 47200;

    public int InternalPortEnd { get; set; } = 47289;

    /// <summary>Engine admin API port (127.0.0.1 only).</summary>
    public int EngineAdminPort { get; set; } = 47299;

    public int MailpitSmtpPort { get; set; } = 47201;

    public int MailpitUiPort { get; set; } = 47202;

    /// <summary>PHP FastCGI port (127.0.0.1 only).</summary>
    public int PhpFastCgiPort { get; set; } = 47203;

    /// <summary>Adminer (database UI) port.</summary>
    public int AdminerPort { get; set; } = 47205;

    /// <summary>Adminer also on the default network address (default: 127.0.0.1 only).</summary>
    public bool AdminerNetworkAccess { get; set; }

    /// <summary>Automatic MariaDB backups (off by default; manual backups always possible).</summary>
    public DbBackupSettings DbBackup { get; set; } = new();

    /// <summary>FastCGI ports of additional PHP processes (other versions, Xdebug): InternalPortStart + 6 … + 9.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<int> PhpPoolPorts => Enumerable.Range(InternalPortStart + 6, 4).Where(p => p <= InternalPortEnd).ToList();

    public PhpSettings Php { get; set; } = new();

    /// <summary>HTTPS bindings allowed: Caddy issues certificates from its local CA (trust it once per device).</summary>
    public bool HttpsEnabled { get; set; }

    /// <summary>MariaDB port.</summary>
    public int MariaDbPort { get; set; } = 47204;

    /// <summary>MariaDB also listens on all network addresses (default: 127.0.0.1 only).</summary>
    public bool MariaDbNetworkAccess { get; set; }

    /// <summary>Quick test: every new folder in the www root becomes a site with live reload.</summary>
    public bool AutoSites { get; set; } = true;

    public LogSettings Logging { get; set; } = new();

    /// <summary>www folders whose automatic site was deleted; not recreated.</summary>
    public List<string> IgnoredAutoFolders { get; set; } = [];

    /// <summary>Default IP for new sites. Empty = first IPv4 address.</summary>
    public string? DefaultBindAddress { get; set; }

    /// <summary>DNS suffix for URL suggestions.</summary>
    public string? DnsSuffix { get; set; }

    /// <summary>Default folder for new sites. Empty = data\www.</summary>
    public string? WwwRoot { get; set; }

    /// <summary>Path to caddy.exe. Empty = bundled engine.</summary>
    public string? EnginePath { get; set; }

    /// <summary>Module on/off switches.</summary>
    public Dictionary<string, bool> Modules { get; set; } = DefaultModules();

    /// <summary>Extra user SIDs allowed on the control pipe (set by the installer).</summary>
    public List<string> AllowedUserSids { get; set; } = [];

    public static Dictionary<string, bool> DefaultModules() => new(StringComparer.OrdinalIgnoreCase)
    {
        [ModuleIds.WebServer] = true,
        [ModuleIds.Mailpit] = false,
        [ModuleIds.Php] = true,
        [ModuleIds.Node] = false,
        [ModuleIds.Python] = false,
        [ModuleIds.MariaDb] = false,
        [ModuleIds.Adminer] = false
    };

    public bool IsModuleEnabled(string id) =>
        Modules.TryGetValue(id, out var enabled) ? enabled : DefaultModules().GetValueOrDefault(id);

    public void Normalize()
    {
        AllowedUserSids ??= [];
        IgnoredAutoFolders ??= [];
        Logging ??= new LogSettings();
        Logging.Normalize();
        Php ??= new PhpSettings();
        Php.Normalize();
        DbBackup ??= new DbBackupSettings();
        DbBackup.Normalize();

        // Existing installations (schema < 3): automatic sites stay off until switched on,
        // so an update does not turn every www folder into a site.
        if (SchemaVersion < 3)
        {
            AutoSites = false;
        }

        SchemaVersion = CurrentSchemaVersion;
        var modules = DefaultModules();
        foreach (var pair in Modules ?? [])
        {
            modules[pair.Key] = pair.Value;
        }

        Modules = modules;
        foreach (var pair in DefaultModules())
        {
            Modules.TryAdd(pair.Key, pair.Value);
        }

        if (SitePortStart is < 1 or > 65535) SitePortStart = 47100;
        if (SitePortEnd < SitePortStart || SitePortEnd > 65535) SitePortEnd = Math.Min(65535, SitePortStart + 99);
        if (InternalPortStart is < 1 or > 65535) InternalPortStart = 47200;
        if (InternalPortEnd < InternalPortStart || InternalPortEnd > 65535) InternalPortEnd = Math.Min(65535, InternalPortStart + 89);
        if (EngineAdminPort is < 1024 or > 65535) EngineAdminPort = 47299;
        if (MailpitSmtpPort is < 1024 or > 65535) MailpitSmtpPort = 47201;
        if (MailpitUiPort is < 1024 or > 65535 || MailpitUiPort == MailpitSmtpPort) MailpitUiPort = 47202;
        if (PhpFastCgiPort is < 1024 or > 65535 || PhpFastCgiPort == MailpitSmtpPort || PhpFastCgiPort == MailpitUiPort) PhpFastCgiPort = 47203;
        if (AdminerPort is < 1024 or > 65535 || AdminerPort == MariaDbPort || AdminerPort == PhpFastCgiPort
            || AdminerPort == MailpitSmtpPort || AdminerPort == MailpitUiPort || AdminerPort == EngineAdminPort) AdminerPort = 47205;
        if (MariaDbPort is < 1024 or > 65535 || MariaDbPort == MailpitSmtpPort || MariaDbPort == MailpitUiPort || MariaDbPort == PhpFastCgiPort) MariaDbPort = 47204;
        DefaultBindAddress = string.IsNullOrWhiteSpace(DefaultBindAddress) ? null : DefaultBindAddress.Trim();
        DnsSuffix = string.IsNullOrWhiteSpace(DnsSuffix) ? null : DnsSuffix.Trim().Trim('.').ToLowerInvariant();
        WwwRoot = string.IsNullOrWhiteSpace(WwwRoot) ? null : WwwRoot.Trim();
        EnginePath = string.IsNullOrWhiteSpace(EnginePath) ? null : EnginePath.Trim();
    }
}
