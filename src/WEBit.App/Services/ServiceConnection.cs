using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Reflection;
using WEBit.Contracts;
using WEBit.Core.Serialization;

namespace WEBit.App.Services;

public enum ConnectionState
{
    Connecting,
    Connected,
    Disconnected,
    AccessDenied,
    Incompatible
}

/// <summary>Named pipe connection to the service with auto reconnect.</summary>
public sealed class ServiceConnection : IDisposable
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<PipeEnvelope>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly CancellationTokenSource _cts = new();
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private long _nextId;

    public event Action<ConnectionState>? StateChanged;

    public event Action<ServiceStatus>? StatusReceived;

    public ConnectionState State { get; private set; } = ConnectionState.Connecting;

    public HelloResponse? Hello { get; private set; }

    public bool IsConnected => State == ConnectionState.Connected;

    public static string ClientVersion =>
        typeof(ServiceConnection).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public void Start() => _ = Task.Run(() => ConnectLoopAsync(_cts.Token));

    /// <summary>Skips the wait and reconnects now.</summary>
    public void Reconnect()
    {
        if (_wake.CurrentCount == 0)
        {
            _wake.Release();
        }
    }

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (OperatingSystem.IsWindows() && !WEBit.Platform.Windows.PipeProbe.Exists(Protocol.PipeName))
            {
                // No server: report offline at once instead of spinning in ConnectAsync.
                SetState(ConnectionState.Disconnected);
                await WaitAsync(TimeSpan.FromSeconds(2), ct);
                continue;
            }

            var pipe = new NamedPipeClientStream(".", Protocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(1500, ct);
            }
            catch (UnauthorizedAccessException)
            {
                await pipe.DisposeAsync();
                SetState(ConnectionState.AccessDenied);
                await WaitAsync(TimeSpan.FromSeconds(5), ct);
                continue;
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                break;
            }
            catch (Exception)
            {
                // Timeout or IOException: service not reachable.
                await pipe.DisposeAsync();
                SetState(ConnectionState.Disconnected);
                await WaitAsync(TimeSpan.FromSeconds(3), ct);
                continue;
            }

            _pipe = pipe;
            _writer = new StreamWriter(pipe, WebitJson.Utf8NoBom, 8192, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
            var readTask = ReadLoopAsync(pipe, ct);

            try
            {
                Hello = await RequestAsync<HelloResponse>(Methods.Hello, new HelloRequest { ClientVersion = ClientVersion }, TimeSpan.FromSeconds(10));
                if (Hello is null || Hello.ProtocolVersion != Protocol.Version)
                {
                    SetState(ConnectionState.Incompatible);
                    await pipe.DisposeAsync();
                }
                else
                {
                    SetState(ConnectionState.Connected);
                    var status = await RequestAsync<ServiceStatus>(Methods.GetStatus);
                    if (status is not null)
                    {
                        StatusReceived?.Invoke(status);
                    }
                }
            }
            catch (Exception)
            {
                // Handshake failed: close the pipe, read loop ends, reconnect.
                await pipe.DisposeAsync();
            }

            await readTask;

            _writer = null;
            _pipe = null;
            await pipe.DisposeAsync();
            FailPending();
            if (ct.IsCancellationRequested)
            {
                break;
            }

            if (State != ConnectionState.Incompatible)
            {
                SetState(ConnectionState.Disconnected);
            }
            await WaitAsync(TimeSpan.FromSeconds(1), ct);
        }
    }

    private async Task ReadLoopAsync(NamedPipeClientStream pipe, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(pipe, WebitJson.Utf8NoBom, false, 8192, leaveOpen: true);
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null)
                {
                    break;
                }

                if (line.Length == 0)
                {
                    continue;
                }

                PipeEnvelope? envelope;
                try
                {
                    envelope = Protocol.Deserialize(line);
                }
                catch
                {
                    continue;
                }

                if (envelope is null)
                {
                    continue;
                }

                if (envelope.Kind == Protocol.KindResponse && _pending.TryRemove(envelope.Id, out var tcs))
                {
                    tcs.TrySetResult(envelope);
                }
                else if (envelope.Kind == Protocol.KindEvent && envelope.Method == Events.StatusChanged)
                {
                    var status = Protocol.FromPayload<ServiceStatus>(envelope.Payload);
                    if (status is not null)
                    {
                        StatusReceived?.Invoke(status);
                    }
                }
            }
        }
        catch
        {
            // Connection closed.
        }
    }

    public async Task<T?> RequestAsync<T>(string method, object? payload = null, TimeSpan? timeout = null)
    {
        var envelope = await SendAsync(method, payload, timeout);
        return Protocol.FromPayload<T>(envelope.Payload);
    }

    public async Task RequestAsync(string method, object? payload = null, TimeSpan? timeout = null) =>
        await SendAsync(method, payload, timeout);

    private async Task<PipeEnvelope> SendAsync(string method, object? payload, TimeSpan? timeout)
    {
        var writer = _writer ?? throw new InvalidOperationException(Localization.Loc.T("ErrNotConnected"));
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<PipeEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var request = new PipeEnvelope
        {
            Kind = Protocol.KindRequest,
            Id = id,
            Method = method,
            Payload = payload is null ? null : Protocol.ToPayload(payload)
        };

        await _writeLock.WaitAsync();
        try
        {
            await writer.WriteLineAsync(Protocol.Serialize(request));
        }
        catch (Exception ex)
        {
            _pending.TryRemove(id, out _);
            throw new InvalidOperationException(Localization.Loc.T("ErrNotConnected"), ex);
        }
        finally
        {
            _writeLock.Release();
        }

        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(90));
        await using (cts.Token.Register(() =>
                     {
                         if (_pending.TryRemove(id, out var pending))
                         {
                             pending.TrySetException(new TimeoutException(Localization.Loc.T("ErrTimeout")));
                         }
                     }))
        {
            var response = await tcs.Task;
            if (!string.IsNullOrEmpty(response.Error))
            {
                throw new InvalidOperationException(response.Error);
            }

            return response;
        }
    }

    private void FailPending()
    {
        foreach (var id in _pending.Keys.ToList())
        {
            if (_pending.TryRemove(id, out var tcs))
            {
                tcs.TrySetException(new InvalidOperationException(Localization.Loc.T("ErrNotConnected")));
            }
        }
    }

    private async Task WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var wakeTask = _wake.WaitAsync(linked.Token);
            await Task.WhenAny(Task.Delay(delay, linked.Token), wakeTask);
            linked.Cancel();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void SetState(ConnectionState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(state);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _pipe?.Dispose();
        }
        catch
        {
            // Ignore.
        }

        _cts.Dispose();
    }
}
