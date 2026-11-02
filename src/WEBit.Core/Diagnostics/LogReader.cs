using System.Globalization;
using System.Text;
using System.Text.Json;

namespace WEBit.Core.Diagnostics;

/// <summary>Log tail reader and Caddy JSON line formatter.</summary>
public static class LogReader
{
    public static List<string> Tail(string path, int lines)
    {
        var result = new List<string>();
        if (!File.Exists(path) || lines <= 0)
        {
            return result;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        const long window = 2 * 1024 * 1024;
        if (stream.Length > window)
        {
            stream.Seek(-window, SeekOrigin.End);
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        if (stream.Position > 0)
        {
            reader.ReadLine(); // angeschnittene erste Zeile verwerfen
        }

        var queue = new Queue<string>(lines + 1);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            queue.Enqueue(line);
            if (queue.Count > lines)
            {
                queue.Dequeue();
            }
        }

        result.AddRange(queue);
        return result;
    }

    /// <summary>Formats a Caddy JSON log line; other lines pass through.</summary>
    public static string Format(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{')
        {
            return line;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var time = root.TryGetProperty("ts", out var ts) && ts.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)(ts.GetDouble() * 1000)).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                : "";

            if (root.TryGetProperty("request", out var request) && request.ValueKind == JsonValueKind.Object)
            {
                var status = root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32().ToString(CultureInfo.InvariantCulture) : "-";
                var method = Str(request, "method");
                var host = Str(request, "host");
                var uri = Str(request, "uri");
                var ip = Str(request, "client_ip");
                if (string.IsNullOrEmpty(ip))
                {
                    ip = Str(request, "remote_ip");
                }

                var duration = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                    ? (d.GetDouble() * 1000).ToString("0.0", CultureInfo.InvariantCulture) + " ms"
                    : "";
                var size = root.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number
                    ? FormatBytes(sz.GetInt64())
                    : "";
                return $"{time}  {status}  {method,-6} {host}{uri}  {ip}  {duration}  {size}".TrimEnd();
            }

            var level = Str(root, "level").ToUpperInvariant();
            var logger = Str(root, "logger");
            var msg = Str(root, "msg");
            var extra = root.TryGetProperty("error", out var err) ? "  " + err.ToString() : "";
            return $"{time}  {level,-5}  {(string.IsNullOrEmpty(logger) ? "" : logger + ": ")}{msg}{extra}";
        }
        catch (JsonException)
        {
            return line;
        }
    }

    private static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : value.ToString("0.0", CultureInfo.InvariantCulture) + " " + units[unit];
    }
}
