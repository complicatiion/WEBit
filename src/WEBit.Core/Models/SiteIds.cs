using System.Text;

namespace WEBit.Core.Models;

/// <summary>File- and URL-safe ids from site names.</summary>
public static partial class SiteIds
{
    public static string Slugify(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "site";
        }

        var value = name.Trim().ToLowerInvariant()
            .Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");

        var sb = new StringBuilder(value.Length);
        var lastDash = false;
        foreach (var ch in value)
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append(ch);
                lastDash = false;
            }
            else if (!lastDash && sb.Length > 0)
            {
                sb.Append('-');
                lastDash = true;
            }
        }

        var slug = sb.ToString().Trim('-');
        if (slug.Length > 40)
        {
            slug = slug[..40].Trim('-');
        }

        return slug.Length == 0 ? "site" : slug;
    }

    public static string CreateUnique(string? name, IEnumerable<string> existingIds)
    {
        var existing = new HashSet<string>(existingIds, StringComparer.OrdinalIgnoreCase);
        var baseId = Slugify(name);
        if (baseId.Length > 34)
        {
            baseId = baseId[..34].Trim('-');
        }

        var id = baseId;
        var counter = 2;
        while (existing.Contains(id))
        {
            id = $"{baseId}-{counter++}";
        }

        return id;
    }

    /// <summary>Rejects ids that could manipulate paths.</summary>
    public static string EnsureSafe(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64 || !SafeId().IsMatch(id))
        {
            throw new ArgumentException($"Invalid site id: {id}", nameof(id));
        }

        return id;
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial System.Text.RegularExpressions.Regex SafeId();
}
