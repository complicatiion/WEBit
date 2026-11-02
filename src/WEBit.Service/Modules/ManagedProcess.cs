using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using WEBit.Contracts;
using WEBit.Core.Models;
using WEBit.Core.Serialization;
using WEBit.Service.Engine;

namespace WEBit.Service.Modules;

/// <summary>Supervised child process with restart backoff and a log file.</summary>
public sealed class ManagedProcess : IDisposable
{
    private readonly string _name;
    private readonly string _logFile;
    private readonly ProcessJob _job;
    private readonly ILogger _log;
    private readonly object _sync = new();
    private Process? _process;
    private ProcessStartInfo? _startInfo;
    private bool _wanted;
    private int _failures;
    private DateTime _startedAt;
    private CancellationTokenSource? _restartCts;

    public ManagedProcess(string name, string logFile, ProcessJob job, ILogger log)
    {
        _name = name;
        _logFile = logFile;
        _job = job;
        _log = log;
    }

    public event Action? StateChanged;

    /// <summary>Restart after an unexpected exit (backoff). Off: the process stays down with an error (dev servers).</summary>
    public bool AutoRestart { get; set; } = true;

    /// <summary>Asks the process to shut down cleanly before it is killed (MariaDB); true = request sent.</summary>
    public Func<bool>? GracefulStop { get; set; }

    public ModuleState State { get; private set; } = ModuleState.Stopped;

    public LocalizedMessage? Message { get; private set; }

    public int? ProcessId
    {
        get
        {
            lock (_sync)
            {
                try
                {
                    return _process is { HasExited: false } p ? p.Id : null;
                }
                catch
                {
                    return null;
                }
            }
        }
    }

    public void Start(ProcessStartInfo startInfo)
    {
        lock (_sync)
        {
            _wanted = true;
            _failures = 0;
            _startInfo = startInfo;
            _restartCts?.Cancel();
        }

        Launch();
    }

    public void Stop()
    {
        Process? process;
        lock (_sync)
        {
            _wanted = false;
            _restartCts?.Cancel();
            process = _process;
            _process = null;
        }

        if (process is not null && GracefulStop is { } graceful && IsAlive(process))
        {
            try
            {
                if (graceful())
                {
                    process.WaitForExit(10000);
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "{Module}: clean shutdown failed.", _name);
            }
        }

        Kill(process);
        SetState(ModuleState.Stopped, null);
    }

    public void Fail(LocalizedMessage message)
    {
        Stop();
        SetState(ModuleState.Error, message);
    }

    private void Launch()
    {
        ProcessStartInfo? psi;
        lock (_sync)
        {
            psi = _startInfo;
            if (!_wanted || psi is null || IsAlive(_process))
            {
                return;
            }
        }

        SetState(ModuleState.Starting, null);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;

        try
        {
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => WriteLog(e.Data);
            process.ErrorDataReceived += (_, e) => WriteLog(e.Data);
            process.Exited += OnExited;
            lock (_sync)
            {
                // Assigned before Start so an immediate exit is not ignored by OnExited.
                _process = process;
                _startedAt = DateTime.UtcNow;
            }

            if (!process.Start())
            {
                throw new InvalidOperationException("Process.Start returned false.");
            }

            _job.Assign(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _log.LogInformation("{Module} started (PID {Pid}).", _name, process.Id);
            if (IsAlive(process))
            {
                SetState(ModuleState.Running, null);
            }
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _process?.Dispose();
                _process = null;
            }

            _log.LogError(ex, "{Module} could not be started.", _name);
            SetState(ModuleState.Error, LocalizedMessage.Of(MessageCodes.ModuleStartFailed, ex.Message));
        }
    }

    private void OnExited(object? sender, EventArgs e)
    {
        CancellationTokenSource cts;
        int failures;
        int? exitCode;
        lock (_sync)
        {
            if (!ReferenceEquals(sender, _process) || !_wanted)
            {
                return;
            }

            try
            {
                exitCode = _process?.ExitCode;
            }
            catch
            {
                exitCode = null;
            }

            _process?.Dispose();
            _process = null;

            // A process that ran for a while counts as a fresh failure, not as a crash loop.
            if (DateTime.UtcNow - _startedAt > TimeSpan.FromMinutes(1))
            {
                _failures = 0;
            }

            failures = ++_failures;
            _restartCts?.Dispose();
            _restartCts = cts = new CancellationTokenSource();
        }

        if (!AutoRestart)
        {
            _log.LogWarning("{Module} exited (code {Code}).", _name, exitCode?.ToString() ?? "?");
            SetState(ModuleState.Error, LocalizedMessage.Of(MessageCodes.ModuleExited, exitCode?.ToString() ?? "?"));
            return;
        }

        var delay = TimeSpan.FromSeconds(Math.Min(30, 2 * failures));
        _log.LogWarning("{Module} exited unexpectedly; restart in {Delay} s.", _name, delay.TotalSeconds);
        SetState(ModuleState.Error, LocalizedMessage.Of(MessageCodes.ModuleExited, exitCode?.ToString() ?? "?"));

        _ = Task.Delay(delay, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                Launch();
            }
        }, TaskScheduler.Default);
    }

    private static bool IsAlive(Process? process)
    {
        try
        {
            return process is { HasExited: false };
        }
        catch (InvalidOperationException)
        {
            // Not started yet.
            return process is not null;
        }
    }

    /// <summary>Write the process output to the log file (Settings → Logs → module logs).</summary>
    public bool LogEnabled { get; set; } = true;

    public long MaxLogBytes { get; set; } = 10L * 1024 * 1024;

    /// <summary>Own lines in the log of this process (start command, install output).</summary>
    public void Log(string line) => WriteLog(line);

    private void WriteLog(string? line)
    {
        if (!LogEnabled || string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        try
        {
            lock (_logFile)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logFile)!);
                var info = new FileInfo(_logFile);
                if (info.Exists && info.Length > MaxLogBytes)
                {
                    File.Move(_logFile, _logFile + ".1", overwrite: true);
                }

                File.AppendAllText(
                    _logFile,
                    $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}  {line}{Environment.NewLine}",
                    WebitJson.Utf8NoBom);
            }
        }
        catch
        {
            // Logging must never break the module.
        }
    }

    private static void Kill(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
            // Best effort.
        }
        finally
        {
            process.Dispose();
        }
    }

    private void SetState(ModuleState state, LocalizedMessage? message)
    {
        State = state;
        Message = message;
        StateChanged?.Invoke();
    }

    public void Dispose() => Stop();
}
