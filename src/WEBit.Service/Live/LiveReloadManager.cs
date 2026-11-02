using System.Text.Json;
using Microsoft.Extensions.Logging;
using WEBit.Core.Models;
using WEBit.Core.Serialization;
using WEBit.Core.Storage;

namespace WEBit.Service.Live;

/// <summary>
/// Watches the folders of running live-reload sites and bumps their state.json on changes.
/// The browser (live.html / livereload.js) polls that file through Caddy.
/// </summary>
public sealed class LiveReloadManager : IDisposable
{
    // Folders apps write to at runtime (would reload the page in a loop) and tooling folders.
    private static readonly string[] IgnoredSegments =
    [
        ".git", ".svn", ".vs", ".idea", "node_modules", "vendor", "_vti_cnf", "_vti_pvt",
        "cache", "logs", "log", "tmp", "temp", "storage", "sessions", "uploads",
        ".venv", "__pycache__", ".next", ".nuxt", ".svelte-kit", ".astro", ".turbo"
    ];

    private static readonly string[] IgnoredSuffixes =
    [
        "~", ".tmp", ".swp", ".swx", ".crdownload", ".part",
        ".log", ".sqlite", ".sqlite3", ".db", ".db-journal", ".db-wal", ".db-shm", ".lock"
    ];

    private readonly WebitPaths _paths;
    private readonly ILogger<LiveReloadManager> _log;
    private readonly object _sync = new();
    private readonly Dictionary<string, Watch> _watches = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Interval and delay per live site; written into state.json for the browser.</summary>
    private readonly Dictionary<string, (int Interval, int Delay)> _options = new(StringComparer.OrdinalIgnoreCase);

    public LiveReloadManager(WebitPaths paths, ILogger<LiveReloadManager> log)
    {
        _paths = paths;
        _log = log;
    }

    /// <summary>Writes live.html and livereload.js (every start, so updates take effect).</summary>
    public void EnsureAssets()
    {
        try
        {
            Directory.CreateDirectory(_paths.LiveAssetsDir);
            File.WriteAllText(Path.Combine(_paths.LiveAssetsDir, "live.html"), LiveAssets.LiveHtml, WebitJson.Utf8NoBom);
            File.WriteAllText(Path.Combine(_paths.LiveAssetsDir, "livereload.js"), LiveAssets.LiveReloadJs, WebitJson.Utf8NoBom);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not write live reload assets.");
        }
    }

    /// <summary>
    /// Running sites with live view: state.json (interval, delay) for all, a folder watcher for those with
    /// reload on file changes; stops all other watchers.
    /// </summary>
    public void Update(IEnumerable<SiteDefinition> sites)
    {
        var live = sites.Where(s => s.HasLiveView).ToList();
        var wanted = live
            .Where(s => s.LiveReload && s.HasRoot && Directory.Exists(s.Root))
            .ToDictionary(s => s.Id, s => s, StringComparer.OrdinalIgnoreCase);

        lock (_sync)
        {
            foreach (var id in _options.Keys.Where(id => live.All(s => !string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase))).ToList())
            {
                _options.Remove(id);
            }

            foreach (var site in live)
            {
                var options = (site.ReloadInterval, site.ReloadDelayMs);
                if (!_options.TryGetValue(site.Id, out var current) || current != options || !File.Exists(_paths.LiveStateFile(site.Id)))
                {
                    _options[site.Id] = options;
                    try
                    {
                        WriteState(site.Id, "full", null);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Live state for {Site} not written.", site.Name);
                    }
                }
            }

            foreach (var id in _watches.Keys.ToList())
            {
                if (!wanted.TryGetValue(id, out var site) || !string.Equals(_watches[id].Root, site.Root, StringComparison.OrdinalIgnoreCase))
                {
                    _watches[id].Dispose();
                    _watches.Remove(id);
                }
            }

            foreach (var site in wanted.Values.Where(s => !_watches.ContainsKey(s.Id)))
            {
                try
                {
                    _watches[site.Id] = new Watch(this, site.Id, site.Root);
                    _log.LogInformation("Live reload active for {Site} ({Root}).", site.Name, site.Root);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Live reload for {Site} not available.", site.Name);
                }
            }
        }
    }

    private void WriteState(string siteId, string kind, string? file)
    {
        var path = _paths.LiveStateFile(siteId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        (int Interval, int Delay) options;
        lock (_sync)
        {
            options = _options.TryGetValue(siteId, out var o) ? o : (0, 0);
        }

        var json = JsonSerializer.Serialize(new
        {
            version = DateTime.UtcNow.Ticks,
            kind,
            file,
            interval = options.Interval,
            delay = options.Delay
        });

        // Replace atomically so the browser never reads a half-written file.
        // The web server may be reading the file right now: retry briefly.
        var temp = path + ".tmp";
        File.WriteAllText(temp, json, WebitJson.Utf8NoBom);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(25 * attempt);
            }
            catch (UnauthorizedAccessException) when (attempt < 5)
            {
                Thread.Sleep(25 * attempt);
            }
        }
    }

    private static bool IsIgnored(string relative)
    {
        var parts = relative.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => IgnoredSegments.Contains(p, StringComparer.OrdinalIgnoreCase)))
        {
            return true;
        }

        return IgnoredSuffixes.Any(s => relative.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        lock (_sync)
        {
            foreach (var watch in _watches.Values)
            {
                watch.Dispose();
            }

            _watches.Clear();
        }
    }

    /// <summary>One watcher per site; changes are debounced and reported as one reload.</summary>
    private sealed class Watch : IDisposable
    {
        private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(200);

        private readonly LiveReloadManager _owner;
        private readonly string _siteId;
        private readonly FileSystemWatcher _watcher;
        private readonly Timer _timer;
        private readonly object _sync = new();
        private readonly HashSet<string> _changed = new(StringComparer.OrdinalIgnoreCase);

        public Watch(LiveReloadManager owner, string siteId, string root)
        {
            _owner = owner;
            _siteId = siteId;
            Root = root;
            _timer = new Timer(_ => Flush());
            _watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            _watcher.Changed += OnEvent;
            _watcher.Created += OnEvent;
            _watcher.Deleted += OnEvent;
            _watcher.Renamed += OnEvent;
            _watcher.Error += (_, _) => Touch("*");
            _watcher.EnableRaisingEvents = true;
        }

        public string Root { get; }

        private void OnEvent(object sender, FileSystemEventArgs e)
        {
            var relative = e.Name ?? "";
            if (!IsIgnored(relative))
            {
                Touch(relative);
            }
        }

        private void Touch(string relative)
        {
            lock (_sync)
            {
                _changed.Add(relative);
                _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
            }
        }

        private void Flush()
        {
            string[] files;
            lock (_sync)
            {
                files = _changed.ToArray();
                _changed.Clear();
            }

            if (files.Length == 0)
            {
                return;
            }

            // Only stylesheets changed: the page swaps CSS without a full reload.
            var kind = files.All(f => f.EndsWith(".css", StringComparison.OrdinalIgnoreCase)) ? "css" : "full";
            try
            {
                _owner.WriteState(_siteId, kind, files.Length == 1 ? files[0].Replace('\\', '/') : null);
            }
            catch (Exception ex)
            {
                _owner._log.LogDebug(ex, "Could not write live state for {Site}.", _siteId);
            }
        }

        public void Dispose()
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _timer.Dispose();
        }
    }
}
