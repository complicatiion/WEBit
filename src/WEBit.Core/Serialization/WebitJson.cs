using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WEBit.Core.Serialization;

/// <summary>Shared JSON options for files and the pipe protocol.</summary>
public static class WebitJson
{
    /// <summary>Config files: indented.</summary>
    public static readonly JsonSerializerOptions File = Create(indented: true);

    /// <summary>Pipe protocol: compact, one message per line.</summary>
    public static readonly JsonSerializerOptions Wire = Create(indented: false);

    public static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
