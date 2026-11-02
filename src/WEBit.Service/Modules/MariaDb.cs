using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WEBit.Contracts;
using WEBit.Core.Diagnostics;
using WEBit.Core.Models;
using WEBit.Core.Storage;
using WEBit.Platform.Windows;

namespace WEBit.Service.Modules;

/// <summary>
/// MariaDB around the server process: data folder (data\mariadb\data, survives updates), generated root
/// password (DPAPI), my.ini, clean shutdown, upgrade after a new version, databases and backups.
/// </summary>
public sealed partial class MariaDb(WebitPaths paths, ILogger log)
{
    private const string PasswordChars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
    private const string PipeName = "WEBit-MariaDB";

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,31}$")]
    private static partial Regex NameRegex();

    /// <summary>System databases and accounts are never created or granted.</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "mysql", "sys", "information_schema", "performance_schema", "root", "mariadb", "test", "PUBLIC"
    };

    public static bool IsValidName(string? name) => name is not null && NameRegex().IsMatch(name) && !ReservedNames.Contains(name);

    /// <summary>Server executable in bin (mariadbd.exe, older builds mysqld.exe).</summary>
    public static string? FindServer(string moduleDir) => FindTool(Path.Combine(moduleDir, "bin"), "mariadbd", "mysqld");

    public static string? FindTool(string binDir, params string[] names)
    {
        foreach (var name in names)
        {
            var file = Path.Combine(binDir, OperatingSystem.IsWindows() ? name + ".exe" : name);
            if (File.Exists(file))
            {
                return file;
            }
        }

        return null;
    }

    private static string? Tool(string server, params string[] names) => FindTool(Path.GetDirectoryName(server)!, names);

    public bool IsInitialized => Directory.Exists(paths.MariaDbDataDir) && Directory.EnumerateFileSystemEntries(paths.MariaDbDataDir).Any();

    /// <summary>Root password, null when the secret is missing or belongs to another computer.</summary>
    public string? RootPassword
    {
        get
        {
            try
            {
                return File.Exists(paths.MariaDbSecretFile) ? Dpapi.Unprotect(File.ReadAllText(paths.MariaDbSecretFile).Trim()) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public static string GeneratePassword(int length = 24) => RandomNumberGenerator.GetString(PasswordChars, length);

    /// <summary>Creates the data folder with a generated root password on first start; null = ready.</summary>
    public LocalizedMessage? EnsureInitialized(string server, WebitSettings settings)
    {
        if (IsInitialized)
        {
            if (RootPassword is null)
            {
                log.LogWarning("MariaDB: root password not available ({File}); databases cannot be managed from WEBit.", paths.MariaDbSecretFile);
            }

            return null;
        }

        var installDb = Tool(server, "mariadb-install-db", "mysql_install_db");
        if (installDb is null)
        {
            return LocalizedMessage.Of(MessageCodes.DbInitFailed, "mariadb-install-db not found");
        }

        var password = GeneratePassword();
        Directory.CreateDirectory(paths.MariaDbDir);
        TryDeleteDirectory(paths.MariaDbDataDir);

        log.LogInformation("MariaDB: creating data folder {Dir}.", paths.MariaDbDataDir);
        var run = ProcessRunner.Run(
            installDb,
            [$"--datadir={paths.MariaDbDataDir}", $"--password={password}", $"--port={settings.MariaDbPort}"],
            TimeSpan.FromMinutes(3),
            Path.GetDirectoryName(installDb));
        if (!run.Success || !IsInitialized)
        {
            var reason = run.TimedOut ? "timeout" : LastLine(run.Output) ?? $"exit {run.ExitCode}";
            log.LogError("MariaDB: initialization failed: {Output}", run.Output);
            TryDeleteDirectory(paths.MariaDbDataDir);
            return LocalizedMessage.Of(MessageCodes.DbInitFailed, reason);
        }

        File.WriteAllText(paths.MariaDbSecretFile, Dpapi.Protect(password), WEBit.Core.Serialization.WebitJson.Utf8NoBom);
        log.LogInformation("MariaDB: data folder created; root password stored encrypted.");
        return null;
    }

    /// <summary>Writes runtime\mariadb\my.ini (port, bind address, data folder) and returns its path.</summary>
    public string WriteIni(string server, WebitSettings settings)
    {
        static string P(string path) => "\"" + path.Replace('\\', '/') + "\"";

        var baseDir = Path.GetDirectoryName(Path.GetDirectoryName(server)!)!;
        var bind = settings.MariaDbNetworkAccess ? "0.0.0.0" : "127.0.0.1";
        var ini = $"""
            # Written by WEBit at every start – change port and network access in WEBit (Settings).
            [mysqld]
            basedir={P(baseDir)}
            datadir={P(paths.MariaDbDataDir)}
            port={settings.MariaDbPort}
            bind-address={bind}
            socket={PipeName}
            character-set-server=utf8mb4
            collation-server=utf8mb4_unicode_ci

            [client]
            port={settings.MariaDbPort}
            socket={PipeName}
            default-character-set=utf8mb4
            """;

        Directory.CreateDirectory(Path.GetDirectoryName(paths.MariaDbIniFile)!);
        File.WriteAllText(paths.MariaDbIniFile, ini.ReplaceLineEndings("\r\n"), WEBit.Core.Serialization.WebitJson.Utf8NoBom);
        return paths.MariaDbIniFile;
    }

    /// <summary>Asks the server to shut down (InnoDB stays consistent); false when it could not be asked.</summary>
    public bool Shutdown(string server, int port)
    {
        if (RootPassword is not { } password || Tool(server, "mariadb-admin", "mysqladmin") is not { } admin)
        {
            return false;
        }

        var run = Client(admin, port, password, ["shutdown"], TimeSpan.FromSeconds(10));
        return run.Success;
    }

    /// <summary>After a version change: mariadb-upgrade once the server accepts connections.</summary>
    public async Task UpgradeIfNeededAsync(string server, WebitSettings settings, string? version, CancellationToken ct)
    {
        var marker = Path.Combine(paths.MariaDbDir, ".data-version");
        try
        {
            var previous = File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
            if (version is null || previous == version)
            {
                return;
            }

            if (previous is null)
            {
                // Data folder created by this version.
                await File.WriteAllTextAsync(marker, version, ct);
                return;
            }

            if (!await WaitForPortAsync(settings.MariaDbPort, TimeSpan.FromSeconds(60), ct)
                || RootPassword is not { } password
                || Tool(server, "mariadb-upgrade", "mysql_upgrade") is not { } upgrade)
            {
                return;
            }

            log.LogInformation("MariaDB: upgrading data from {Old} to {New}.", previous, version);
            var run = await Task.Run(() => Client(upgrade, settings.MariaDbPort, password, [], TimeSpan.FromMinutes(10)), ct);
            if (run.Success)
            {
                await File.WriteAllTextAsync(marker, version, ct);
                log.LogInformation("MariaDB: upgrade done.");
            }
            else
            {
                log.LogWarning("MariaDB: upgrade failed: {Output}", run.Output);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "MariaDB: upgrade check failed.");
        }
    }

    /// <summary>Database plus user with the same name and a generated password (all hosts; the bind address limits access).</summary>
    public CreateDatabaseResult CreateDatabase(string server, WebitSettings settings, string name)
    {
        if (!IsValidName(name))
        {
            throw new ArgumentException("Name: letters, digits and underscore, starting with a letter (max. 32); no system names.");
        }

        var password = RootPassword ?? throw new InvalidOperationException("The MariaDB root password is not available.");
        var client = Tool(server, "mariadb", "mysql") ?? throw new InvalidOperationException("mariadb.exe not found.");
        var userPassword = GeneratePassword(20);

        // Name and password are restricted to [A-Za-z0-9_]: no quoting issues. CREATE USER first:
        // an existing user stops the script, so its password is never replaced silently.
        var sql = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"CREATE USER '{name}'@'%' IDENTIFIED BY '{userPassword}';")
            .Append(CultureInfo.InvariantCulture, $"CREATE DATABASE IF NOT EXISTS `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;")
            .Append(CultureInfo.InvariantCulture, $"GRANT ALL PRIVILEGES ON `{name}`.* TO '{name}'@'%';")
            .Append("FLUSH PRIVILEGES;")
            .ToString();

        var run = Client(client, settings.MariaDbPort, password, ["-e", sql], TimeSpan.FromSeconds(30));
        if (!run.Success)
        {
            throw new InvalidOperationException(LastLine(run.Output) ?? $"mariadb exit {run.ExitCode}");
        }

        log.LogInformation("MariaDB: database and user {Name} created.", name);
        return new CreateDatabaseResult { Database = name, User = name, Password = userPassword };
    }

    /// <summary>
    /// Dump to data\backups: all databases (mariadb-yyyyMMdd-HHmmss.sql) or one (&lt;name&gt;-yyyyMMdd-HHmmss.sql,
    /// without CREATE DATABASE so it can be imported under another name).
    /// </summary>
    public string Backup(string server, WebitSettings settings, string? database = null, string? file = null)
    {
        var password = RootPassword ?? throw new InvalidOperationException("The MariaDB root password is not available.");
        var dump = Tool(server, "mariadb-dump", "mysqldump") ?? throw new InvalidOperationException("mariadb-dump.exe not found.");
        if (database is not null && !NameRegex().IsMatch(database))
        {
            throw new ArgumentException("Invalid database name.");
        }

        Directory.CreateDirectory(paths.BackupsDir);
        file ??= Path.Combine(paths.BackupsDir, $"{database ?? AllDatabasesName}-{DateTime.Now:yyyyMMdd-HHmmss}.sql");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string[] scope = database is null ? ["--all-databases"] : [database];

        var run = Client(dump, settings.MariaDbPort, password,
            ["--single-transaction", "--routines", "--events", "--triggers", $"--result-file={file}", .. scope],
            TimeSpan.FromMinutes(30));
        if (!run.Success || !File.Exists(file))
        {
            TryDeleteFile(file);
            throw new InvalidOperationException(LastLine(run.Output) ?? $"mariadb-dump exit {run.ExitCode}");
        }

        log.LogInformation("MariaDB: backup written to {File}.", file);
        return file;
    }

    /// <summary>File name prefix of a dump of all databases (older versions: "mariadb").</summary>
    public const string AllDatabasesName = "all-databases";

    /// <summary>User databases with their size (system schemas left out).</summary>
    public List<(string Name, long Size)> ListDatabases(string server, WebitSettings settings)
    {
        var password = RootPassword ?? throw new InvalidOperationException("The MariaDB root password is not available.");
        var client = Tool(server, "mariadb", "mysql") ?? throw new InvalidOperationException("mariadb.exe not found.");
        const string sql = "SELECT s.schema_name, COALESCE(SUM(t.data_length + t.index_length), 0) FROM information_schema.schemata s " +
                           "LEFT JOIN information_schema.tables t ON t.table_schema = s.schema_name " +
                           "WHERE s.schema_name NOT IN ('mysql','sys','information_schema','performance_schema') " +
                           "GROUP BY s.schema_name ORDER BY s.schema_name";
        var run = Client(client, settings.MariaDbPort, password, ["-N", "-B", "-e", sql], TimeSpan.FromSeconds(30));
        if (!run.Success)
        {
            throw new InvalidOperationException(LastLine(run.Output) ?? $"mariadb exit {run.ExitCode}");
        }

        return run.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\t'))
            .Where(parts => parts.Length == 2 && NameRegex().IsMatch(parts[0]))
            .Select(parts => (parts[0], long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? size : 0))
            .ToList();
    }

    /// <summary>Deletes a database and the user with the same name.</summary>
    public void DropDatabase(string server, WebitSettings settings, string name)
    {
        if (!IsValidName(name))
        {
            throw new ArgumentException("Invalid or system database name.");
        }

        var password = RootPassword ?? throw new InvalidOperationException("The MariaDB root password is not available.");
        var client = Tool(server, "mariadb", "mysql") ?? throw new InvalidOperationException("mariadb.exe not found.");
        var run = Client(client, settings.MariaDbPort, password,
            ["-e", $"DROP DATABASE IF EXISTS `{name}`; DROP USER IF EXISTS '{name}'@'%'; FLUSH PRIVILEGES;"], TimeSpan.FromSeconds(60));
        if (!run.Success)
        {
            throw new InvalidOperationException(LastLine(run.Output) ?? $"mariadb exit {run.ExitCode}");
        }

        log.LogInformation("MariaDB: database and user {Name} deleted.", name);
    }

    /// <summary>
    /// Loads an SQL dump (stdin of the client) as the database's own user and in sandbox mode: a crafted dump can
    /// neither touch other databases nor run shell commands (\! / system).
    /// </summary>
    public void Import(string server, WebitSettings settings, CreateDatabaseResult target, string sqlFile) =>
        Load(server, settings, target.User, target.Password, target.Database, sqlFile);

    /// <summary>
    /// Backup back into its database: the database is emptied (dropped and created again; user and grants stay) and the
    /// dump is loaded as root, in sandbox mode.
    /// </summary>
    public void Restore(string server, WebitSettings settings, string database, string sqlFile)
    {
        if (!IsValidName(database))
        {
            throw new ArgumentException("Invalid or system database name.");
        }

        var password = RootPassword ?? throw new InvalidOperationException("The MariaDB root password is not available.");
        var client = Tool(server, "mariadb", "mysql") ?? throw new InvalidOperationException("mariadb.exe not found.");
        var run = Client(client, settings.MariaDbPort, password,
            ["-e", $"DROP DATABASE IF EXISTS `{database}`; CREATE DATABASE `{database}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;"],
            TimeSpan.FromSeconds(60));
        if (!run.Success)
        {
            throw new InvalidOperationException(LastLine(run.Output) ?? $"mariadb exit {run.ExitCode}");
        }

        Load(server, settings, "root", password, database, sqlFile);
        log.LogInformation("MariaDB: {Database} restored from {File}.", database, sqlFile);
    }

    private void Load(string server, WebitSettings settings, string user, string password, string database, string sqlFile)
    {
        if (!NameRegex().IsMatch(database))
        {
            throw new ArgumentException("Invalid database name.");
        }

        var client = Tool(server, "mariadb", "mysql") ?? throw new InvalidOperationException("mariadb.exe not found.");
        var psi = new System.Diagnostics.ProcessStartInfo(client)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(client)!
        };
        foreach (var arg in new[] { "--no-defaults", "--sandbox", "--protocol=TCP", "--host=127.0.0.1", $"--port={settings.MariaDbPort}",
                     $"--user={user}", "--default-character-set=utf8mb4", database })
        {
            psi.ArgumentList.Add(arg);
        }

        psi.Environment["MYSQL_PWD"] = password;
        using var process = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("mariadb could not be started.");
        var errors = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();
        try
        {
            using (var input = File.OpenRead(sqlFile))
            {
                input.CopyTo(process.StandardInput.BaseStream);
            }

            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The client stopped early (SQL error): its message follows below.
        }
        if (!process.WaitForExit(TimeSpan.FromMinutes(30)))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Import timed out.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(LastLine(errors.Result) ?? $"mariadb exit {process.ExitCode}");
        }

        log.LogInformation("MariaDB: {File} imported into {Database}.", sqlFile, database);
    }

    /// <summary>Account names (a leftover user blocks CREATE USER of the same name).</summary>
    private List<string> ListUsers(string server, WebitSettings settings)
    {
        var password = RootPassword ?? throw new InvalidOperationException("The MariaDB root password is not available.");
        var client = Tool(server, "mariadb", "mysql") ?? throw new InvalidOperationException("mariadb.exe not found.");
        var run = Client(client, settings.MariaDbPort, password, ["-N", "-B", "-e", "SELECT DISTINCT User FROM mysql.user"], TimeSpan.FromSeconds(30));
        return run.Success
            ? run.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : [];
    }

    /// <summary>First free valid name: base, base_2, base_3 … (neither database nor user exists).</summary>
    public string FreeName(string server, WebitSettings settings, string wanted)
    {
        var clean = new string(wanted.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        if (clean.Length == 0 || !char.IsAsciiLetter(clean[0]))
        {
            clean = "db_" + clean;
        }

        clean = clean.Length > 28 ? clean[..28] : clean;
        var existing = ListDatabases(server, settings).Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        existing.UnionWith(ListUsers(server, settings));
        var name = clean;
        for (var i = 2; existing.Contains(name) || !IsValidName(name); i++)
        {
            name = $"{clean}_{i}";
        }

        return name;
    }

    /// <summary>
    /// Client tool over TCP as root; the password goes through MYSQL_PWD, never the command line.
    /// --no-defaults: option files of other installations (C:\my.ini …) must not override it.
    /// </summary>
    private static ProcessRunner.Result Client(string tool, int port, string password, IEnumerable<string> args, TimeSpan timeout) =>
        ProcessRunner.Run(
            tool,
            ["--no-defaults", "--protocol=TCP", "--host=127.0.0.1", $"--port={port}", "--user=root", .. args],
            timeout,
            Path.GetDirectoryName(tool),
            new Dictionary<string, string> { ["MYSQL_PWD"] = password });

    public static async Task<bool> WaitForPortAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            try
            {
                using var client = new TcpClient();
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(500);
                await client.ConnectAsync("127.0.0.1", port, attempt.Token);
                return true;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                await Task.Delay(700, ct);
            }
        }

        return false;
    }

    private static string? LastLine(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception)
        {
            // Left as is; the next start reports the problem.
        }
    }

    private static void TryDeleteFile(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception)
        {
            // Ignore.
        }
    }
}
