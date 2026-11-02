using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WEBit.Contracts;
using WEBit.Core.Models;
using WEBit.Core.Storage;

namespace WEBit.Service.Modules;

/// <summary>
/// One-click download of modules from their official sources into data\modules\&lt;id&gt;:
/// PHP (windows.php.net, SHA-256 from releases.json), Mailpit, Caddy and uv (GitHub release, SHA-256),
/// Node.js LTS (nodejs.org, SHASUMS256.txt), MariaDB LTS (downloads.mariadb.org REST API, SHA-256)
/// and, for PHP, the Visual C++ runtime installer (aka.ms, Microsoft Authenticode signature).
/// </summary>
public sealed partial class ModuleDownloader : IDisposable
{
    public const string VcRedistId = "vcredist";
    public const string VcRedistFile = "vc_redist.x64.exe";

    private const string PhpIndex = "https://windows.php.net/downloads/releases/releases.json";
    private const string PhpBase = "https://windows.php.net/downloads/releases/";
    private const string MailpitRelease = "https://api.github.com/repos/axllent/mailpit/releases/latest";
    private const string MailpitAsset = "mailpit-windows-amd64.zip";
    private const string CaddyRelease = "https://api.github.com/repos/caddyserver/caddy/releases/latest";
    private const string VcRedistUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe";
    private const string NodeIndex = "https://nodejs.org/dist/index.json";
    private const string NodeBase = "https://nodejs.org/dist/";
    private const string UvRelease = "https://api.github.com/repos/astral-sh/uv/releases/latest";
    private const string UvAsset = "uv-x86_64-pc-windows-msvc.zip";
    private const string MariaDbApi = "https://downloads.mariadb.org/rest-api/mariadb/";
    private const string ComposerBase = "https://getcomposer.org";
    private const string ComposerVersions = "https://getcomposer.org/versions";
    private const string WpCliRelease = "https://api.github.com/repos/wp-cli/wp-cli/releases/latest";
    private const string AdminerRelease = "https://api.github.com/repos/vrana/adminer/releases/latest";
    private const string XdebugPage = "https://xdebug.org/download";
    private const string WebitRelease = "https://api.github.com/repos/complicatiion/WEBit/releases/latest";

    /// <summary>Release page of the newest WEBit version (last update check).</summary>
    public string? WebitReleaseUrl { get; private set; }
    private const string XdebugBase = "https://xdebug.org";

    /// <summary>Modules with an update check, in display order.</summary>
    private static readonly string[] Checked =
        [ModuleIds.WebServer, ModuleIds.Php, ModuleIds.Mailpit, ModuleIds.MariaDb, ModuleIds.Node, ModuleIds.Python,
         ModuleIds.Composer, ModuleIds.WpCli, ModuleIds.Adminer, ModuleIds.Xdebug];

    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(400);

    private readonly WebitPaths _paths;
    private readonly ILogger<ModuleDownloader> _log;
    private readonly HttpClient _http;
    private readonly object _sync = new();
    private readonly Dictionary<string, ModuleDownloadStatus> _status = new(StringComparer.OrdinalIgnoreCase);

    public ModuleDownloader(WebitPaths paths, ILogger<ModuleDownloader> log)
    {
        _paths = paths;
        _log = log;

        // System proxy settings apply (HttpClient.DefaultProxy).
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WEBit", Sites.SiteManager.ServiceVersion));
    }

    /// <summary>Progress changed (throttled) or a download finished.</summary>
    public event Action? Changed;

    /// <summary>Raised before the module folder is replaced: the module must be stopped.</summary>
    public event Action<string>? Installing;

    /// <summary>Module folder replaced: start the module again.</summary>
    public event Action<string>? Installed;

    public static bool Supports(string id) =>
        id is ModuleIds.Php or ModuleIds.Mailpit or ModuleIds.WebServer or ModuleIds.Node or ModuleIds.Python or ModuleIds.MariaDb
            or ModuleIds.Composer or ModuleIds.WpCli or ModuleIds.Adminer or ModuleIds.Xdebug or VcRedistId
        || ModuleIds.IsPhpVersion(id);

    private List<string> _phpBranches = [];

    /// <summary>PHP branches with a Windows build (last update check), newest first.</summary>
    public IReadOnlyList<string> PhpBranches
    {
        get
        {
            lock (_sync)
            {
                return _phpBranches.ToList();
            }
        }
    }

    private readonly Dictionary<string, string> _latest = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Newest version found by the last update check (null = not checked).</summary>
    public string? Latest(string id)
    {
        lock (_sync)
        {
            return _latest.TryGetValue(id, out var v) ? v : null;
        }
    }

    public bool IsChecking { get; private set; }

    public DateTimeOffset? LastCheck { get; private set; }

    /// <summary>Looks up the newest versions of PHP and Mailpit (no download).</summary>
    public async Task CheckUpdatesAsync(CancellationToken ct)
    {
        if (IsChecking)
        {
            return;
        }

        IsChecking = true;
        Changed?.Invoke();
        try
        {
            foreach (var id in Checked)
            {
                try
                {
                    if (id == ModuleIds.Php)
                    {
                        // All branches at once: the default PHP and every additional version.
                        var builds = await ResolvePhpBuildsAsync(ct);
                        lock (_sync)
                        {
                            _phpBranches = builds.Select(b => b.Branch).ToList();
                            _latest[id] = builds[0].Package.Version;
                            foreach (var (branch, build) in builds)
                            {
                                _latest[ModuleIds.PhpVersion(branch)] = build.Version;
                            }
                        }

                        continue;
                    }

                    var package = id == ModuleIds.Xdebug ? (await ResolveXdebugAsync(ct)).Version : (await ResolveAsync(id, ct)).Version;
                    lock (_sync)
                    {
                        _latest[id] = package;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    _log.LogWarning(ex, "Update check for {Module} failed.", id);
                }
            }

            try
            {
                await CheckWebitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _log.LogWarning(ex, "Update check for WEBit failed.");
            }

            LastCheck = DateTimeOffset.Now;
        }
        finally
        {
            IsChecking = false;
            Changed?.Invoke();
        }
    }

    /// <summary>Newest WEBit release (tag vX.Y.Z); drafts and pre-releases are not returned by /latest.</summary>
    private async Task CheckWebitAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, WebitRelease);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // No release published yet.
            return;
        }

        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var version = Text(doc.RootElement, "tag_name")?.TrimStart('v');
        if (version is null)
        {
            return;
        }

        lock (_sync)
        {
            _latest[ModuleIds.Webit] = version;
        }

        WebitReleaseUrl = Text(doc.RootElement, "html_url");
    }

    public ModuleDownloadStatus? Get(string id)
    {
        lock (_sync)
        {
            return _status.TryGetValue(id, out var s)
                ? new ModuleDownloadStatus { State = s.State, Percent = s.Percent, Version = s.Version, Message = s.Message }
                : null;
        }
    }

    public bool IsRunning(string id)
    {
        lock (_sync)
        {
            return _status.TryGetValue(id, out var s) && s.State == ModuleDownloadState.Running;
        }
    }

    /// <summary>Starts the download in the background; progress arrives with the status.</summary>
    public void Start(string id, CancellationToken stopping)
    {
        if (!Supports(id))
        {
            throw new NotSupportedException($"Module '{id}' cannot be downloaded.");
        }

        lock (_sync)
        {
            if (_status.TryGetValue(id, out var current) && current.State == ModuleDownloadState.Running)
            {
                return;
            }

            _status[id] = new ModuleDownloadStatus
            {
                State = ModuleDownloadState.Running,
                Message = LocalizedMessage.Of(MessageCodes.DownloadResolving)
            };
        }

        Changed?.Invoke();
        _ = Task.Run(() => RunAsync(id, stopping));
    }

    private async Task RunAsync(string id, CancellationToken ct)
    {
        var work = Path.Combine(_paths.ModulesDir, ".download", id + "-" + Guid.NewGuid().ToString("N")[..8]);
        var stopped = false;
        try
        {
            Directory.CreateDirectory(work);
            if (id == VcRedistId)
            {
                // Installer only; the app runs it with administrator rights.
                await DownloadVcRedistAsync(ct, force: true);
                Update(id, s =>
                {
                    s.State = ModuleDownloadState.Completed;
                    s.Message = LocalizedMessage.Of(MessageCodes.DownloadCompleted, "14.x");
                });
                return;
            }

            var staging = Path.Combine(work, "extract");
            string version;
            if (id == ModuleIds.Xdebug)
            {
                version = await DownloadXdebugAsync(work, staging, ct);
            }
            else
            {
                var package = await ResolveAsync(id, ct);
                version = package.Version;
                Update(id, s => s.Version = package.Version);
                _log.LogInformation("Downloading {Module} {Version} from {Url}.", id, package.Version, package.Url);

                var zip = Path.Combine(work, package.FileName);
                await DownloadAsync(id, package.Url, zip, ct);

                Update(id, s => { s.Percent = 100; s.Message = LocalizedMessage.Of(MessageCodes.DownloadVerifying); });
                var actual = await HashAsync(zip, package.Sha256.Length == 128, ct);
                if (!string.Equals(actual, package.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new DownloadException(LocalizedMessage.Of(MessageCodes.DownloadChecksumMismatch, package.FileName));
                }

                Update(id, s => s.Message = LocalizedMessage.Of(MessageCodes.DownloadInstalling));
                if (package.InstallAs is { } single)
                {
                    // Single file (composer.phar, wp-cli.phar, Adminer): stored under a fixed name.
                    Directory.CreateDirectory(staging);
                    File.Copy(zip, Path.Combine(staging, single));
                }
                else
                {
                    ZipFile.ExtractToDirectory(zip, staging, overwriteFiles: true);
                }

                // Node.js and MariaDB zips contain one top folder (node-v22…-win-x64, mariadb-11…-winx64).
                staging = Unwrap(staging);
            }

            File.WriteAllText(Path.Combine(staging, ".version"), version);

            // PHP needs the VC++ runtime: fetch its installer before the swap. A failure here is not fatal.
            if ((id == ModuleIds.Php || ModuleIds.IsPhpVersion(id)) && OperatingSystem.IsWindows() && !ModuleManager.HasSystemVcRuntime())
            {
                try
                {
                    await DownloadVcRedistAsync(ct, force: false, progressId: id);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "Visual C++ runtime installer could not be downloaded.");
                }

                Update(id, s => { s.Percent = 100; s.Message = LocalizedMessage.Of(MessageCodes.DownloadInstalling); });
            }

            // Swap folders while the module is stopped; it is started again in any case (finally).
            // Set first: Installed must always follow (it releases the gate taken in Installing).
            stopped = true;
            Installing?.Invoke(id);
            var target = _paths.ModuleDir(id);
            ReplaceFolder(staging, target);
            _log.LogInformation("{Module} {Version} installed to {Target}.", id, version, target);

            Update(id, s =>
            {
                s.State = ModuleDownloadState.Completed;
                s.Message = LocalizedMessage.Of(MessageCodes.DownloadCompleted, version);
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Update(id, s => { s.State = ModuleDownloadState.Failed; s.Message = LocalizedMessage.Of(MessageCodes.DownloadFailed, "cancelled"); });
        }
        catch (OperationCanceledException)
        {
            // HttpClient timeout or stalled transfer.
            Update(id, s => { s.State = ModuleDownloadState.Failed; s.Message = LocalizedMessage.Of(MessageCodes.DownloadFailed, "timeout"); });
        }
        catch (DownloadException ex)
        {
            _log.LogWarning("Download of {Module} failed: {Message}", id, ex.Localized);
            Update(id, s => { s.State = ModuleDownloadState.Failed; s.Message = ex.Localized; });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Download of {Module} failed.", id);
            Update(id, s => { s.State = ModuleDownloadState.Failed; s.Message = LocalizedMessage.Of(MessageCodes.DownloadFailed, ex.Message); });
        }
        finally
        {
            TryDelete(work);
            Changed?.Invoke();
            if (stopped)
            {
                // New or (after a failed swap) old files: start the module again.
                Installed?.Invoke(id);
            }
        }
    }

    /// <summary>Removes leftovers of interrupted downloads (startup).</summary>
    public void CleanUp()
    {
        TryDelete(Path.Combine(_paths.ModulesDir, ".download"));
        if (!Directory.Exists(_paths.ModulesDir))
        {
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(_paths.ModulesDir, "*.old-*"))
        {
            TryDelete(dir);
        }
    }

    // ------------------------------------------------------------------ Sources

    /// <param name="InstallAs">Not an archive: file name in the module folder.</param>
    private sealed record Package(string Version, string Url, string FileName, string Sha256, string? InstallAs = null);

    private Task<Package> ResolveAsync(string id, CancellationToken ct) => id switch
    {
        ModuleIds.Php => ResolvePhpAsync(null, ct),
        _ when ModuleIds.PhpBranchOf(id) is { } branch => ResolvePhpAsync(branch, ct),
        ModuleIds.WebServer => ResolveCaddyAsync(ct),
        ModuleIds.Mailpit => ResolveMailpitAsync(ct),
        ModuleIds.Node => ResolveNodeAsync(ct),
        ModuleIds.Python => ResolveGitHubAsync(UvRelease, _ => [UvAsset], _ => UvAsset + ".sha256", ct),
        ModuleIds.MariaDb => ResolveMariaDbAsync(ct),
        ModuleIds.Composer => ResolveComposerAsync(ct),
        ModuleIds.WpCli => AsFile(ResolveGitHubAsync(WpCliRelease, v => [$"wp-cli-{v}.phar"], v => $"wp-cli-{v}.phar.sha512", ct), "wp-cli.phar"),
        ModuleIds.Adminer => AsFile(ResolveGitHubAsync(AdminerRelease, v => [$"adminer-{v}-mysql.php", $"adminer-{v}.php"], _ => "", ct), "index.php"),
        _ => throw new NotSupportedException($"Module '{id}' cannot be downloaded.")
    };

    private static async Task<Package> AsFile(Task<Package> resolve, string installAs) => (await resolve) with { InstallAs = installAs };

    /// <summary>Latest stable Composer (getcomposer.org/versions) with its published SHA-256.</summary>
    private async Task<Package> ResolveComposerAsync(CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await _http.GetStringAsync(ComposerVersions, ct));
        var stable = doc.RootElement.GetProperty("stable").EnumerateArray().First();
        var version = Text(stable, "version") ?? throw new DownloadException(LocalizedMessage.Of(MessageCodes.DownloadFailed, "no Composer version"));
        var path = Text(stable, "path") ?? $"/download/{version}/composer.phar";
        var url = ComposerBase + path;
        var sums = await _http.GetStringAsync(url + ".sha256sum", ct);
        var hash = FindHash(sums, "composer.phar") ?? throw new DownloadException(LocalizedMessage.Of(MessageCodes.DownloadFailed, "no checksum for composer.phar"));
        return new Package(version, url, "composer.phar", hash, "composer.phar");
    }

    /// <summary>Newest LTS release with a Windows x64 zip; SHA-256 from SHASUMS256.txt of the release.</summary>
    private async Task<Package> ResolveNodeAsync(CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await _http.GetStringAsync(NodeIndex, ct));
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            // "lts" is false or the code name; index.json is sorted newest first.
            var lts = release.TryGetProperty("lts", out var l) && l.ValueKind == JsonValueKind.String;
            var hasZip = release.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array
                         && files.EnumerateArray().Any(f => f.GetString() == "win-x64-zip");
            if (!lts || !hasZip || release.GetProperty("version").GetString() is not { } tag)
            {
                continue;
            }

            var file = $"node-{tag}-win-x64.zip";
            var sums = await _http.GetStringAsync($"{NodeBase}{tag}/SHASUMS256.txt", ct);
            var hash = FindHash(sums, file) ?? throw new DownloadException(LocalizedMessage.Of(MessageCodes.DownloadFailed, "no checksum for " + file));
            return new Package(tag.TrimStart('v'), $"{NodeBase}{tag}/{file}", file, hash);
        }

        throw new DownloadException(LocalizedMessage.Of(MessageCodes.DownloadFailed, "no Node.js LTS build in index.json"));
    }

    /// <summary>Newest stable long-term-support series, latest point release, winx64 zip with SHA-256.</summary>
    private async Task<Package> ResolveMariaDbAsync(CancellationToken ct)
    {
        using var majors = JsonDocument.Parse(await _http.GetStringAsync(MariaDbApi, ct));
        var series = majors.RootElement.GetProperty("major_releases").EnumerateArray()
            .Select(r => new
            {
                Id = Text(r, "release_id"),
                Status = Text(r, "release_status"),
                Support = Text(r, "release_support_type")
            })
            .Where(r => r.Id is not null && System.Version.TryParse(r.Id, out _)
                        && string.Equals(r.Status, "Stable", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Support?.Contains("Long Term", StringComparison.OrdinalIgnoreCase) == true)
            .ThenByDescending(r => System.Version.Parse(r.Id!))
            .Select(r => r.Id!)
            .ToList();

        foreach (var id in series.Take(3))
        {
            using var latest = JsonDocument.Parse(await _http.GetStringAsync($"{MariaDbApi}{id}/latest/", ct));
            if (!latest.RootElement.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var release in releases.EnumerateObject())
            {
                if (!release.Value.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var file in files.EnumerateArray())
                {
                    var name = Text(file, "file_name");
                    if (name is null || !name.EndsWith("-winx64.zip", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("debug", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var url = Text(file, "file_download_url");
                    var hash = file.TryGetProperty("checksum", out var sums) && sums.ValueKind == JsonValueKind.Object
                        ? Text(sums, "sha256sum") ?? Text(sums, "sha512sum")
                        : null;
                    if (url is null || hash is not { Length: 64 or 128 })
                    {
                        continue;
                    }

                    var version = Text(release.Value, "release_id") ?? release.Name;
                    return new Package(version, url, name, hash.ToLowerInvariant());
                }
            }
        }

        throw new DownloadException(LocalizedMessage.Of(MessageCodes.DownloadFailed, "no MariaDB Windows build found"));
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>"&lt;hash&gt;  &lt;file&gt;" (or "*file") line of a checksum list; a lone hash for single-file lists.</summary>
    private static string? FindHash(string text, string fileName)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var line = lines.FirstOrDefault(l => l.EndsWith(" " + fileName, StringComparison.Ordinal) || l.EndsWith("*" + fileName, StringComparison.Ordinal));
        var hash = line?.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        if (hash is null && lines.Count == 1 && lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: 1 } single)
        {
            hash = single[0];
        }

        return hash is { Length: 64 or 128 } && hash.All(Uri.IsHexDigit) ? hash.ToLowerInvariant() : null;
    }

    /// <summary>Archive with exactly one top folder and no files: that folder is the module.</summary>
    private static string Unwrap(string staging)
    {
        var dirs = Directory.GetDirectories(staging);
        return dirs.Length == 1 && Directory.GetFiles(staging).Length == 0 ? dirs[0] : staging;
    }

    /// <summary>Default PHP: newest branch; additional version: that branch. Non-thread-safe x64 build (nts-vs*-x64).</summary>
    private async Task<Package> ResolvePhpAsync(string? branch, CancellationToken ct)
    {
        var builds = await ResolvePhpBuildsAsync(ct);
        if (branch is null)
        {
            return builds[0].Package;
        }

        return builds.FirstOrDefault(b => b.Branch == branch).Package
               ?? throw new DownloadException(LocalizedMessage.Of(MessageCodes.DownloadFailed, $"no PHP {branch} build in releases.json"));
    }

    /// <summary>Every branch of releases.json with an nts x64 zip, newest first.</summary>
    private async Task<List<(string Branch, Package Package)>> ResolvePhpBuildsAsync(CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await _http.GetStringAsync(PhpIndex, ct));
        var branches = doc.RootElement.EnumerateObject()
            .Where(p => ModuleIds.IsPhpBranch(p.Name) && p.Value.ValueKind == JsonValueKind.Object)
            .OrderByDescending(p => System.Version.Parse(p.Name));

        var result = new List<(string Branch, Package Package)>();
        foreach (var branch in branches)
        {
            var build = branch.Value.EnumerateObject()
                .FirstOrDefault(p => p.Name.StartsWith("nts-vs", StringComparison.OrdinalIgnoreCase)
                                     && p.Name.EndsWith("-x64", StringComparison.OrdinalIgnoreCase));
            if (build.Value.ValueKind != JsonValueKind.Object
                || !build.Value.TryGetProperty("zip", out var zip)
                || !zip.TryGetProperty("path", out var path)
                || !zip.TryGetProperty("sha256", out var sha))
            {
                continue;
            }

            var version = branch.Value.TryGetProperty("version", out var v) ? v.GetString() ?? branch.Name : branch.Name;
            var file = path.GetString()!;
            result.Add((branch.Name, new Package(version, PhpBase + file, file, sha.GetString()!)));
        }

        return result.Count > 0
            ? result
            : throw new DownloadException(LocalizedMessage.Of(MessageCodes.DownloadFailed, "no PHP build in releases.json"));
    }

    /// <summary>Latest Mailpit release; SHA-256 from the GitHub asset digest or checksums.txt.</summary>
    private Task<Package> ResolveMailpitAsync(CancellationToken ct) =>
        ResolveGitHubAsync(MailpitRelease, _ => [MailpitAsset], _ => "checksums.txt", ct);

    /// <summary>Caddy (only needed when the bundled copy is missing, e.g. a source build without fetch).</summary>
    private Task<Package> ResolveCaddyAsync(CancellationToken ct) =>
        ResolveGitHubAsync(CaddyRelease, v => [$"caddy_{v}_windows_amd64.zip"], v => $"caddy_{v}_checksums.txt", ct);

    /// <summary>Latest GitHub release; SHA-256 from the asset digest, fallback: checksum file (SHA-256 or SHA-512).</summary>
    /// <param name="assetNames">Candidates in order of preference (first existing asset wins).</param>
    private async Task<Package> ResolveGitHubAsync(string releaseUrl, Func<string, string[]> assetNames, Func<string, string> checksumName, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, releaseUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        var version = doc.RootElement.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "?";
        var candidates = assetNames(version);
        var assets = doc.RootElement.GetProperty("assets").EnumerateArray().ToList();
        var name = candidates.FirstOrDefault(c => assets.Any(a => a.GetProperty("name").GetString() == c)) ?? candidates[0];
        var asset = assets.FirstOrDefault(a => a.GetProperty("name").GetString() == name);
        if (asset.ValueKind != JsonValueKind.Object)
        {
            throw new DownloadException(LocalizedMessage.Of(MessageCodes.DownloadFailed, name + " not found"));
        }

        var url = asset.GetProperty("browser_download_url").GetString()!;
        if (asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } d && d.StartsWith("sha256:", StringComparison.Ordinal))
        {
            return new Package(version, url, name, d["sha256:".Length..]);
        }

        // Fallback: checksum file of the release ("<hash>  <file>").
        var sumsName = checksumName(version);
        var sums = assets.FirstOrDefault(a => a.GetProperty("name").GetString() == sumsName);
        if (sums.ValueKind == JsonValueKind.Object)
        {
            var text = await _http.GetStringAsync(sums.GetProperty("browser_download_url").GetString(), ct);
            if (FindHash(text, name) is { } hash)
            {
                return new Package(version, url, name, hash);
            }
        }

        throw new DownloadException(LocalizedMessage.Of(MessageCodes.DownloadFailed, "no checksum for " + name));
    }

    /// <summary>Installer of the Visual C++ runtime (needs administrator rights, started from the app).</summary>
    private async Task DownloadVcRedistAsync(CancellationToken ct, bool force, string progressId = VcRedistId)
    {
        var target = Path.Combine(_paths.ModuleDir(VcRedistId), VcRedistFile);
        if (!force && File.Exists(target) && Platform.Windows.Authenticode.IsSignedBy(target, "Microsoft Corporation"))
        {
            return;
        }

        var temp = target + "." + Guid.NewGuid().ToString("N")[..8] + ".download";
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await DownloadAsync(progressId, VcRedistUrl, temp, ct);
        if (!Platform.Windows.Authenticode.IsSignedBy(temp, "Microsoft Corporation"))
        {
            TryDelete(temp);
            throw new DownloadException(LocalizedMessage.Of(MessageCodes.DownloadChecksumMismatch, VcRedistFile));
        }

        File.Move(temp, target, overwrite: true);
        _log.LogInformation("Visual C++ runtime installer downloaded to {Target}.", target);
    }

    // ------------------------------------------------------------------ Helpers

    private async Task DownloadAsync(string id, string url, string file, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;

        Update(id, s => { s.Percent = total is > 0 ? 0 : null; s.Message = LocalizedMessage.Of(MessageCodes.DownloadRunning); });
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = File.Create(file);

        var buffer = new byte[81920];
        long done = 0;
        var lastReport = DateTime.UtcNow;
        while (true)
        {
            // HttpClient.Timeout does not cover the body: every read must make progress within 60 s.
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stall.CancelAfter(TimeSpan.FromSeconds(60));
            var read = await source.ReadAsync(buffer, stall.Token);
            if (read == 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (total is > 0 && DateTime.UtcNow - lastReport > ProgressInterval)
            {
                lastReport = DateTime.UtcNow;
                var percent = (int)Math.Min(99, done * 100 / total.Value);
                Update(id, s => s.Percent = percent);
            }
        }
    }

    private static async Task<string> HashAsync(string file, bool sha512, CancellationToken ct)
    {
        await using var stream = File.OpenRead(file);
        var hash = sha512 ? await SHA512.HashDataAsync(stream, ct) : await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Old folder is moved aside first, so a failed copy never leaves a half module.</summary>
    private static void ReplaceFolder(string source, string target)
    {
        // Unique name: a locked leftover never blocks the next update.
        var backup = target + ".old-" + Guid.NewGuid().ToString("N")[..8];
        if (Directory.Exists(target))
        {
            // The stopped process may still hold its files for a moment.
            MoveWithRetry(target, backup);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            // Virus scanners often hold freshly extracted DLLs briefly.
            MoveWithRetry(source, target);
        }
        catch
        {
            if (!Directory.Exists(target) && Directory.Exists(backup))
            {
                Directory.Move(backup, target);
            }

            throw;
        }

        TryDelete(backup);
    }

    private static void MoveWithRetry(string source, string target)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(source, target);
                return;
            }
            catch (Exception) when (attempt < 10)
            {
                Thread.Sleep(300);
            }
        }
    }

    private void Update(string id, Action<ModuleDownloadStatus> change)
    {
        lock (_sync)
        {
            if (_status.TryGetValue(id, out var status))
            {
                change(status);
            }
        }

        Changed?.Invoke();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Left for the next run.
        }
    }

    public void Dispose() => _http.Dispose();

    private sealed class DownloadException(LocalizedMessage message) : Exception(message.ToString())
    {
        public LocalizedMessage Localized { get; } = message;
    }
}
