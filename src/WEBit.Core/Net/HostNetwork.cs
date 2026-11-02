using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WEBit.Core.Net;

/// <summary>Host network addresses.</summary>
public static class HostNetwork
{
    public sealed record HostAddressInfo(string Address, string InterfaceName, bool IsLoopback);

    public static IReadOnlyList<HostAddressInfo> GetIPv4Addresses(bool includeLoopback = false)
    {
        var result = new List<HostAddressInfo>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                var isLoopbackNic = nic.NetworkInterfaceType == NetworkInterfaceType.Loopback;
                if (isLoopbackNic && !includeLoopback)
                {
                    continue;
                }

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    var loopback = IPAddress.IsLoopback(unicast.Address);
                    if (loopback && !includeLoopback)
                    {
                        continue;
                    }

                    // Skip APIPA addresses (169.254.x.x).
                    var bytes = unicast.Address.GetAddressBytes();
                    if (bytes[0] == 169 && bytes[1] == 254)
                    {
                        continue;
                    }

                    result.Add(new HostAddressInfo(unicast.Address.ToString(), nic.Name, loopback));
                }
            }
        }
        catch (NetworkInformationException)
        {
            // No network information: empty list.
        }

        return result
            .GroupBy(a => a.Address)
            .Select(g => g.First())
            .OrderBy(a => a.IsLoopback)
            .ThenBy(a => a.Address, StringComparer.Ordinal)
            .ToList();
    }

    public static bool IsLocalAddress(string? address)
    {
        if (Endpoint.IsWildcard(address) || Endpoint.IsLoopback(address))
        {
            return true;
        }

        if (!IPAddress.TryParse(address?.Trim(), out var ip))
        {
            return false;
        }

        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Any(u => u.Address.Equals(ip));
        }
        catch (NetworkInformationException)
        {
            return true;
        }
    }

    public static string MachineName => Environment.MachineName;

    /// <summary>Fully qualified host name if a domain is set.</summary>
    public static string GetFqdn()
    {
        try
        {
            var props = IPGlobalProperties.GetIPGlobalProperties();
            var host = string.IsNullOrWhiteSpace(props.HostName) ? Environment.MachineName : props.HostName;
            var domain = props.DomainName;
            return string.IsNullOrWhiteSpace(domain) ? host.ToLowerInvariant() : $"{host}.{domain}".ToLowerInvariant();
        }
        catch
        {
            return Environment.MachineName.ToLowerInvariant();
        }
    }

    public static string? FirstIPv4() => GetIPv4Addresses().FirstOrDefault()?.Address;
}
