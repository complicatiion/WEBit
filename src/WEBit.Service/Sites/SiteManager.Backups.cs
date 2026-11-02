using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WEBit.Contracts;
using WEBit.Service.Modules;

namespace WEBit.Service.Sites;

/// <summary>MariaDB backups: list, restore, delete; scheduled backups (off by default) into data\backups\auto.</summary>
public sealed partial class SiteManager
{
    // <database>-yyyyMMdd-HHmmss.sql; all-databases-… / mariadb-… = dump of every database.
    [GeneratedRegex(@"^(?<db>[A-Za-z][A-Za-z0-9_]{0,63}|all-databases)-(?<ts>\d{8}-\d{6})\.sql$")]
    private static partial Regex BackupNameRegex();

    private static readonly TimeSpan BackupCheckInterval = TimeSpan.FromMinutes(10);

    private Timer? _backupTimer;

    /// <summary>Last scheduled run (also without databases), so an empty server is not checked every tick.</summary>
    private DateTimeOffset? _lastScheduledBackup;
    private readonly SemaphoreSlim _backupRun = new(1, 1);

    private void StartBackupSchedule() =>
        _backupTimer = new Timer(_ => _ = RunScheduledBackupAsync(), null, TimeSpan.FromMinutes(2), BackupCheckInterval);

    private void StopBackupSchedule()
    {
        _backupTimer?.Dispose();
        _backupTimer = null;
    }

    public BackupList GetBackups()
    {
        var items = new List<BackupInfo>();
        foreach (var (dir, automatic) in new[] { (_paths.BackupsDir, false), (_paths.AutoBackupsDir, true) })
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*.sql"))
            {
                if (ParseBackupName(file.Name) is not { } parsed)
                {
                    continue;
                }

                items.Add(new BackupInfo
                {
                    FileName = file.Name,
                    Database = parsed.Database,
                    CreatedAt = parsed.CreatedAt,
                    SizeBytes = file.Length,
                    Automatic = automatic
                });
            }
        }

        return new BackupList
        {
            Folder = _paths.BackupsDir,
            Items = items.OrderByDescending(i => i.CreatedAt).ToList(),
            NextAutomatic = _settings.DbBackup.Enabled ? NextScheduledBackup() : null
        };
    }

    internal static (string? Database, DateTimeOffset CreatedAt)? ParseBackupName(string name)
    {
        // Older versions named the dump of all databases "mariadb-<timestamp>.sql".
        var normalized = name.StartsWith("mariadb-", StringComparison.OrdinalIgnoreCase) ? "all-databases-" + name["mariadb-".Length..] : name;
        var match = BackupNameRegex().Match(normalized);
        if (!match.Success
            || !DateTime.TryParseExact(match.Groups["ts"].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var time))
        {
            return null;
        }

        var db = match.Groups["db"].Value;
        return (db == MariaDb.AllDatabasesName ? null : db, new DateTimeOffset(time));
    }

    /// <summary>Path of a listed backup; only names that match the backup pattern, never other paths.</summary>
    private string BackupPath(BackupFileRequest request)
    {
        var name = Path.GetFileName(request.FileName ?? "");
        if (name != request.FileName || ParseBackupName(name) is null)
        {
            throw new ArgumentException("Unknown backup.");
        }

        var path = Path.Combine(request.Automatic ? _paths.AutoBackupsDir : _paths.BackupsDir, name);
        return File.Exists(path) ? path : throw new InvalidOperationException("The backup no longer exists.");
    }

    public async Task RestoreBackupAsync(BackupFileRequest request, CancellationToken ct)
    {
        var path = BackupPath(request);
        if (ParseBackupName(Path.GetFileName(path))?.Database is not { } database)
        {
            throw new InvalidOperationException("A backup of all databases cannot be restored here – import it in Adminer as root.");
        }

        var server = RunningMariaDb();
        await Task.Run(() => _modules.Database.Restore(server, _settings, database, path), ct);
    }

    public void DeleteBackup(BackupFileRequest request)
    {
        File.Delete(BackupPath(request));
        _log.LogInformation("Backup {File} deleted.", request.FileName);
    }

    /// <summary>Newest automatic backup + interval (now when there is none).</summary>
    private DateTimeOffset NextScheduledBackup()
    {
        var newest = Directory.Exists(_paths.AutoBackupsDir)
            ? new DirectoryInfo(_paths.AutoBackupsDir).EnumerateFiles("*.sql").Select(f => (DateTimeOffset?)f.LastWriteTime).Max()
            : null;
        if (_lastScheduledBackup is { } run && (newest is null || run > newest))
        {
            newest = run;
        }

        return newest is { } last ? last + TimeSpan.FromHours(_settings.DbBackup.IntervalHours) : DateTimeOffset.Now;
    }

    /// <summary>Every user database into its own file; older automatic backups beyond "Keep" are removed per database.</summary>
    private async Task RunScheduledBackupAsync()
    {
        var backup = _settings.DbBackup;
        if (!backup.Enabled || !_modules.IsMariaDbRunning || _modules.MariaDbExecutable is not { } server
            || NextScheduledBackup() > DateTimeOffset.Now || !await _backupRun.WaitAsync(0))
        {
            return;
        }

        try
        {
            _lastScheduledBackup = DateTimeOffset.Now;
            var count = 0;
            await Task.Run(() =>
            {
                Directory.CreateDirectory(_paths.AutoBackupsDir);
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                foreach (var (name, _) in _modules.Database.ListDatabases(server, _settings))
                {
                    try
                    {
                        _modules.Database.Backup(server, _settings, name, Path.Combine(_paths.AutoBackupsDir, $"{name}-{stamp}.sql"));
                        PruneBackups(name, backup.Keep);
                        count++;
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Scheduled backup of {Database} failed.", name);
                    }
                }
            }, _stopping);
            if (count > 0)
            {
                _log.LogInformation("Scheduled MariaDB backup: {Count} database(s).", count);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Scheduled MariaDB backup failed.");
        }
        finally
        {
            _backupRun.Release();
        }
    }

    private void PruneBackups(string database, int keep)
    {
        var old = new DirectoryInfo(_paths.AutoBackupsDir).EnumerateFiles("*.sql")
            .Select(f => (File: f, Parsed: ParseBackupName(f.Name)))
            .Where(x => x.Parsed is { } p && p.Database == database)
            .OrderByDescending(x => x.Parsed!.Value.CreatedAt)
            .Skip(keep)
            .ToList();
        foreach (var (file, _) in old)
        {
            try
            {
                file.Delete();
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Old backup {File} not removed.", file.Name);
            }
        }
    }
}
