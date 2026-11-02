using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WEBit.Contracts;
using WEBit.Core.Diagnostics;
using WEBit.Core.Models;
using WEBit.Core.Net;
using WEBit.Core.Storage;
using WEBit.Platform.Windows;

namespace WEBit.Service.Engine;

/// <summary>Result of a config load.</summary>
public sealed record EngineLoadResult(bool Success, string? Error);

/// <summary>Runs Caddy: start, supervision, config load via the admin API (127.0.0.1 only), stop.</summary>
public sealed class CaddyEngine : IDisposable
{
    private readonly WebitPaths _paths;
    private readonly ILogger<CaddyEngine> _log;
    private readonly object _sync = new();
    private readonly Queue<string> _recentOutput = new();
    private readonly ProcessJob _job;
    private Process? _process;
    private HttpClient? _http;
    private bool _stopping;

    public CaddyEngine(WebitPaths paths, ProcessJob job, ILogger<CaddyEngine> log)
    {
        _paths = paths;
        _job = job;
        _log = log;
    }

    /// <summary>Raised when Caddy exits unexpectedly.</summary>
    public event Action? UnexpectedExit;

    public EngineState State { get; private set; } = EngineState.Stopped;

    public LocalizedMessage? Message { get; private set; }

    public string? Version { get; private set; }

    public string? ExecutablePath { get; private set; }

    public int AdminPort { get; private set; }

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

    public EngineStatus Snapshot() => new()
    {
        State = State,
        Version = Version,
        ExecutablePath = ExecutablePath,
        ProcessId = ProcessId,
        AdminPort = AdminPort,
        Message = Message
    };

    private string? _versionPath;
    private DateTime _versionStamp;

    public string? ResolveExecutable(WebitSettings settings)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.EnginePath))
        {
            candidates.Add(settings.EnginePath!);
        }

        candidates.Add(_paths.BundledEnginePath);
        candidates.Add(_paths.BundledEngineLegacyPath);

        // Downloaded on the dependencies page when the bundled copy is missing.
        candidates.Add(Path.Combine(_paths.ModuleDir(ModuleIds.WebServer), OperatingSystem.IsWindows() ? "caddy.exe" : "caddy"));

        // Development: runtime\caddy of the repository (folder with WEBit.slnx) – never arbitrary parent folders.
        var exeName = OperatingSystem.IsWindows() ? "caddy.exe" : "caddy";
        var dir = new DirectoryInfo(_paths.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "WEBit.slnx")))
            {
                candidates.Add(Path.Combine(dir.FullName, "runtime", "caddy", exeName));
                break;
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task<bool> StartAsync(WebitSettings settings, JsonObject initialConfig, CancellationToken ct)
    {
        if (State == EngineState.Running && ProcessId is not null)
        {
            return true;
        }

        AdminPort = settings.EngineAdminPort;
        ExecutablePath = ResolveExecutable(settings);
        if (ExecutablePath is null)
        {
            SetState(EngineState.NotInstalled, LocalizedMessage.Of(MessageCodes.EngineNotInstalled, _paths.BundledEnginePath));
            _log.LogError("Web server engine not found. Expected: {Path}", _paths.BundledEnginePath);
            return false;
        }

        SetState(EngineState.Starting, null);
        _http?.Dispose();
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(3) })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{settings.EngineAdminPort}"),
            Timeout = TimeSpan.FromSeconds(60)
        };

        // Stop an orphaned Caddy instance on the admin port.
        if (!PortProbe.CanBind("127.0.0.1", settings.EngineAdminPort, out _))
        {
            if (await IsCaddyAdminAsync(ct))
            {
                _log.LogWarning("Stopping orphaned Caddy instance on admin port {Port}.", settings.EngineAdminPort);
                await TryPostAsync("/stop", ct);
                await Task.Delay(1500, ct);
            }

            if (!PortProbe.CanBind("127.0.0.1", settings.EngineAdminPort, out _))
            {
                SetState(EngineState.Error, LocalizedMessage.Of(MessageCodes.EngineAdminPortInUse, settings.EngineAdminPort));
                _log.LogError("Admin port {Port} is in use.", settings.EngineAdminPort);
                return false;
            }
        }

        // Read again after an update or a changed path (same file: cached).
        var stamp = File.GetLastWriteTimeUtc(ExecutablePath);
        if (Version is null || !string.Equals(_versionPath, ExecutablePath, StringComparison.OrdinalIgnoreCase) || _versionStamp != stamp)
        {
            Version = ReadVersion(ExecutablePath);
            _versionPath = ExecutablePath;
            _versionStamp = stamp;
        }

        Directory.CreateDirectory(_paths.EngineRuntimeDir);
        await File.WriteAllTextAsync(_paths.EngineConfigFile, initialConfig.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);

        var psi = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _paths.EngineRuntimeDir
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--config");
        psi.ArgumentList.Add(_paths.EngineConfigFile);
        psi.Environment["XDG_DATA_HOME"] = Path.Combine(_paths.EngineRuntimeDir, "data");
        psi.Environment["XDG_CONFIG_HOME"] = Path.Combine(_paths.EngineRuntimeDir, "config");

        Process process;
        try
        {
            process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => OnOutput(e.Data);
            process.ErrorDataReceived += (_, e) => OnOutput(e.Data);
            process.Exited += OnProcessExited;
            if (!process.Start())
            {
                throw new InvalidOperationException("Process.Start returned false.");
            }
        }
        catch (Exception ex)
        {
            SetState(EngineState.Error, LocalizedMessage.Of(MessageCodes.EngineStartFailed, ex.Message));
            _log.LogError(ex, "Web server engine could not be started.");
            return false;
        }

        _job.Assign(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        lock (_sync)
        {
            _process = process;
            _stopping = false;
        }

        _log.LogInformation("Web server engine started (PID {Pid}, {Version}).", process.Id, Version ?? "?");

        // Wait for the admin API.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (process.HasExited)
            {
                var output = RecentOutput();
                lock (_sync)
                {
                    _stopping = true;
                    _process = null;
                }

                process.Dispose();
                SetState(EngineState.Error, LocalizedMessage.Of(MessageCodes.EngineStartFailed, output));
                _log.LogError("Web server engine exited right after start: {Output}", output);
                return false;
            }

            if (await IsCaddyAdminAsync(ct))
            {
                SetState(EngineState.Running, null);
                return true;
            }

            await Task.Delay(250, ct);
        }

        await StopAsync();
        SetState(EngineState.Error, LocalizedMessage.Of(MessageCodes.EngineStartFailed, "Admin API not responding (timeout)."));
        return false;
    }

    /// <summary>Loads a full config. Caddy swaps it without downtime or keeps the old one on error.</summary>
    public async Task<EngineLoadResult> LoadAsync(JsonObject config, CancellationToken ct)
    {
        if (_http is null || State != EngineState.Running)
        {
            return new EngineLoadResult(false, "Engine not running");
        }

        try
        {
            using var content = new StringContent(config.ToJsonString(), Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await _http.PostAsync("/load", content, ct);
            if (response.IsSuccessStatusCode)
            {
                return new EngineLoadResult(true, null);
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            return new EngineLoadResult(false, ExtractError(body) ?? $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new EngineLoadResult(false, ex.Message);
        }
    }

    public async Task StopAsync()
    {
        Process? process;
        lock (_sync)
        {
            process = _process;
            _stopping = true;
        }

        if (process is null)
        {
            SetState(EngineState.Stopped, null);
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await TryPostAsync("/stop", cts.Token);
                try
                {
                    await process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Timeout: kill.
                }

                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Error while stopping the web server engine.");
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_process, process))
                {
                    _process = null;
                }
            }

            process.Dispose();
            SetState(EngineState.Stopped, null);
            _log.LogInformation("Web server engine stopped.");
        }
    }

    public void Dispose()
    {
        Process? process;
        lock (_sync)
        {
            _stopping = true;
            process = _process;
            _process = null;
        }

        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort.
        }

        process?.Dispose();
        _http?.Dispose();
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        bool expected;
        lock (_sync)
        {
            expected = _stopping || !ReferenceEquals(sender, _process);
        }

        if (expected)
        {
            return;
        }

        var output = RecentOutput();
        _log.LogError("Web server engine exited unexpectedly. Last output: {Output}", output);
        SetState(EngineState.Error, LocalizedMessage.Of(MessageCodes.EngineStopped, output));
        Process? exited;
        lock (_sync)
        {
            exited = _process;
            _process = null;
        }

        exited?.Dispose();

        UnexpectedExit?.Invoke();
    }

    private void OnOutput(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (_recentOutput)
        {
            _recentOutput.Enqueue(LogReader.Format(line));
            while (_recentOutput.Count > 20)
            {
                _recentOutput.Dequeue();
            }
        }

        _log.LogDebug("[caddy] {Line}", line);
    }

    private string RecentOutput()
    {
        lock (_recentOutput)
        {
            var lines = _recentOutput.TakeLast(5).ToArray();
            var text = string.Join(" | ", lines);
            return text.Length > 600 ? text[..600] + " …" : text;
        }
    }

    private async Task<bool> IsCaddyAdminAsync(CancellationToken ct)
    {
        if (_http is null)
        {
            return false;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            using var response = await _http.GetAsync("/config/", cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task TryPostAsync(string path, CancellationToken ct)
    {
        if (_http is null)
        {
            return;
        }

        try
        {
            using var content = new StringContent("", Encoding.UTF8);
            using var _ = await _http.PostAsync(path, content, ct);
        }
        catch
        {
            // Best effort.
        }
    }

    private string? ReadVersion(string executable)
    {
        try
        {
            var run = ProcessRunner.Run(executable, ["version"], TimeSpan.FromSeconds(10));
            var first = run.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            return string.IsNullOrEmpty(first) ? null : first.Split(' ')[0];
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not read engine version.");
            return null;
        }
    }

    private static string? ExtractError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                return error.GetString();
            }
        }
        catch (JsonException)
        {
            // Not JSON.
        }

        return body.Trim();
    }

    private void SetState(EngineState state, LocalizedMessage? message)
    {
        State = state;
        Message = message;
    }
}
