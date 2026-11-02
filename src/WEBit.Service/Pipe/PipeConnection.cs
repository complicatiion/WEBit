using System.IO.Pipes;
using Microsoft.Extensions.Logging;
using WEBit.Contracts;
using WEBit.Core.Serialization;

namespace WEBit.Service.Pipe;

/// <summary>One connected client; reads requests line by line and answers them.</summary>
public sealed class PipeConnection(
    NamedPipeServerStream pipe,
    RequestDispatcher dispatcher,
    StatusBroadcaster broadcaster,
    ILogger logger)
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private StreamWriter? _writer;

    public async Task RunAsync(CancellationToken ct)
    {
        broadcaster.Add(this);
        try
        {
            using var reader = new StreamReader(pipe, WebitJson.Utf8NoBom, false, 8192, leaveOpen: true);
            _writer = new StreamWriter(pipe, WebitJson.Utf8NoBom, 8192, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };

            while (!ct.IsCancellationRequested && pipe.IsConnected)
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

                PipeEnvelope? request;
                try
                {
                    request = Protocol.Deserialize(line);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Invalid message received.");
                    continue;
                }

                if (request is null || request.Kind != Protocol.KindRequest || string.IsNullOrEmpty(request.Method))
                {
                    continue;
                }

                _ = HandleAsync(request, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (IOException)
        {
            // Disconnected.
        }
        catch (ObjectDisposedException)
        {
            // Disconnected.
        }
        finally
        {
            broadcaster.Remove(this);
            try
            {
                if (pipe.IsConnected)
                {
                    pipe.Disconnect();
                }
            }
            catch
            {
                // Ignore.
            }

            await pipe.DisposeAsync();
        }
    }

    private async Task HandleAsync(PipeEnvelope request, CancellationToken ct)
    {
        var response = new PipeEnvelope
        {
            Kind = Protocol.KindResponse,
            Id = request.Id,
            Method = request.Method
        };

        try
        {
            response.Payload = await dispatcher.DispatchAsync(request.Method!, request.Payload, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Request {Method} failed: {Message}", request.Method, ex.Message);
            response.Error = ex.Message;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await SendLineAsync(Protocol.Serialize(response));
    }

    public async Task SendLineAsync(string line)
    {
        var writer = _writer;
        if (writer is null)
        {
            return;
        }

        await _writeLock.WaitAsync();
        try
        {
            if (pipe.IsConnected)
            {
                await writer.WriteLineAsync(line);
            }
        }
        catch
        {
            // Peer gone; the read loop cleans up.
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
