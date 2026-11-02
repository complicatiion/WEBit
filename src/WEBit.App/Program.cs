using Avalonia;

namespace WEBit.App;

internal static class Program
{
    /// <summary>Signal to the running instance: show window.</summary>
    internal static EventWaitHandle? ShowSignal { get; private set; }

    internal static bool StartInTray { get; private set; }

    /// <summary>--desktop-mode (set by the desktop installation): remembered in ui.json.</summary>
    internal static bool RequestDesktopMode { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        StartInTray = args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));
        RequestDesktopMode = args.Any(a => string.Equals(a, "--desktop-mode", StringComparison.OrdinalIgnoreCase));

        Mutex? mutex = null;
        if (OperatingSystem.IsWindows())
        {
            // One instance per session; a second start brings the window to front.
            mutex = new Mutex(true, @"Local\WEBit.App", out var createdNew);
            if (!createdNew)
            {
                try
                {
                    using var existing = EventWaitHandle.OpenExisting(@"Local\WEBit.App.Show");
                    existing.Set();
                }
                catch
                {
                    // First instance not ready yet.
                }

                mutex.Dispose();
                return 0;
            }

            ShowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\WEBit.App.Show");
        }

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        finally
        {
            ShowSignal?.Dispose();
            mutex?.ReleaseMutex();
            mutex?.Dispose();
        }
    }

    // Also used by the Avalonia designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
