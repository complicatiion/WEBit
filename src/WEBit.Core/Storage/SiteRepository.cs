using WEBit.Core.Models;

namespace WEBit.Core.Storage;

/// <summary>One JSON file per site in config/sites.</summary>
public sealed class SiteRepository(WebitPaths paths)
{
    public IReadOnlyList<SiteDefinition> LoadAll(Action<string, Exception>? onError = null)
    {
        var result = new List<SiteDefinition>();
        if (!Directory.Exists(paths.SitesDir))
        {
            return result;
        }

        foreach (var file in Directory.EnumerateFiles(paths.SitesDir, "*.json"))
        {
            try
            {
                var site = JsonFile.Read<SiteDefinition>(file);
                if (site is null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(site.Id))
                {
                    site.Id = Path.GetFileNameWithoutExtension(file);
                }

                site.Normalize();
                result.Add(site);
            }
            catch (Exception ex)
            {
                onError?.Invoke(file, ex);
            }
        }

        return result;
    }

    public void Save(SiteDefinition site) => JsonFile.Write(FileFor(site.Id), site);

    public void Delete(string id)
    {
        var file = FileFor(id);
        if (File.Exists(file))
        {
            File.Delete(file);
        }
    }

    private string FileFor(string id) => Path.Combine(paths.SitesDir, SiteIds.EnsureSafe(id) + ".json");
}

/// <summary>Service settings store.</summary>
public sealed class SettingsRepository(WebitPaths paths)
{
    public WebitSettings Load()
    {
        WebitSettings? settings = null;
        try
        {
            settings = JsonFile.Read<WebitSettings>(paths.SettingsFile);
        }
        catch
        {
            // Corrupt file: use defaults, keep the file for analysis.
        }

        settings ??= new WebitSettings();
        settings.Normalize();
        return settings;
    }

    public void Save(WebitSettings settings) => JsonFile.Write(paths.SettingsFile, settings);
}
