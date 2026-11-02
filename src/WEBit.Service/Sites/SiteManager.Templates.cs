using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WEBit.Contracts;
using WEBit.Core.Models;
using WEBit.Core.Net;
using WEBit.Core.Sites;
using WEBit.Core.Urls;
using WEBit.Service.Modules;

namespace WEBit.Service.Sites;

/// <summary>New sites from templates (WordPress via WP-CLI, Laravel via Composer) and database actions.</summary>
public sealed partial class SiteManager
{
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromMinutes(20);

    [GeneratedRegex(@"^APP_URL=.*$", RegexOptions.Multiline)]
    private static partial Regex AppUrlRegex();

    /// <summary>
    /// Creates folder, database and site in one go. Long running (downloads); not under the gate except for the final save.
    /// Anything created is removed again when a step fails.
    /// </summary>
    public async Task<TemplateResult> CreateFromTemplateAsync(TemplateRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length == 0)
        {
            throw new ArgumentException("Name required.");
        }

        var template = request.Template?.Trim().ToLowerInvariant();
        if (template is not (SiteTemplates.WordPress or SiteTemplates.Laravel))
        {
            throw new NotSupportedException($"Unknown template '{request.Template}'.");
        }

        // Prerequisites first, so nothing is created that cannot be finished.
        _ = _tools.PhpCli;
        string? server = null;
        if (template == SiteTemplates.WordPress)
        {
            _ = _modules.WpCliPhar ?? throw new InvalidOperationException("WP-CLI is not installed (Modules → WP-CLI).");
            server = RunningMariaDb();
        }
        else
        {
            _ = _modules.ComposerPhar ?? throw new InvalidOperationException("Composer is not installed (Modules → Composer).");
        }

        var root = Path.Combine(WwwRoot, SiteFolders.FromName(name));
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw new InvalidOperationException($"The folder {root} exists and is not empty.");
        }

        string id;
        lock (_stateLock)
        {
            id = SiteIds.CreateUnique(name, _sites.Keys);
        }

        var address = _settings.DefaultBindAddress ?? HostNetwork.FirstIPv4() ?? "127.0.0.1";
        var port = SuggestPort(new SuggestPortRequest { Address = address }).Port
                   ?? throw new InvalidOperationException("No free port in the site range.");
        var binding = new SiteBinding { Address = address, Port = port };
        var url = UrlBuilder.ForBinding(binding, new UrlContext(HostNetwork.GetFqdn(), _settings.DnsSuffix)).TrimEnd('/');
        var logFile = _paths.SiteAppLog(id);

        var createdFolder = !Directory.Exists(root);
        string? createdDatabase = null;
        var result = new TemplateResult { Url = url + "/" };
        var site = new SiteDefinition
        {
            Id = id,
            Name = name,
            Type = SiteType.Php,
            Framework = template,
            Bindings = [binding]
        };

        SetBusyFolder(root, busy: true);
        try
        {
            if (template == SiteTemplates.WordPress)
            {
                await Task.Run(() => CreateWordPress(request, server!, root, url, logFile, site, result, out createdDatabase), ct);
            }
            else
            {
                await Task.Run(() => CreateLaravel(root, url, logFile, site), ct);
            }

            // Not cancelled by a disconnecting app: the installation is done, the site must exist.
            var saved = await SaveSiteAsync(site, CancellationToken.None, insertOnly: true);
            result.Site = saved.Site;
        }
        catch
        {
            // A site that was saved before the error keeps its folder and database.
            bool saved;
            lock (_stateLock)
            {
                saved = _sites.Values.Any(s => string.Equals(NormalizePath(s.Root), NormalizePath(site.Root ?? ""), StringComparison.OrdinalIgnoreCase)
                                               && !string.IsNullOrEmpty(site.Root));
            }

            if (!saved)
            {
                CleanUpTemplate(root, createdFolder, server, createdDatabase);
            }

            throw;
        }
        finally
        {
            SetBusyFolder(root, busy: false);
        }

        _log.LogInformation("Site {Name} created from template {Template}.", name, template);
        return result;
    }

    private void CreateWordPress(TemplateRequest request, string server, string root, string url, string logFile,
        SiteDefinition site, TemplateResult result, out string? createdDatabase)
    {
        createdDatabase = null;
        var db = _modules.Database.CreateDatabase(server, _settings, _modules.Database.FreeName(server, _settings, "wp_" + SiteFolders.FromName(site.Name)));
        createdDatabase = db.Database;

        var locale = string.IsNullOrWhiteSpace(request.Locale) ? "en_US" : request.Locale.Trim();
        Check(_tools.WpCli(_settings, WwwRoot, logFile, ToolTimeout, "core", "download", $"--path={root}", $"--locale={locale}"), "wp core download");
        Check(_tools.WpCli(_settings, root, logFile, ToolTimeout, "config", "create", $"--path={root}",
            $"--dbname={db.Database}", $"--dbuser={db.User}", $"--dbpass={db.Password}", $"--dbhost=127.0.0.1:{_settings.MariaDbPort}",
            "--dbcharset=utf8mb4", $"--locale={locale}", "--skip-check"), "wp config create");
        AddWordPressSettings(Path.Combine(root, "wp-config.php"));

        var user = string.IsNullOrWhiteSpace(request.AdminUser) ? "admin" : request.AdminUser.Trim();
        var email = string.IsNullOrWhiteSpace(request.AdminEmail) ? "admin@webit.test" : request.AdminEmail.Trim();
        var password = MariaDb.GeneratePassword(16);
        Check(_tools.WpCli(_settings, root, logFile, ToolTimeout, "core", "install", $"--path={root}", $"--url={url}",
            $"--title={site.Name}", $"--admin_user={user}", $"--admin_password={password}", $"--admin_email={email}", "--skip-email"),
            "wp core install");

        site.Root = root;
        site.Database = db.Database;
        site.Description = "WordPress";
        result.AdminUrl = url + "/wp-admin/";
        result.AdminUser = user;
        result.AdminPassword = password;
        result.Database = db.Database;
        result.DatabaseUser = db.User;
        result.DatabasePassword = db.Password;
    }

    /// <summary>
    /// WordPress follows the address it is opened with (IP, host name, port, HTTPS) – otherwise it redirects to the
    /// install URL after every port or address change. Plugins and updates write directly.
    /// </summary>
    internal static void AddWordPressSettings(string wpConfig)
    {
        const string marker = "/* WEBit */";
        var text = File.ReadAllText(wpConfig);
        if (text.Contains(marker, StringComparison.Ordinal))
        {
            return;
        }

        // Guarded: existing definitions of an imported site win.
        const string extra = """
            /* WEBit */
            if ( ! empty( $_SERVER['HTTP_HOST'] ) && ! defined( 'WP_HOME' ) ) {
                $webit_https = ( ! empty( $_SERVER['HTTPS'] ) && 'off' !== $_SERVER['HTTPS'] );
                define( 'WP_HOME', ( $webit_https ? 'https://' : 'http://' ) . $_SERVER['HTTP_HOST'] );
                defined( 'WP_SITEURL' ) || define( 'WP_SITEURL', WP_HOME );
            }
            defined( 'WP_ENVIRONMENT_TYPE' ) || define( 'WP_ENVIRONMENT_TYPE', 'local' );
            defined( 'FS_METHOD' ) || define( 'FS_METHOD', 'direct' );

            """;

        // Before "That's all, stop editing!" (or the settings include as fallback).
        var anchor = text.IndexOf("/* That's all", StringComparison.Ordinal);
        if (anchor < 0)
        {
            anchor = text.IndexOf("require_once ABSPATH", StringComparison.Ordinal);
        }

        text = anchor < 0 ? text + Environment.NewLine + extra : text.Insert(anchor, extra.ReplaceLineEndings("\n"));
        File.WriteAllText(wpConfig, text, WebitJsonEncoding.Utf8NoBom);
    }

    private void CreateLaravel(string root, string url, string logFile, SiteDefinition site)
    {
        Check(_tools.Composer(_settings, WwwRoot, logFile, ToolTimeout,
            "create-project", "laravel/laravel", root, "--prefer-dist", "--no-interaction", "--no-progress"), "composer create-project");

        // URLs generated on the console (mails, queues) use APP_URL.
        var env = Path.Combine(root, ".env");
        if (File.Exists(env))
        {
            var text = File.ReadAllText(env);
            text = AppUrlRegex().IsMatch(text) ? AppUrlRegex().Replace(text, "APP_URL=" + url) : text + Environment.NewLine + "APP_URL=" + url;
            File.WriteAllText(env, text, WebitJsonEncoding.Utf8NoBom);
        }

        site.Root = Path.Combine(root, "public");
        site.Description = "Laravel";
        site.LiveReload = false;
    }

    private static void Check(Tools.PhpTools.Result result, string step)
    {
        if (!result.Success)
        {
            throw new InvalidOperationException($"{step}: {Tools.PhpTools.LastLine(result.Output)}");
        }
    }

    private void CleanUpTemplate(string root, bool createdFolder, string? server, string? database)
    {
        if (database is not null && server is not null)
        {
            try
            {
                _modules.Database.DropDatabase(server, _settings, database);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not remove database {Database} after a failed template.", database);
            }
        }

        if (createdFolder && Directory.Exists(root))
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not remove {Root} after a failed template.", root);
            }
        }
    }

    // ------------------------------------------------------------------ Databases page

    public DatabaseList GetDatabases()
    {
        if (_modules.MariaDbExecutable is not { } exe || !_modules.IsMariaDbRunning)
        {
            return new DatabaseList { Running = false };
        }

        List<SiteDefinition> sites;
        lock (_stateLock)
        {
            sites = _sites.Values.ToList();
        }

        return new DatabaseList
        {
            Running = true,
            Items = _modules.Database.ListDatabases(exe, _settings)
                .Select(d => new DatabaseInfo
                {
                    Name = d.Name,
                    SizeBytes = d.Size,
                    Sites = sites.Where(s => string.Equals(s.Database, d.Name, StringComparison.OrdinalIgnoreCase)).Select(s => s.Name).ToList()
                })
                .ToList()
        };
    }

    public Task DropDatabaseAsync(IdRequest request, CancellationToken ct)
    {
        var server = RunningMariaDb();
        var name = request.Id?.Trim() ?? "";
        return Task.Run(() => _modules.Database.DropDatabase(server, _settings, name), ct);
    }

    /// <summary>composer install for PHP sites with composer.json (project folder = root or its parent for …\public).</summary>
    private async Task<bool> ComposerInstallAsync(SiteDefinition site, CancellationToken ct)
    {
        var project = ProjectDir(site) ?? throw new InvalidOperationException("No composer.json in the site folder.");
        var result = await Task.Run(() => _tools.ComposerWith(_settings, site.PhpVersion, project, _paths.SiteAppLog(site.Id), ToolTimeout, "install", "--no-interaction", "--no-progress"), ct);
        return result.Success;
    }

    /// <summary>Folder with composer.json: the site root or, for Laravel-style …\public roots, its parent.</summary>
    internal static string? ProjectDir(SiteDefinition site)
    {
        if (string.IsNullOrWhiteSpace(site.Root))
        {
            return null;
        }

        var root = site.Root.TrimEnd('\\', '/');
        if (File.Exists(Path.Combine(root, "composer.json")))
        {
            return root;
        }

        // Only a "public" document root belongs to the project above it (never the whole www folder).
        var isPublic = string.Equals(Path.GetFileName(root), "public", StringComparison.OrdinalIgnoreCase)
                       || site.Framework == SiteTemplates.Laravel;
        var parent = Path.GetDirectoryName(root);
        return isPublic && parent is not null && File.Exists(Path.Combine(parent, "composer.json")) ? parent : null;
    }
}
