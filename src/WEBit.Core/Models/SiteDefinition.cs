using System.Text.Json;
using WEBit.Core.Serialization;

namespace WEBit.Core.Models;

/// <summary>Kind of site.</summary>
public enum SiteType
{
    /// <summary>Static files (HTML, images, CSS, JS, documentation).</summary>
    Static,

    /// <summary>Redirect to another address.</summary>
    Redirect,

    /// <summary>PHP via the PHP module (FastCGI) plus static files.</summary>
    Php,

    /// <summary>Node.js app (dev server or own server) started by WEBit, behind Caddy.</summary>
    Node,

    /// <summary>Python app (Flask, Django, FastAPI …) started through uv, behind Caddy.</summary>
    Python,

    /// <summary>Reverse proxy to an address that runs elsewhere (own server, container).</summary>
    Proxy
}

/// <summary>Charset sent with HTML responses.</summary>
public enum CharsetMode
{
    /// <summary>No charset in the header; the page meta tag applies (best for legacy content).</summary>
    Auto,

    Utf8,

    /// <summary>Typical for FrontPage exports without meta tag.</summary>
    Windows1252,

    Iso88591
}

/// <summary>Listener of a site: IP, port and optional host name.</summary>
public sealed class SiteBinding
{
    /// <summary>"0.0.0.0" binds to all addresses (not recommended).</summary>
    public string Address { get; set; } = "0.0.0.0";

    public int Port { get; set; }

    /// <summary>Optional host name. Sites may share IP and port when host names differ.</summary>
    public string? Host { get; set; }

    /// <summary>HTTPS with a certificate of the local WEBit CA (Settings → HTTPS).</summary>
    public bool Tls { get; set; }

    public bool HasHost => !string.IsNullOrWhiteSpace(Host);

    public SiteBinding Clone() => new() { Address = Address, Port = Port, Host = Host, Tls = Tls };

    public override string ToString() => (Tls ? "https " : "") + (HasHost ? $"{Host} → {Address}:{Port}" : $"{Address}:{Port}");
}

public sealed class SiteContentOptions
{
    public List<string> DefaultDocuments { get; set; } = DefaultDocumentList();

    public bool DirectoryBrowsing { get; set; }

    public CharsetMode Charset { get; set; } = CharsetMode.Auto;

    public bool Compression { get; set; } = true;

    /// <summary>File and folder globs that are never served, at any depth.</summary>
    public List<string> HiddenPaths { get; set; } = DefaultHiddenPaths();

    public static List<string> DefaultDocumentList() =>
        ["index.html", "index.htm", "default.htm", "default.html", "index.php"];

    public static List<string> DefaultHiddenPaths() =>
        ["_vti_*", "_private", "_borders", "_derived", "_overlay", "_themes", "_fpclass", ".git", ".svn", "Thumbs.db", "desktop.ini",
         ".env", ".env.*", ".htpasswd"];
}

public sealed class SiteAccessOptions
{
    /// <summary>Allowed networks in CIDR notation. Empty = everyone.</summary>
    public List<string> AllowNetworks { get; set; } = [];
}

/// <summary>Node.js and Python apps: how WEBit starts and supervises the process.</summary>
public sealed class SiteAppOptions
{
    /// <summary>Own start command; empty = detected from the folder (package.json, manage.py, app.py …).</summary>
    public string? Command { get; set; }

    /// <summary>Internal port (127.0.0.1) the app listens on; passed as PORT. 0 = assigned on save.</summary>
    public int Port { get; set; }

    /// <summary>Continuous operation: restart automatically after a crash. Off = dev server for testing.</summary>
    public bool KeepAlive { get; set; }

    /// <summary>Restart the process when files in the folder change (servers without own reloader).</summary>
    public bool RestartOnChange { get; set; }

    /// <summary>Python version for uv (e.g. 3.13); empty = uv default.</summary>
    public string? PythonVersion { get; set; }

    /// <summary>Extra environment variables.</summary>
    public Dictionary<string, string> Environment { get; set; } = [];
}

/// <summary>Deep link to a sub page.</summary>
public sealed class SiteLink
{
    public string Label { get; set; } = "";

    public string Path { get; set; } = "/";
}

/// <summary>Complete configuration of one site (one card in the UI).</summary>
public sealed class SiteDefinition
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string? Description { get; set; }

    /// <summary>Card color (#RRGGBB); "random" is resolved to a palette color when the site is saved.</summary>
    public string Color { get; set; } = Sites.SiteColors.Random;

    /// <summary>Desired run state; evaluated at service start.</summary>
    public bool Enabled { get; set; } = true;

    public SiteType Type { get; set; } = SiteType.Static;

    public string Root { get; set; } = "";

    public string? RedirectTarget { get; set; }

    /// <summary>Reload the browser on file changes and send no-cache headers (quick testing).</summary>
    public bool LiveReload { get; set; }

    /// <summary>Auto reload: reload the browser every N seconds (0 = off); also without file changes (network shares, database content).</summary>
    public int ReloadInterval { get; set; }

    /// <summary>Wait after a file change before reloading (ms), so builds can finish.</summary>
    public int ReloadDelayMs { get; set; }

    /// <summary>Created automatically from a folder in the www root.</summary>
    public bool AutoCreated { get; set; }

    public bool RedirectPermanent { get; set; }

    /// <summary>Created from a template: "wordpress" or "laravel" (card actions, export/import).</summary>
    public string? Framework { get; set; }

    /// <summary>MariaDB database of the site (exported and imported with it).</summary>
    public string? Database { get; set; }

    /// <summary>Proxy sites: target address (http://127.0.0.1:3000/).</summary>
    public string? ProxyTarget { get; set; }

    /// <summary>PHP sites: PHP version ("8.2"); null = the default PHP module.</summary>
    public string? PhpVersion { get; set; }

    /// <summary>PHP sites: requests run through a php-cgi with Xdebug (step debugging, IDE on 127.0.0.1).</summary>
    public bool Xdebug { get; set; }

    public SiteAppOptions App { get; set; } = new();

    public List<SiteBinding> Bindings { get; set; } = [];

    public SiteContentOptions Content { get; set; } = new();

    public SiteAccessOptions Access { get; set; } = new();

    /// <summary>Address shared with users instead of the generated one (e.g. behind a load balancer).</summary>
    public string? PublicUrl { get; set; }

    public List<SiteLink> Links { get; set; } = [];

    /// <summary>Static and PHP sites serve files from <see cref="Root"/>.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ServesFiles => Type is SiteType.Static or SiteType.Php;

    /// <summary>The live view (/__webit/live.html) is available: file changes and/or interval.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasLiveView => Type != SiteType.Redirect && (LiveReload || ReloadInterval > 0);

    /// <summary>Node.js and Python sites: WEBit runs a process for them.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsApp => Type is SiteType.Node or SiteType.Python;

    /// <summary>Sites with a project folder (files or app).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasRoot => ServesFiles || IsApp;

    /// <summary>Module that runs the app (node, python) or null.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? AppModule => Type switch
    {
        SiteType.Node => ModuleIds.Node,
        SiteType.Python => ModuleIds.Python,
        _ => null
    };

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.Now;

    public SiteDefinition Clone()
    {
        var json = JsonSerializer.Serialize(this, WebitJson.Wire);
        return JsonSerializer.Deserialize<SiteDefinition>(json, WebitJson.Wire)!;
    }

    /// <summary>Replaces null lists and trims values (older or hand-edited files).</summary>
    public void Normalize()
    {
        Bindings ??= [];
        Links ??= [];
        Content ??= new SiteContentOptions();
        Content.DefaultDocuments ??= SiteContentOptions.DefaultDocumentList();
        Content.HiddenPaths ??= [];
        Access ??= new SiteAccessOptions();
        Access.AllowNetworks ??= [];
        ReloadInterval = ReloadInterval <= 0 ? 0 : Math.Clamp(ReloadInterval, 2, 3600);
        ReloadDelayMs = Math.Clamp(ReloadDelayMs, 0, 10000);
        App ??= new SiteAppOptions();
        App.Environment ??= [];
        App.Command = string.IsNullOrWhiteSpace(App.Command) ? null : App.Command.Trim();
        App.PythonVersion = string.IsNullOrWhiteSpace(App.PythonVersion) ? null : App.PythonVersion.Trim();
        ProxyTarget = string.IsNullOrWhiteSpace(ProxyTarget) ? null : ProxyTarget.Trim();
        PhpVersion = ModuleIds.IsPhpBranch(PhpVersion?.Trim()) ? PhpVersion!.Trim() : null;
        Database = string.IsNullOrWhiteSpace(Database) ? null : Database.Trim();
        Framework = string.IsNullOrWhiteSpace(Framework) ? null : Framework.Trim().ToLowerInvariant();
        Name = (Name ?? "").Trim();
        Root = (Root ?? "").Trim();
        foreach (var b in Bindings)
        {
            b.Address = string.IsNullOrWhiteSpace(b.Address) ? "0.0.0.0" : b.Address.Trim();
            b.Host = string.IsNullOrWhiteSpace(b.Host) ? null : b.Host.Trim().ToLowerInvariant();
        }
    }
}
