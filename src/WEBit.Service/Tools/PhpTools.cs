using System.Text;
using Microsoft.Extensions.Logging;
using WEBit.Core.Diagnostics;
using WEBit.Core.Models;
using WEBit.Core.Serialization;
using WEBit.Core.Storage;
using WEBit.Service.Modules;

namespace WEBit.Service.Tools;

/// <summary>Runs PHP archives (Composer, WP-CLI) with the PHP module's php.exe and php.ini; output goes to a log file.</summary>
public sealed class PhpTools(WebitPaths paths, ModuleManager modules, ILogger<PhpTools> log)
{
    public sealed record Result(bool Success, string Output);

    public string PhpCli => modules.PhpCliExecutable ?? throw new InvalidOperationException("PHP is not installed (Modules → PHP).");

    public Result Composer(WebitSettings settings, string workDir, string logFile, TimeSpan timeout, params string[] args) =>
        ComposerWith(settings, null, workDir, logFile, timeout, args);

    /// <summary>Composer with the PHP version of a site (null = default PHP).</summary>
    public Result ComposerWith(WebitSettings settings, string? phpBranch, string workDir, string logFile, TimeSpan timeout, params string[] args) =>
        Run(settings, phpBranch, modules.ComposerPhar ?? throw new InvalidOperationException("Composer is not installed (Modules → Composer)."),
            workDir, logFile, timeout, args);

    public Result WpCli(WebitSettings settings, string workDir, string logFile, TimeSpan timeout, params string[] args) =>
        Run(settings, null, modules.WpCliPhar ?? throw new InvalidOperationException("WP-CLI is not installed (Modules → WP-CLI)."),
            workDir, logFile, timeout, args);

    private Result Run(WebitSettings settings, string? phpBranch, string phar, string workDir, string logFile, TimeSpan timeout, string[] args)
    {
        var cgi = modules.PhpExecutableFor(phpBranch) ?? throw new InvalidOperationException($"PHP {phpBranch} is not installed (Dependencies).");
        var php = Path.Combine(Path.GetDirectoryName(cgi)!, OperatingSystem.IsWindows() ? "php.exe" : "php");
        if (!File.Exists(php))
        {
            throw new InvalidOperationException("PHP is not installed (Modules → PHP).");
        }

        // Same php.ini as the web server (extensions, mail to Mailpit); written now in case PHP is switched off.
        var branch = ModuleIds.BranchOfVersion(modules.VersionOf(ModuleIds.Php)) == phpBranch ? null : phpBranch;
        var ini = PhpIni.Write(paths, settings, cgi, null, branch is null ? "php.ini" : $"php-{branch}.ini");
        var phpDir = Path.GetDirectoryName(php)!;
        var cache = Path.Combine(paths.CacheDir, "php-tools");
        Directory.CreateDirectory(cache);
        var env = new Dictionary<string, string>
        {
            ["PATH"] = phpDir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
            ["PHPRC"] = Path.GetDirectoryName(ini)!,
            ["COMPOSER_HOME"] = Path.Combine(cache, "composer"),
            ["COMPOSER_CACHE_DIR"] = Path.Combine(cache, "composer", "cache"),
            ["COMPOSER_NO_INTERACTION"] = "1",
            ["WP_CLI_CACHE_DIR"] = Path.Combine(cache, "wp-cli"),
            ["WP_CLI_DISABLE_AUTO_CHECK_UPDATE"] = "1",
            ["NO_COLOR"] = "1",

            // Service account without a usable profile: WP-CLI and Composer need a home folder.
            ["HOME"] = Path.Combine(cache, "home")
        };
        Directory.CreateDirectory(env["HOME"]);

        string[] command = ["-c", ini, "-d", "memory_limit=1024M", phar, .. args];
        Append(logFile, "▶ php " + string.Join(' ', command.Skip(5).Prepend(Path.GetFileName(phar)).Select(Quote)));
        log.LogInformation("Running {Tool} {Args} in {Dir}.", Path.GetFileName(phar), string.Join(' ', args.Take(2)), workDir);

        Directory.CreateDirectory(workDir);
        var run = ProcessRunner.Run(php, command, timeout, workDir, env);
        Append(logFile, run.Output.TrimEnd());
        Append(logFile, run.TimedOut ? "■ timeout" : $"■ exit {run.ExitCode}");
        return new Result(run.Success, run.Output);
    }

    /// <summary>Last meaningful output line for an error message.</summary>
    public static string LastLine(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => !l.StartsWith("Warning: ", StringComparison.Ordinal)) ?? "";

    /// <summary>For the log: quoted, passwords masked (--dbpass=…, --admin_password=…).</summary>
    private static string Quote(string arg)
    {
        var separator = arg.IndexOf('=');
        if (separator > 0 && arg[..separator].Contains("pass", StringComparison.OrdinalIgnoreCase))
        {
            arg = arg[..(separator + 1)] + "***";
        }

        return arg.Contains(' ') ? $"\"{arg}\"" : arg;
    }

    private static void Append(string file, string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {text}{Environment.NewLine}", WebitJson.Utf8NoBom);
        }
        catch (Exception)
        {
            // Logging must not stop the tool.
        }
    }
}
