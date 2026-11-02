using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace WEBit.App.Views;

/// <summary>Compact tray panel (left click): sites, quick actions, open manager.</summary>
public partial class TrayFlyoutWindow : Window
{
    public TrayFlyoutWindow()
    {
        InitializeComponent();
        Deactivated += (_, _) =>
        {
            LastHidden = DateTime.UtcNow;
            Hide();
        };
    }

    /// <summary>Last hide time; prevents the tray click from reopening the panel immediately.</summary>
    public DateTime LastHidden { get; private set; } = DateTime.MinValue;

    public event Action? OpenManagerRequested;

    public event Action? OpenUrlsRequested;

    /// <summary>Shows the panel bottom right above the notification area.</summary>
    public void ShowNearTray()
    {
        Opacity = 0;
        Show();
        Activate();
        Dispatcher.UIThread.Post(() =>
        {
            PositionNearTray();
            Opacity = 1;
        }, DispatcherPriority.Loaded);
    }

    private void PositionNearTray()
    {
        var screen = Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var area = screen.WorkingArea;
        var scaling = screen.Scaling;
        var width = (int)Math.Ceiling(Bounds.Width * scaling);
        var height = (int)Math.Ceiling(Bounds.Height * scaling);
        var margin = (int)(12 * scaling);
        Position = new PixelPoint(area.Right - width - margin, area.Bottom - height - margin);
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        LastHidden = DateTime.UtcNow;
        Hide();
    }

    private void OnOpenManager(object? sender, RoutedEventArgs e)
    {
        Hide();
        OpenManagerRequested?.Invoke();
    }

    private void OnOpenUrls(object? sender, RoutedEventArgs e)
    {
        Hide();
        OpenUrlsRequested?.Invoke();
    }
}
