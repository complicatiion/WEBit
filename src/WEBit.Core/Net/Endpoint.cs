using System.Net;
using System.Net.Sockets;

namespace WEBit.Core.Net;

/// <summary>IP address and endpoint helpers.</summary>
public static class Endpoint
{
    public static bool IsWildcard(string? address) =>
        string.IsNullOrWhiteSpace(address) || address.Trim() is "0.0.0.0" or "::" or "*";

    public static bool IsLoopback(string? address) =>
        IPAddress.TryParse(address?.Trim(), out var ip) && IPAddress.IsLoopback(ip);

    public static bool TryParse(string? address, out IPAddress ip)
    {
        if (IsWildcard(address))
        {
            ip = IPAddress.Any;
            return true;
        }

        if (IPAddress.TryParse(address!.Trim(), out var parsed) && parsed.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
        {
            ip = parsed;
            return true;
        }

        ip = IPAddress.None;
        return false;
    }

    /// <summary>Caddy listen key, e.g. "10.0.0.5:47101" or ":47101" for all addresses.</summary>
    public static string ListenKey(string? address, int port)
    {
        if (IsWildcard(address))
        {
            return $":{port}";
        }

        var a = address!.Trim();
        return a.Contains(':') ? $"[{a}]:{port}" : $"{a}:{port}";
    }

    /// <summary>Display form.</summary>
    public static string Display(string? address, int port) =>
        IsWildcard(address) ? $"0.0.0.0:{port}" : ListenKey(address, port);

    /// <summary>Same address or one of them is a wildcard.</summary>
    public static bool Overlaps(string? a, string? b) =>
        IsWildcard(a) || IsWildcard(b) || string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    public static bool SameAddress(string? a, string? b) =>
        (IsWildcard(a) && IsWildcard(b)) || string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Validates CIDR (e.g. 10.0.0.0/8) or a single IP.</summary>
    public static bool IsValidNetwork(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Trim().Split('/');
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var ip))
        {
            return false;
        }

        if (parts.Length == 1)
        {
            return true;
        }

        var max = ip.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        return int.TryParse(parts[1], out var prefix) && prefix >= 0 && prefix <= max;
    }
}
