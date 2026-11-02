namespace WEBit.Core.Sites;

/// <summary>Card color presets; new sites without a choice get a random, preferably unused one.</summary>
public static class SiteColors
{
    /// <summary>Editor value for "pick one for me".</summary>
    public const string Random = "random";

    public static readonly IReadOnlyList<string> Palette =
    [
        "#118AB2", "#7CD5C7", "#4C9F70", "#F2B84B", "#E5606B", "#9B7FD4", "#F28C4B", "#464B71",
        "#3A86FF", "#00B4D8", "#06D6A0", "#8AC926", "#FFCA3A", "#FF7AA2", "#C77DFF", "#A0A4B8"
    ];

    public static bool IsRandom(string? color) =>
        string.IsNullOrWhiteSpace(color) || string.Equals(color, Random, StringComparison.OrdinalIgnoreCase);

    /// <summary>Random palette color, colors not used by other sites first.</summary>
    public static string Pick(IEnumerable<string?> used)
    {
        var taken = new HashSet<string>(used.Where(c => c is not null)!, StringComparer.OrdinalIgnoreCase);
        var free = Palette.Where(c => !taken.Contains(c)).ToList();
        var pool = free.Count > 0 ? free : Palette.ToList();
        return pool[System.Random.Shared.Next(pool.Count)];
    }
}
