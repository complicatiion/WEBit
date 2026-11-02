namespace WEBit.Core.Net;

/// <summary>
/// Port rules: avoid standard and development ports used by other applications.
/// </summary>
public static class PortPolicy
{
    public sealed record AvoidedRange(int From, int To, string Reason);

    public static readonly IReadOnlyList<AvoidedRange> Avoided =
    [
        new(1, 1023, "System / well-known ports"),
        new(1433, 1434, "SQL Server"),
        new(1521, 1521, "Oracle"),
        new(2375, 2376, "Docker"),
        new(3000, 3001, "Node.js dev server"),
        new(3306, 3306, "MySQL/MariaDB"),
        new(3389, 3389, "RDP"),
        new(4200, 4200, "Angular dev server"),
        new(5000, 5300, "ASP.NET Core templates"),
        new(5173, 5173, "Vite"),
        new(5432, 5432, "PostgreSQL"),
        new(5985, 5986, "WinRM"),
        new(6379, 6379, "Redis"),
        new(7000, 7300, "ASP.NET Core templates (HTTPS)"),
        new(8000, 8000, "Dev server"),
        new(8008, 8008, "Alternative HTTP"),
        new(8080, 8081, "Alternative HTTP / proxy"),
        new(8443, 8443, "Alternative HTTPS"),
        new(8888, 8888, "Dev server / Jupyter"),
        new(9000, 9000, "Dev server / PHP-FPM"),
        new(9090, 9090, "Admin consoles"),
        new(9200, 9300, "Elasticsearch"),
        new(27017, 27017, "MongoDB"),
        new(49152, 65535, "Windows dynamic port range")
    ];

    /// <summary>Allowed despite the avoided range (dedicated IP).</summary>
    public static readonly IReadOnlySet<int> AllowedWellKnown = new HashSet<int> { 80, 443 };

    public static AvoidedRange? AvoidReason(int port)
    {
        if (AllowedWellKnown.Contains(port))
        {
            return null;
        }

        return Avoided.FirstOrDefault(r => port >= r.From && port <= r.To);
    }

    public static bool IsValidPort(int port) => port is >= 1 and <= 65535;

    /// <summary>First free, non-avoided port in the range.</summary>
    public static int? Suggest(int start, int end, Func<int, bool> isFree)
    {
        for (var port = Math.Max(1, start); port <= Math.Min(65535, end); port++)
        {
            if (AvoidReason(port) is null && isFree(port))
            {
                return port;
            }
        }

        return null;
    }
}
