using System.Globalization;
using Microsoft.Extensions.Logging;
using WEBit.Core.Serialization;

namespace WEBit.Service.Logging;

/// <summary>File logger with rotation; level, size and archives follow Settings → Logs.</summary>
public sealed class FileLoggerProvider(string path, LogLevel minimumLevel = LogLevel.Information) : ILoggerProvider
{
    private readonly object _lock = new();
    private volatile bool _enabled = true;
    private LogLevel _minimumLevel = minimumLevel;
    private long _maxBytes = 10L * 1024 * 1024;
    private int _archives = 5;

    public void Configure(bool enabled, LogLevel level, int maxSizeMb, int archives)
    {
        _enabled = enabled;
        _minimumLevel = level;
        Interlocked.Exchange(ref _maxBytes, Math.Max(1, maxSizeMb) * 1024L * 1024);
        _archives = Math.Max(0, archives);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, ShortCategory(categoryName));

    public void Dispose()
    {
    }

    // Warnings and errors are always written, even with the service log switched off.
    internal bool IsEnabled(LogLevel level) =>
        level != LogLevel.None && level >= _minimumLevel && (_enabled || level >= LogLevel.Warning);

    internal void Write(string line)
    {
        lock (_lock)
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var info = new FileInfo(path);
                if (info.Exists && info.Length > Interlocked.Read(ref _maxBytes))
                {
                    Roll();
                }

                File.AppendAllText(path, line + Environment.NewLine, WebitJson.Utf8NoBom);
            }
            catch
            {
                // Logging must never break the service.
            }
        }
    }

    private void Roll()
    {
        var archives = _archives;
        if (archives == 0)
        {
            File.Delete(path);
            return;
        }

        File.Delete($"{path}.{archives}");
        for (var i = archives - 1; i >= 1; i--)
        {
            var source = $"{path}.{i}";
            if (File.Exists(source))
            {
                File.Move(source, $"{path}.{i + 1}", overwrite: true);
            }
        }

        File.Move(path, $"{path}.1", overwrite: true);
    }

    private static string ShortCategory(string category)
    {
        var index = category.LastIndexOf('.');
        return index >= 0 ? category[(index + 1)..] : category;
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var level = logLevel switch
            {
                LogLevel.Trace => "TRC",
                LogLevel.Debug => "DBG",
                LogLevel.Information => "INF",
                LogLevel.Warning => "WRN",
                LogLevel.Error => "ERR",
                LogLevel.Critical => "CRT",
                _ => "   "
            };

            var message = formatter(state, exception);
            var line = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}  {level}  {category}: {message}";
            if (exception is not null)
            {
                line += Environment.NewLine + "    " + exception.ToString().Replace(Environment.NewLine, Environment.NewLine + "    ");
            }

            provider.Write(line);
        }
    }
}
