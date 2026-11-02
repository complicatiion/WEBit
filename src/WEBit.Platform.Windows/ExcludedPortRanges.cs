using System.Text.RegularExpressions;
using WEBit.Core.Diagnostics;

namespace WEBit.Platform.Windows;

/// <summary>Port ranges reserved by Windows (Hyper-V, WinNAT, Docker, manual).</summary>
public static partial class ExcludedPortRanges
{
    [GeneratedRegex(@"^\s*(\d{1,5})\s+(\d{1,5})\b", RegexOptions.Multiline)]
    private static partial Regex RangeLine();

    public static IReadOnlyList<(int Start, int End)> Get()
    {
        var result = new List<(int, int)>();
        var run = ProcessRunner.Run(
            "netsh",
            ["int", "ipv4", "show", "excludedportrange", "protocol=tcp"],
            TimeSpan.FromSeconds(8));

        if (!run.Success)
        {
            return result;
        }

        foreach (Match match in RangeLine().Matches(run.Output))
        {
            if (int.TryParse(match.Groups[1].Value, out var start) &&
                int.TryParse(match.Groups[2].Value, out var end) &&
                start is >= 1 and <= 65535 && end >= start && end <= 65535)
            {
                result.Add((start, end));
            }
        }

        return result;
    }
}
