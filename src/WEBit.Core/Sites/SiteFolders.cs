namespace WEBit.Core.Sites;

/// <summary>Folder names of new sites: the site name, made safe for Windows.</summary>
public static class SiteFolders
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>"My Shop: Test" → "My Shop- Test"; empty or reserved names → "site".</summary>
    public static string FromName(string? name)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };
        var chars = (name ?? "").Trim().Select(c => invalid.Contains(c) || char.IsControl(c) ? '-' : c).ToArray();
        var folder = new string(chars).Trim().TrimEnd('.', ' ');
        if (folder.Length > 80)
        {
            folder = folder[..80].TrimEnd('.', ' ');
        }

        return folder.Length == 0 || Reserved.Contains(folder) ? "site" : folder;
    }

    /// <summary>First free "name", "name-2", "name-3" … in the parent folder.</summary>
    public static string Suggest(string parent, string folder)
    {
        if (!Directory.Exists(Path.Combine(parent, folder)))
        {
            return folder;
        }

        for (var i = 2; ; i++)
        {
            var candidate = $"{folder}-{i}";
            if (!Directory.Exists(Path.Combine(parent, candidate)))
            {
                return candidate;
            }
        }
    }
}
