using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WEBit.Contracts;
using WEBit.Core.Models;
using WEBit.Core.Net;
using WEBit.Core.Serialization;
using WEBit.Core.Sites;

namespace WEBit.Service.Sites;

/// <summary>
/// Export and import of sites as one ZIP: webit-export.json (manifest), sites/&lt;id&gt;/files/… (project folder),
/// sites/&lt;id&gt;/database.sql (MariaDB dump without CREATE DATABASE) and optionally settings.json.
/// With a password the ZIP is AES-256 encrypted.
/// </summary>
public sealed partial class SiteManager
{
    private const string ManifestEntry = "webit-export.json";
    private const string SettingsEntry = "settings.json";
    private const string ExportFormat = "webit-site-export";

    /// <summary>Package folders that can be installed again (left out unless requested).</summary>
    private static readonly string[] PackageFolders = ["node_modules", "vendor", ".venv", "venv", "__pycache__"];

    /// <summary>Never exported.</summary>
    private static readonly string[] SkippedFolders = [".git", ".svn", ".vs", ".idea"];

    [GeneratedRegex(@"define\(\s*['""](?<key>DB_NAME|DB_USER|DB_PASSWORD|DB_HOST)['""]\s*,\s*(['""])[^'""]*\1\s*\)")]
    private static partial Regex WpDefineRegex();

    [GeneratedRegex(@"^(?<key>DB_DATABASE|DB_USERNAME|DB_PASSWORD|DB_HOST|DB_PORT)=.*$", RegexOptions.Multiline)]
    private static partial Regex EnvDbRegex();

    private sealed class ExportManifest
    {
        public string Format { get; set; } = ExportFormat;

        public int Version { get; set; } = 1;

        public string WebitVersion { get; set; } = "";

        public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.Now;

        public List<ExportedSite> Sites { get; set; } = [];
    }

    private sealed class ExportedSite
    {
        public SiteDefinition Site { get; set; } = new();

        /// <summary>Prefix of the project files in the ZIP (null = no files).</summary>
        public string? Files { get; set; }

        /// <summary>Site root relative to the project folder ("" or e.g. "public").</summary>
        public string RootPath { get; set; } = "";

        public string? Database { get; set; }
    }

    // ------------------------------------------------------------------ Export

    public async Task<ExportResult> ExportSitesAsync(ExportRequest request, CancellationToken ct)
    {
        List<SiteDefinition> sites;
        lock (_stateLock)
        {
            sites = request.SiteIds
                .Select(id => _sites.TryGetValue(id, out var s) ? s.Clone() : null)
                .OfType<SiteDefinition>()
                .ToList();
        }

        if (sites.Count == 0)
        {
            throw new InvalidOperationException("No sites selected.");
        }

        return await Task.Run(() => Export(sites, request, ct), ct);
    }

    private ExportResult Export(List<SiteDefinition> sites, ExportRequest request, CancellationToken ct)
    {
        Directory.CreateDirectory(_paths.ExportsDir);
        var name = sites.Count == 1 ? SiteFolders.FromName(sites[0].Name) : "sites";
        var file = Path.Combine(_paths.ExportsDir, $"webit-{name}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        var result = new ExportResult { Path = file };
        var manifest = new ExportManifest { WebitVersion = ServiceVersion };
        var server = _modules.MariaDbExecutable is { } exe && _modules.IsMariaDbRunning ? exe : null;

        try
        {
            using (var zip = new ExportArchiveWriter(file, request.Password))
            {
                foreach (var site in sites)
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = new ExportedSite { Site = site };
                    var prefix = $"sites/{site.Id}/";

                    // Project folder: the site root, or its parent for …\public roots (Laravel).
                    var content = site.HasRoot && Directory.Exists(site.Root)
                        ? (site.Type == SiteType.Php ? ProjectDir(site) : null) ?? site.Root
                        : null;
                    if (content is not null)
                    {
                        entry.Files = prefix + "files/";
                        var relative = Path.GetRelativePath(content, site.Root);
                        entry.RootPath = relative == "." ? "" : relative.Replace('\\', '/');
                        AddFolder(zip, content, entry.Files, request.IncludePackages, result.Warnings, ct);
                    }

                    if (request.IncludeDatabases && site.Database is { } database)
                    {
                        if (server is null)
                        {
                            result.Warnings.Add($"{site.Name}: MariaDB is not running – database {database} not exported.");
                        }
                        else
                        {
                            var dump = Path.Combine(_paths.ExportsDir, $".{site.Id}-{Guid.NewGuid():N}.sql");
                            try
                            {
                                _modules.Database.Backup(server, _settings, database, dump);
                                zip.AddFile(dump, prefix + "database.sql");
                                entry.Database = prefix + "database.sql";
                            }
                            catch (Exception ex)
                            {
                                result.Warnings.Add($"{site.Name}: database {database} not exported ({ex.Message}).");
                            }
                            finally
                            {
                                TryDeleteFile(dump);
                            }
                        }
                    }

                    manifest.Sites.Add(entry);
                }

                if (request.IncludeSettings)
                {
                    // Machine-specific values stay behind.
                    var settings = GetSettings();
                    settings.AllowedUserSids = [];
                    settings.IgnoredAutoFolders = [];
                    settings.WwwRoot = null;
                    settings.EnginePath = null;
                    settings.DefaultBindAddress = null;
                    zip.AddJson(SettingsEntry, settings);
                }

                zip.AddJson(ManifestEntry, manifest);
            }

            result.Sites = manifest.Sites.Count;
            result.SizeBytes = new FileInfo(file).Length;
            _log.LogInformation("Exported {Count} site(s) to {File}{Protected}.", result.Sites, file,
                string.IsNullOrEmpty(request.Password) ? "" : " (password protected)");
            return result;
        }
        catch
        {
            TryDeleteFile(file);
            throw;
        }
    }

    private static void AddFolder(ExportArchiveWriter zip, string root, string prefix, bool includePackages, List<string> warnings, CancellationToken ct)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            foreach (var sub in Directory.EnumerateDirectories(dir, "*", options))
            {
                var folder = Path.GetFileName(sub);
                if (SkippedFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)
                    || (!includePackages && PackageFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)))
                {
                    continue;
                }

                pending.Push(sub);
            }

            foreach (var path in Directory.EnumerateFiles(dir, "*", options))
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                try
                {
                    zip.AddFile(path, prefix + relative);
                }
                catch (IOException ex)
                {
                    // Locked files (open SQLite database, logs): the rest is exported.
                    warnings.Add($"{relative}: {ex.Message}");
                }
                catch (UnauthorizedAccessException ex)
                {
                    warnings.Add($"{relative}: {ex.Message}");
                }
            }
        }
    }

    // ------------------------------------------------------------------ Import

    public async Task<ImportResult> ImportSitesAsync(ImportRequest request, CancellationToken ct)
    {
        // Only ZIPs the app placed in data\imports: the service never reads arbitrary paths.
        var file = Path.GetFullPath(request.Path ?? "");
        var imports = Path.GetFullPath(_paths.ImportsDir) + Path.DirectorySeparatorChar;
        if (!file.StartsWith(imports, StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
        {
            throw new InvalidOperationException("Import file not found.");
        }

        var created = new List<ImportedItem>();
        try
        {
            var result = new ImportResult();
            var prepared = await Task.Run(() => PrepareImport(file, request.ApplySettings, request.Password, result, created, ct), ct);

            // Files and databases exist now: save without cancellation; on failure the unsaved rest is removed.
            foreach (var item in created.ToList())
            {
                var saved = await SaveSiteAsync(item.Site, CancellationToken.None, insertOnly: true);
                result.Sites.Add(saved.Site.Name);
                created.Remove(item);
                if (item.Folder is not null)
                {
                    SetBusyFolder(item.Folder, busy: false);
                }
            }

            if (request.ApplySettings && prepared.Settings is { } imported)
            {
                var settings = GetSettings();
                settings.Php = imported.Php;
                settings.Logging = imported.Logging;
                settings.HttpsEnabled = imported.HttpsEnabled;
                settings.DnsSuffix = imported.DnsSuffix ?? settings.DnsSuffix;
                await SaveSettingsAsync(settings, ct);
            }

            _log.LogInformation("Imported {Count} site(s) from {File}.", result.Sites.Count, file);
            return result;
        }
        catch
        {
            foreach (var item in created)
            {
                // Skip a site whose save went through before the error.
                bool saved;
                lock (_stateLock)
                {
                    saved = item.Folder is not null && _sites.Values.Any(s => NormalizePath(s.Root).StartsWith(NormalizePath(item.Folder), StringComparison.OrdinalIgnoreCase));
                }

                if (!saved)
                {
                    CleanUpTemplate(item.Folder ?? "", item.Folder is not null, item.Server, item.Database);
                }
            }

            throw;
        }
        finally
        {
            foreach (var item in created.Where(i => i.Folder is not null))
            {
                SetBusyFolder(item.Folder!, busy: false);
            }

            TryDeleteFile(file);
        }
    }

    private sealed record PreparedImport(WebitSettings? Settings);

    /// <summary>What an imported site created before it is saved (removed again on failure).</summary>
    private sealed class ImportedItem(SiteDefinition site)
    {
        public SiteDefinition Site { get; } = site;

        public string? Folder { get; set; }

        public string? Database { get; set; }

        public string? Server { get; set; }
    }

    private PreparedImport PrepareImport(string file, bool applySettings, string? password, ImportResult result, List<ImportedItem> created,
        CancellationToken ct)
    {
        using var zip = new ExportArchiveReader(file, password);
        var manifest = zip.ReadJson<ExportManifest>(ManifestEntry);
        if (manifest is null || manifest.Format != ExportFormat)
        {
            throw new InvalidOperationException("Not a WEBit export (webit-export.json missing).");
        }

        var settings = zip.Exists(SettingsEntry) ? zip.ReadJson<WebitSettings>(SettingsEntry) : null;
        settings?.Normalize();
        var server = _modules.MariaDbExecutable is { } exe && _modules.IsMariaDbRunning ? exe : null;
        var reserved = new List<int>();
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_stateLock)
        {
            usedIds.UnionWith(_sites.Keys);
        }

        foreach (var exported in manifest.Sites)
        {
            ct.ThrowIfCancellationRequested();
            var site = exported.Site;
            site.Normalize();
            site.Id = SiteIds.CreateUnique(site.Name, usedIds);
            usedIds.Add(site.Id);
            site.AutoCreated = false;
            site.App.Port = 0;
            var item = new ImportedItem(site);
            created.Add(item);

            // Apps run their command at once: imported switched off, so the user sees it first.
            site.Enabled = !site.IsApp;
            if (site.IsApp)
            {
                result.Warnings.Add($"{site.Name}: imported switched off – start command: {site.App.Command ?? "detected from the folder"}.");
            }

            // Files: into a new folder in www named after the site.
            if (exported.Files is { } prefix)
            {
                var folder = Path.GetFullPath(Path.Combine(WwwRoot, SiteFolders.Suggest(WwwRoot, SiteFolders.FromName(site.Name))));
                item.Folder = folder;
                SetBusyFolder(folder, busy: true);
                Extract(zip, prefix, folder);
                site.Root = string.IsNullOrEmpty(exported.RootPath)
                    ? folder
                    : Path.GetFullPath(Path.Combine(folder, exported.RootPath));
                if (!site.Root.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    site.Root = folder;
                }
            }
            else if (site.HasRoot)
            {
                result.Warnings.Add($"{site.Name}: no files in the export.");
            }

            // Addresses of this computer, free ports.
            foreach (var binding in site.Bindings)
            {
                var local = Endpoint.IsWildcard(binding.Address) || Endpoint.IsLoopback(binding.Address)
                            || HostNetwork.GetIPv4Addresses().Any(a => a.Address == binding.Address);
                if (!local)
                {
                    binding.Address = _settings.DefaultBindAddress ?? HostNetwork.FirstIPv4() ?? "127.0.0.1";
                }

                if (binding.Tls && !_settings.HttpsEnabled && !(applySettings && settings?.HttpsEnabled == true))
                {
                    binding.Tls = false;
                    result.Warnings.Add($"{site.Name}: HTTPS is off in the settings – imported as HTTP.");
                }

                var port = SuggestPort(new SuggestPortRequest { Address = binding.Address, Reserved = reserved }).Port;
                if (port is { } free)
                {
                    binding.Port = free;
                    reserved.Add(free);
                }
            }

            ImportDatabase(zip, exported, item, server, result);
        }

        return new PreparedImport(settings);
    }

    /// <summary>New database (free name) with the dump; WordPress/Laravel configuration follows the new credentials.</summary>
    private void ImportDatabase(ExportArchiveReader zip, ExportedSite exported, ImportedItem item, string? server, ImportResult result)
    {
        var site = item.Site;
        if (exported.Database is not { } dumpEntry || !zip.Exists(dumpEntry))
        {
            site.Database = null;
            return;
        }

        if (server is null)
        {
            result.Warnings.Add($"{site.Name}: MariaDB is not running – database not imported.");
            site.Database = null;
            return;
        }

        var temp = Path.Combine(_paths.ImportsDir, $".{Guid.NewGuid():N}.sql");
        try
        {
            zip.ExtractToFile(dumpEntry, temp);
            var db = _modules.Database.CreateDatabase(server, _settings, _modules.Database.FreeName(server, _settings, site.Database ?? site.Name));
            item.Database = db.Database;
            item.Server = server;
            _modules.Database.Import(server, _settings, db, temp);
            site.Database = db.Database;
            UpdateDatabaseConfig(site, db);
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"{site.Name}: database import failed ({ex.Message}). Dumps with DEFINER routines/triggers of other accounts cannot be imported safely – import them in Adminer as root.");
            if (item.Database is { } failed)
            {
                // No half-filled database: the site is imported without one.
                try
                {
                    _modules.Database.DropDatabase(server, _settings, failed);
                }
                catch (Exception)
                {
                    // Reported above.
                }

                item.Database = null;
            }

            site.Database = null;
        }
        finally
        {
            TryDeleteFile(temp);
        }
    }

    private void UpdateDatabaseConfig(SiteDefinition site, CreateDatabaseResult db)
    {
        var host = $"127.0.0.1:{_settings.MariaDbPort}";
        var wpConfig = Path.Combine(site.Root, "wp-config.php");
        if (File.Exists(wpConfig))
        {
            var text = WpDefineRegex().Replace(File.ReadAllText(wpConfig), m => m.Groups["key"].Value switch
            {
                "DB_NAME" => $"define( 'DB_NAME', '{db.Database}' )",
                "DB_USER" => $"define( 'DB_USER', '{db.User}' )",
                "DB_PASSWORD" => $"define( 'DB_PASSWORD', '{db.Password}' )",
                _ => $"define( 'DB_HOST', '{host}' )"
            });
            File.WriteAllText(wpConfig, text, WebitJsonEncoding.Utf8NoBom);
            AddWordPressSettings(wpConfig);
        }

        var env = ProjectDir(site) is { } project ? Path.Combine(project, ".env") : null;
        if (env is not null && File.Exists(env))
        {
            var text = File.ReadAllText(env);
            if (Regex.IsMatch(text, @"^DB_CONNECTION=(mysql|mariadb)\s*$", RegexOptions.Multiline))
            {
                text = EnvDbRegex().Replace(text, m => m.Groups["key"].Value switch
                {
                    "DB_DATABASE" => "DB_DATABASE=" + db.Database,
                    "DB_USERNAME" => "DB_USERNAME=" + db.User,
                    "DB_PASSWORD" => "DB_PASSWORD=" + db.Password,
                    "DB_HOST" => "DB_HOST=127.0.0.1",
                    _ => "DB_PORT=" + _settings.MariaDbPort
                });
                File.WriteAllText(env, text, WebitJsonEncoding.Utf8NoBom);
            }
        }
    }

    /// <summary>Entries below prefix into target; paths that leave the target (zip slip) are skipped.</summary>
    private static void Extract(ExportArchiveReader zip, string prefix, string target)
    {
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        foreach (var name in zip.FileNames.Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            var relative = name[prefix.Length..];
            if (relative.Length == 0 || relative.EndsWith('/'))
            {
                continue;
            }

            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            zip.ExtractToFile(name, path);
        }
    }

    private static void TryDeleteFile(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
        catch (Exception)
        {
            // Left for the next cleanup.
        }
    }
}
