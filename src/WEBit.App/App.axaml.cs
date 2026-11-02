using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using WEBit.App.Localization;
using WEBit.App.Services;
using WEBit.App.ViewModels;
using WEBit.App.Views;

namespace WEBit.App;

public partial class App : Application
{
    private ServiceConnection? _connection;
    private TrayController? _tray;
    private MainWindow? _mainWindow;
    private MainViewModel? _viewModel;
    private IClassicDesktopStyleApplicationLifetime? _desktop;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var ui = UiSettings.Load();
            if (Program.RequestDesktopMode && ui.AutoDesktopMode is null)
            {
                ui.AutoDesktopMode = true;
                ui.Save();
            }
            Loc.Apply(ui.Language ?? Loc.DefaultLanguage);

            _connection = new ServiceConnection();
            var host = new UiHost();
            _viewModel = new MainViewModel(_connection, host, ui);

            _mainWindow = new MainWindow { DataContext = _viewModel };
            host.MainWindow = _mainWindow;
            host.ShowMainWindow = ShowMainWindow;
            host.ExitApplication = Exit;

            _tray = new TrayController(this, _viewModel, ShowMainWindow, Exit);

            desktop.Exit += (_, _) =>
            {
                _tray?.Dispose();
                _connection?.Dispose();
            };

            StartShowSignalListener();
            _connection.Start();

            if (!Program.StartInTray)
            {
                ShowMainWindow();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        var window = _mainWindow;
        var wasHidden = !window.IsVisible;
        if (wasHidden)
        {
            // Show restored first: maximizing a window before it has a native size
            // leaves the title bar partly above the screen.
            window.WindowState = WindowState.Normal;
            window.Show();
        }

        // Opening from tray or minimized: always maximized, after the first layout pass.
        if (wasHidden || window.WindowState == WindowState.Minimized)
        {
            Dispatcher.UIThread.Post(() => window.WindowState = WindowState.Maximized, DispatcherPriority.Loaded);
        }

        window.Activate();
    }

    private void Exit()
    {
        if (_mainWindow is not null)
        {
            _mainWindow.AllowClose = true;
            _mainWindow.Close();
        }

        _desktop?.Shutdown();
    }

    /// <summary>A second app start signals "show window".</summary>
    private void StartShowSignalListener()
    {
        var signal = Program.ShowSignal;
        if (signal is null)
        {
            return;
        }

        var thread = new Thread(() =>
        {
            try
            {
                while (signal.WaitOne())
                {
                    Dispatcher.UIThread.Post(ShowMainWindow);
                }
            }
            catch (ObjectDisposedException)
            {
                // App is exiting.
            }
        })
        {
            IsBackground = true,
            Name = "WEBit.ShowSignal"
        };
        thread.Start();
    }
}
