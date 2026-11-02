using WEBit.Contracts;
using WEBit.Core.Models;
using WEBit.Core.Net;
using WEBit.Platform.Windows;

namespace WEBit.Service.Engine;

/// <summary>
/// Checks sites against the host: folder access, local IP, free and unreserved ports (engine listeners ignored).
/// </summary>
public sealed class EnvironmentChecker
{
    /// <summary>Port usage snapshot shared by all checks of one pass.</summary>
    public sealed class PortSnapshot
    {
        public IReadOnlyList<TcpListenerTable.Listener> Listeners { get; init; } = [];

        public IReadOnlyList<(int Start, int End)> Excluded { get; init; } = [];

        public bool Available { get; init; }
    }

    public PortSnapshot TakeSnapshot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new PortSnapshot();
        }

        try
        {
            return new PortSnapshot
            {
                Listeners = TcpListenerTable.GetIPv4Listeners(),
                Excluded = ExcludedPortRanges.Get(),
                Available = true
            };
        }
        catch
        {
            return new PortSnapshot();
        }
    }

    public List<ValidationIssue> CheckSite(SiteDefinition site, PortSnapshot snapshot, int? enginePid)
    {
        var issues = new List<ValidationIssue>();

        if (site.HasRoot && !string.IsNullOrWhiteSpace(site.Root))
        {
            if (!Directory.Exists(site.Root))
            {
                issues.Add(ValidationIssue.Error(MessageCodes.RootMissing, "root", site.Root));
            }
            else
            {
                try
                {
                    using var enumerator = Directory.EnumerateFileSystemEntries(site.Root).GetEnumerator();
                    enumerator.MoveNext();
                }
                catch (UnauthorizedAccessException)
                {
                    issues.Add(ValidationIssue.Error(MessageCodes.RootNoAccess, "root", site.Root, Environment.UserName));
                }
                catch (IOException ex)
                {
                    issues.Add(ValidationIssue.Error(MessageCodes.RootNoAccess, "root", site.Root, ex.Message));
                }
            }
        }

        for (var i = 0; i < site.Bindings.Count; i++)
        {
            var b = site.Bindings[i];
            issues.AddRange(CheckEndpoint(b.Address, b.Port, snapshot, enginePid, $"binding:{i}"));
        }

        return issues;
    }

    public List<ValidationIssue> CheckEndpoint(string address, int port, PortSnapshot snapshot, int? enginePid, string? field = null)
    {
        var issues = new List<ValidationIssue>();
        if (!PortPolicy.IsValidPort(port) || !Endpoint.TryParse(address, out _))
        {
            return issues;
        }

        var endpoint = Endpoint.Display(address, port);

        if (!HostNetwork.IsLocalAddress(address))
        {
            issues.Add(ValidationIssue.Error(MessageCodes.AddressNotLocal, field, address));
        }

        foreach (var (start, end) in snapshot.Excluded)
        {
            if (port >= start && port <= end)
            {
                issues.Add(ValidationIssue.Error(MessageCodes.PortReserved, field, endpoint, $"{start}–{end}"));
                break;
            }
        }

        if (snapshot.Available)
        {
            var conflict = snapshot.Listeners.FirstOrDefault(l =>
                l.Port == port &&
                l.ProcessId != (enginePid ?? -1) &&
                Endpoint.Overlaps(l.Address == "0.0.0.0" ? null : l.Address, address));

            if (conflict is not null)
            {
                issues.Add(ValidationIssue.Error(MessageCodes.PortInUse, field, endpoint, conflict.ProcessName ?? "?", conflict.ProcessId));
            }
        }
        else
        {
            // No listener table (e.g. Linux): test bind, unless the engine holds the port.
            if (enginePid is null && !PortProbe.CanBind(address, port, out _))
            {
                issues.Add(ValidationIssue.Error(MessageCodes.PortInUse, field, endpoint, "?", "?"));
            }
        }

        if (!issues.Any(x => x.Level == IssueLevel.Error))
        {
            issues.Add(ValidationIssue.Ok(MessageCodes.BindingOk, field, endpoint));
        }

        return issues;
    }

    public NetworkInfo GetNetworkInfo(int? enginePid)
    {
        var snapshot = TakeSnapshot();
        return new NetworkInfo
        {
            MachineName = HostNetwork.MachineName,
            Fqdn = HostNetwork.GetFqdn(),
            Addresses = HostNetwork.GetIPv4Addresses().Select(a => new HostAddress { Address = a.Address, InterfaceName = a.InterfaceName }).ToList(),
            Listeners = snapshot.Listeners
                .Select(l => new PortListener
                {
                    Address = l.Address,
                    Port = l.Port,
                    ProcessId = l.ProcessId,
                    ProcessName = l.ProcessName,
                    IsEngine = enginePid is not null && l.ProcessId == enginePid
                })
                .OrderBy(l => l.Port)
                .ThenBy(l => l.Address, StringComparer.Ordinal)
                .ToList(),
            ExcludedRanges = snapshot.Excluded.Select(r => new PortRange { Start = r.Start, End = r.End }).ToList(),
            ListenerInfoAvailable = snapshot.Available
        };
    }

    public bool IsPortFree(int port, string? address, PortSnapshot snapshot, int? enginePid)
    {
        if (snapshot.Excluded.Any(r => port >= r.Start && port <= r.End))
        {
            return false;
        }

        if (snapshot.Available)
        {
            return !snapshot.Listeners.Any(l => l.Port == port && l.ProcessId != (enginePid ?? -1) &&
                                                Endpoint.Overlaps(l.Address == "0.0.0.0" ? null : l.Address, address));
        }

        return PortProbe.CanBind(address, port, out _);
    }
}
