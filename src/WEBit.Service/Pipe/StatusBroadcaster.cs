using WEBit.Contracts;

namespace WEBit.Service.Pipe;

/// <summary>Pushes events to all connected clients (tray instances, RDP sessions).</summary>
public sealed class StatusBroadcaster
{
    private readonly object _lock = new();
    private readonly List<PipeConnection> _connections = [];

    public void Add(PipeConnection connection)
    {
        lock (_lock)
        {
            _connections.Add(connection);
        }
    }

    public void Remove(PipeConnection connection)
    {
        lock (_lock)
        {
            _connections.Remove(connection);
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _connections.Count;
            }
        }
    }

    public void Broadcast<T>(string eventName, T payload)
    {
        PipeConnection[] targets;
        lock (_lock)
        {
            if (_connections.Count == 0)
            {
                return;
            }

            targets = [.. _connections];
        }

        var envelope = new PipeEnvelope
        {
            Kind = Protocol.KindEvent,
            Method = eventName,
            Payload = Protocol.ToPayload(payload)
        };
        var line = Protocol.Serialize(envelope);

        foreach (var connection in targets)
        {
            _ = connection.SendLineAsync(line);
        }
    }
}
