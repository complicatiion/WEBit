using System.IO.Pipes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using WEBit.Contracts;
using WEBit.Core.Storage;
using WEBit.Service.Checks;
using WEBit.Service.Engine;
using WEBit.Service.Logging;
using WEBit.Service.Modules;
using WEBit.Service.Pipe;
using WEBit.Service.Setup;
using WEBit.Service.Sites;

// WEBit.Service.exe                              runs as Windows service or in desktop mode
// WEBit.Service.exe --console                    desktop mode with console output
// WEBit.Service.exe --install [--user DOMAIN\x]  register service (admin)
// WEBit.Service.exe --uninstall [--purge]        remove service, group, autostart; --purge also deletes data (admin)

var paths = WebitPaths.Resolve();

if (args.Any(a => string.Equals(a, "--install", StringComparison.OrdinalIgnoreCase)))
{
    return ServiceSetup.Install(paths, args);
}

if (args.Any(a => string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase)))
{
    return ServiceSetup.Uninstall(paths, args);
}

// Only one instance (service or desktop mode) may own the control pipe.
if (IsAlreadyRunning())
{
    Console.WriteLine("WEBit is already running (control pipe in use).");
    return 2;
}

paths.EnsureCreated();

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Services.AddWindowsService(options => options.ServiceName = "WEBit");
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(30));

builder.Logging.ClearProviders();
// The file provider filters by Settings → Logs (level can be changed at runtime).
builder.Logging.SetMinimumLevel(LogLevel.Debug);
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("System", LogLevel.Warning);
var fileLog = new FileLoggerProvider(paths.ServiceLogFile);
builder.Logging.AddProvider(fileLog);
builder.Services.AddSingleton(fileLog);
if (!(OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService()))
{
    builder.Logging.AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "HH:mm:ss ";
    });
}

builder.Services.AddSingleton(paths);
builder.Services.AddSingleton<ProcessJob>();
builder.Services.AddSingleton<CaddyEngine>();
builder.Services.AddSingleton<ModuleManager>();
builder.Services.AddSingleton<WEBit.Service.Apps.AppManager>();
builder.Services.AddSingleton<WEBit.Service.Tools.PhpTools>();
builder.Services.AddSingleton<ModuleDownloader>();
builder.Services.AddSingleton<WEBit.Service.Live.LiveReloadManager>();
builder.Services.AddSingleton<EnvironmentChecker>();
builder.Services.AddSingleton<StatusBroadcaster>();
builder.Services.AddSingleton<UrlChecker>();
builder.Services.AddSingleton<SettingsRepositoryAccessor>();
builder.Services.AddSingleton<SiteManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SiteManager>());
builder.Services.AddSingleton<RequestDispatcher>();
builder.Services.AddHostedService<PipeServer>();

using var host = builder.Build();
await host.RunAsync();
return 0;

static bool IsAlreadyRunning()
{
    try
    {
        using var probe = new NamedPipeClientStream(".", Protocol.PipeName, PipeDirection.InOut);
        probe.Connect(300);
        return true;
    }
    catch (TimeoutException)
    {
        return false;
    }
    catch (UnauthorizedAccessException)
    {
        // Pipe exists but is not accessible: service is running.
        return true;
    }
    catch (IOException)
    {
        return false;
    }
}
