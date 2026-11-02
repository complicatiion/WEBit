using Avalonia.Controls;
using Avalonia.Threading;
using WEBit.Platform.Windows;

namespace WEBit.App.Services;

/// <summary>Common look of WEBit windows: dark-blue native title bar, optionally maximized after opening.</summary>
public static class WindowChrome
{
    // Matches BrushBgSidebar (#202236) and BrushTextPrimary (#F2F2ED).
    private const int CaptionColor = 0x202236;
    private const int CaptionTextColor = 0xF2F2ED;

    public static void Apply(Window window, bool maximize)
    {
        window.Opened += (_, _) =>
        {
            if (OperatingSystem.IsWindows() && window.TryGetPlatformHandle()?.Handle is { } hwnd)
            {
                DwmTitleBar.Apply(hwnd, CaptionColor, CaptionTextColor);
            }

            if (maximize)
            {
                // After the first layout pass: maximizing earlier can push the title bar off screen.
                Dispatcher.UIThread.Post(() => window.WindowState = WindowState.Maximized, DispatcherPriority.Loaded);
            }
        };
    }
}
