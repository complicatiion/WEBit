using WEBit.Core.Models;
using WEBit.Core.Urls;

namespace WEBit.Contracts;

public sealed class HelloRequest
{
    public int ProtocolVersion { get; set; } = Protocol.Version;

    public string? ClientVersion { get; set; }
}

public sealed class HelloResponse
{
    public int ProtocolVersion { get; set; } = Protocol.Version;

    public string ServiceVersion { get; set; } = "";

    public bool IsWindowsService { get; set; }

    public string DataRoot { get; set; } = "";

    public string MachineName { get; set; } = "";
}

public enum EngineState
{
    Stopped,
    Starting,
    Running,
    Error,
    NotInstalled
}

public enum SiteRunState
{
    Stopped,
    Starting,
    Running,
    Error
}

public sealed class EngineStatus
{
    public EngineState State { get; set; }

    public string? Version { get; set; }

    public string? ExecutablePath { get; set; }

    public int? ProcessId { get; set; }

    public int AdminPort { get; set; }

    public LocalizedMessage? Message { get; set; }
}

public sealed class SiteStatus
{
    public SiteDefinition Site { get; set; } = new();

    public SiteRunState State { get; set; }

    public LocalizedMessage? Message { get; set; }

    public string PrimaryUrl { get; set; } = "";

    public List<SiteUrl> Urls { get; set; } = [];

    /// <summary>Node.js/Python: start command as WEBit runs it (detected or own).</summary>
    public string? AppCommand { get; set; }
}

public sealed class ServiceStatus
{
    public string ServiceVersion { get; set; } = "";

    public bool IsWindowsService { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public string DataRoot { get; set; } = "";

    public string WwwRoot { get; set; } = "";

    public string LogsDir { get; set; } = "";

    public string MachineName { get; set; } = "";

    public string? Fqdn { get; set; }

    public EngineStatus Engine { get; set; } = new();

    public List<SiteStatus> Sites { get; set; } = [];

    public List<ModuleStatus> Modules { get; set; } = [];

    public List<ComponentStatus> Components { get; set; } = [];

    /// <summary>An update check is running.</summary>
    public bool CheckingUpdates { get; set; }

    public DateTimeOffset? LastUpdateCheck { get; set; }

    /// <summary>Branch of the default PHP module ("8.4"), null when not installed.</summary>
    public string? PhpDefaultVersion { get; set; }

    /// <summary>Additional PHP versions installed next to the default ("8.2", "8.3").</summary>
    public List<string> PhpVersions { get; set; } = [];

    public bool XdebugInstalled { get; set; }
}

public enum ModuleState
{
    Disabled,
    Stopped,
    Starting,
    Running,
    Error,
    NotInstalled,
    Planned,

    /// <summary>Runtime without own process (Node.js, Python): installed and switched on.</summary>
    Ready
}

/// <summary>Status of a service module (web server, Mailpit, planned runtimes).</summary>
public sealed class ModuleStatus
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public ModuleState State { get; set; }

    public bool Enabled { get; set; }

    /// <summary>False for planned or missing modules (switch is read-only).</summary>
    public bool CanToggle { get; set; }

    public string? Version { get; set; }

    public string? ExecutablePath { get; set; }

    public string? Endpoint { get; set; }

    /// <summary>Web UI of the module, if any.</summary>
    public string? Url { get; set; }

    public string License { get; set; } = "";

    public string? PlannedFor { get; set; }

    public LocalizedMessage? Message { get; set; }

    /// <summary>The service can download this module from its official source.</summary>
    public bool CanDownload { get; set; }

    /// <summary>Progress or result of the last download; null = none in this session.</summary>
    public ModuleDownloadStatus? Download { get; set; }

    /// <summary>PHP: the Visual C++ runtime is missing and its installer is ready (needs administrator rights).</summary>
    public string? VcRedistInstaller { get; set; }
}

/// <summary>How a component gets onto the machine.</summary>
public enum ComponentKind
{
    /// <summary>Part of the WEBit package (Caddy).</summary>
    Bundled,

    /// <summary>Downloaded by WEBit into data\modules (PHP, Mailpit).</summary>
    Download,

    /// <summary>Installed into Windows by its own installer (Visual C++ runtime).</summary>
    SystemWide,

    /// <summary>WEBit itself: new versions are offered on the release page.</summary>
    Application
}

/// <summary>Dependency shown on the dependencies page.</summary>
public sealed class ComponentStatus
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public ComponentKind Kind { get; set; }

    public bool Installed { get; set; }

    public string? Version { get; set; }

    /// <summary>Newest version of the last update check.</summary>
    public string? LatestVersion { get; set; }

    public bool UpdateAvailable { get; set; }

    /// <summary>Executable or installer on disk.</summary>
    public string? Path { get; set; }

    /// <summary>Official source (web page).</summary>
    public string Source { get; set; } = "";

    public string License { get; set; } = "";

    public bool CanDownload { get; set; }

    /// <summary>A downloaded copy exists that WEBit can remove.</summary>
    public bool CanRemove { get; set; }

    public ModuleDownloadStatus? Download { get; set; }
}

public enum ModuleDownloadState
{
    Running,
    Completed,
    Failed
}

public sealed class ModuleDownloadStatus
{
    public ModuleDownloadState State { get; set; }

    /// <summary>0–100; null while the size is unknown.</summary>
    public int? Percent { get; set; }

    /// <summary>Downloaded version, e.g. 8.5.12.</summary>
    public string? Version { get; set; }

    public LocalizedMessage? Message { get; set; }
}

/// <summary>MariaDB access for the UI (copy password, connection details).</summary>
public sealed class DbCredentials
{
    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; }

    public string User { get; set; } = "root";

    public string? Password { get; set; }

    public bool NetworkAccess { get; set; }
}

public sealed class CreateDatabaseRequest
{
    /// <summary>Database name; also the user name (letters, digits, underscore).</summary>
    public string Name { get; set; } = "";
}

public sealed class CreateDatabaseResult
{
    public string Database { get; set; } = "";

    public string User { get; set; } = "";

    public string Password { get; set; } = "";
}

/// <summary>PHP of the PHP module: version and extensions in ext\.</summary>
public sealed class PhpInfo
{
    public string? Version { get; set; }

    public List<string> Available { get; set; } = [];

    /// <summary>Extensions WEBit loads when nothing is chosen.</summary>
    public List<string> Defaults { get; set; } = [];
}

public static class SiteTemplates
{
    public const string WordPress = "wordpress";
    public const string Laravel = "laravel";
}

/// <summary>New site from a template (WordPress via WP-CLI, Laravel via Composer).</summary>
public sealed class TemplateRequest
{
    public string Template { get; set; } = SiteTemplates.WordPress;

    public string Name { get; set; } = "";

    public string? AdminUser { get; set; }

    public string? AdminEmail { get; set; }

    /// <summary>WordPress language, e.g. de_DE or en_US.</summary>
    public string? Locale { get; set; }
}

public sealed class TemplateResult
{
    public SiteDefinition Site { get; set; } = new();

    public string Url { get; set; } = "";

    public string? AdminUrl { get; set; }

    public string? AdminUser { get; set; }

    public string? AdminPassword { get; set; }

    public string? Database { get; set; }

    public string? DatabaseUser { get; set; }

    public string? DatabasePassword { get; set; }
}

public sealed class DatabaseInfo
{
    public string Name { get; set; } = "";

    public long SizeBytes { get; set; }

    /// <summary>Sites that use this database.</summary>
    public List<string> Sites { get; set; } = [];
}

public sealed class DatabaseList
{
    public bool Running { get; set; }

    public List<DatabaseInfo> Items { get; set; } = [];
}

/// <summary>Backup of one database (Name) or all (null).</summary>
public sealed class BackupRequest
{
    public string? Name { get; set; }
}

/// <summary>Root certificate of the local HTTPS CA (PEM), for trust and export.</summary>
public sealed class CertificateInfo
{
    public bool Available { get; set; }

    public string? Pem { get; set; }

    public string? Subject { get; set; }

    public string? Thumbprint { get; set; }

    public DateTimeOffset? NotAfter { get; set; }
}

/// <summary>Sites as one ZIP: files, databases, optionally the WEBit settings.</summary>
public sealed class ExportRequest
{
    public List<string> SiteIds { get; set; } = [];

    public bool IncludeDatabases { get; set; } = true;

    /// <summary>node_modules, vendor, .venv: large, can be installed again.</summary>
    public bool IncludePackages { get; set; }

    public bool IncludeSettings { get; set; }

    /// <summary>Encrypts the ZIP (AES-256); null/empty = no password.</summary>
    public string? Password { get; set; }
}

public sealed class ExportResult
{
    /// <summary>ZIP in data\exports (the app copies it to the chosen place).</summary>
    public string Path { get; set; } = "";

    public long SizeBytes { get; set; }

    public int Sites { get; set; }

    public List<string> Warnings { get; set; } = [];
}

public sealed class ImportRequest
{
    /// <summary>ZIP in data\imports (copied there by the app).</summary>
    public string Path { get; set; } = "";

    /// <summary>Apply PHP, log and HTTPS settings contained in the export.</summary>
    public bool ApplySettings { get; set; }

    /// <summary>Password of an encrypted export.</summary>
    public string? Password { get; set; }
}

public sealed class ImportResult
{
    public List<string> Sites { get; set; } = [];

    public List<string> Warnings { get; set; } = [];
}

public sealed class BackupResult
{
    public string Path { get; set; } = "";
}

/// <summary>SQL dump in data\backups (manual) or data\backups\auto (scheduled).</summary>
public sealed class BackupInfo
{
    public string FileName { get; set; } = "";

    /// <summary>Database of the dump; null = all databases (not restorable from the app).</summary>
    public string? Database { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public long SizeBytes { get; set; }

    public bool Automatic { get; set; }
}

public sealed class BackupList
{
    public string Folder { get; set; } = "";

    public List<BackupInfo> Items { get; set; } = [];

    /// <summary>Next scheduled backup (automatic backups switched on), else null.</summary>
    public DateTimeOffset? NextAutomatic { get; set; }
}

/// <summary>One backup file, by name (no paths).</summary>
public sealed class BackupFileRequest
{
    public string FileName { get; set; } = "";

    public bool Automatic { get; set; }
}

public sealed class ModuleToggleRequest
{
    public string Id { get; set; } = "";

    public bool Enabled { get; set; }
}

public sealed class IdRequest
{
    public string Id { get; set; } = "";
}

public sealed class SaveSiteResult
{
    public SiteDefinition Site { get; set; } = new();

    public List<ValidationIssue> Issues { get; set; } = [];
}

public sealed class EndpointCheckRequest
{
    public string Address { get; set; } = "";

    public int Port { get; set; }

    public string? ExcludeSiteId { get; set; }
}

public sealed class SuggestPortRequest
{
    public string? Address { get; set; }

    public string? ExcludeSiteId { get; set; }

    /// <summary>Ports already used in the editor.</summary>
    public List<int> Reserved { get; set; } = [];
}

public sealed class SuggestPortResponse
{
    public int? Port { get; set; }
}

public sealed class HostAddress
{
    public string Address { get; set; } = "";

    public string InterfaceName { get; set; } = "";
}

public sealed class PortListener
{
    public string Address { get; set; } = "";

    public int Port { get; set; }

    public int ProcessId { get; set; }

    public string? ProcessName { get; set; }

    public bool IsEngine { get; set; }
}

public sealed class PortRange
{
    public int Start { get; set; }

    public int End { get; set; }
}

public sealed class NetworkInfo
{
    public string MachineName { get; set; } = "";

    public string? Fqdn { get; set; }

    public List<HostAddress> Addresses { get; set; } = [];

    public List<PortListener> Listeners { get; set; } = [];

    public List<PortRange> ExcludedRanges { get; set; } = [];

    public bool ListenerInfoAvailable { get; set; }
}

public sealed class UrlCheckRequest
{
    public List<string> Urls { get; set; } = [];
}

public sealed class UrlCheckResult
{
    public string Url { get; set; } = "";

    public IssueLevel DnsLevel { get; set; }

    public string? DnsText { get; set; }

    public IssueLevel HttpLevel { get; set; }

    public int? HttpStatus { get; set; }

    public long? ElapsedMs { get; set; }

    public string? Error { get; set; }
}

public sealed class LogRequest
{
    /// <summary>"service", "engine", "module:&lt;id&gt;" or "site:&lt;id&gt;".</summary>
    public string Source { get; set; } = "service";

    public int Lines { get; set; } = 300;
}

public sealed class LogResponse
{
    public string Source { get; set; } = "";

    public string? FilePath { get; set; }

    public List<string> Lines { get; set; } = [];
}

public sealed class PathRequest
{
    public string Path { get; set; } = "";

    public List<string>? DefaultDocuments { get; set; }
}
