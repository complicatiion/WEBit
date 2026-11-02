using System.Text.Json;
using WEBit.Core.Serialization;

namespace WEBit.Core.Storage;

/// <summary>Atomic JSON file read/write (temp file, then replace).</summary>
public static class JsonFile
{
    public static T? Read<T>(string path)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<T>(stream, WebitJson.File);
    }

    public static void Write<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, WebitJson.File), WebitJson.Utf8NoBom);
        File.Move(temp, path, overwrite: true);
    }
}
