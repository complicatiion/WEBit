using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WEBit.App.Localization;
using WEBit.App.Services;
using WEBit.Contracts;
using WEBit.Platform.Windows;

namespace WEBit.App.ViewModels;

/// <summary>Main window: navigation, connection state, toasts, service actions.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly DispatcherTimer _toastTimer;

    public MainViewModel(ServiceConnection connection, UiHost host, UiSettings ui)
    {
        Connection = connection;
        Host = host;
        Ui = ui;

        Dashboard = new DashboardViewModel(this);
        Sites = new SitesViewModel(this);
        Urls = new UrlsViewModel(this);
        Modules = new ModulesViewModel(this);
        Databases = new DatabasesViewModel(this);
        Dependencies = new DependenciesViewModel(this);
        Network = new NetworkViewModel(this);
        Logs = new LogsViewModel(this);
        Settings = new SettingsViewModel(this);
        About = new AboutViewModel(this);

        TopNav =
        [
            new NavItem("NavDashboard", "IconDashboard", Dashboard),
            new NavItem("NavSites", "IconWeb", Sites),
            new NavItem("NavUrls", "IconLink", Urls),
            new NavItem("NavDatabases", "IconDatabase", Databases),
            new NavItem("NavModules", "IconPuzzle", Modules),
            new NavItem("NavDependencies", "IconPackage", Dependencies),
            new NavItem("NavNetwork", "IconLan", Network),
            new NavItem("NavLogs", "IconLog", Logs)
        ];

        BottomNav =
        [
            new NavItem("NavSettings", "IconCog", Settings),
            new NavItem("NavAbout", "IconInfo", About)
        ];

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast = null;
        };

        connection.StateChanged += state => Dispatcher.UIThread.Post(() => OnConnectionState(state));
        connection.StatusReceived += QueueStatus;
        Loc.LanguageChanged += OnLanguageChanged;

        SelectedTopNav = TopNav[0];
    }

    public ServiceConnection Connection { get; }

    public UiHost Host { get; }

    public UiSettings Ui { get; }

    public DashboardViewModel Dashboard { get; }

    public SitesViewModel Sites { get; }

    public UrlsViewModel Urls { get; }

    public ModulesViewModel Modules { get; }

    public DatabasesViewModel Databases { get; }

    public DependenciesViewModel Dependencies { get; }

    public NetworkViewModel Network { get; }

    public LogsViewModel Logs { get; }

    public SettingsViewModel Settings { get; }

    public AboutViewModel About { get; }

    public ObservableCollection<NavItem> TopNav { get; }

    public ObservableCollection<NavItem> BottomNav { get; }

    /// <summary>Raised on status, connection or language changes (tray menu).</summary>
    public event Action? StatusUpdated;

    // ------------------------------------------------------------------ Navigation

    [ObservableProperty]
    private PageViewModel? _currentPage;

    [ObservableProperty]
    private NavItem? _selectedTopNav;

    [ObservableProperty]
    private NavItem? _selectedBottomNav;

    /// <summary>Sidebar starts expanded; the logo toggles it.</summary>
    [ObservableProperty]
    private bool _isSidebarCollapsed;

    public double SidebarWidth => IsSidebarCollapsed ? 68 : 236;

    partial void OnSelectedTopNavChanged(NavItem? value)
    {
        if (value is null)
        {
            return;
        }

        SelectedBottomNav = null;
        Activate(value.Page);
    }

    partial void OnSelectedBottomNavChanged(NavItem? value)
    {
        if (value is null)
        {
            return;
        }

        SelectedTopNav = null;
        Activate(value.Page);
    }

    /// <summary>Icon of the current page (header, left of the title).</summary>
    public Geometry? CurrentPageIcon =>
        TopNav.Concat(BottomNav).FirstOrDefault(n => ReferenceEquals(n.Page, CurrentPage))?.Icon;

    partial void OnCurrentPageChanged(PageViewModel? oldValue, PageViewModel? newValue)
    {
        OnPropertyChanged(nameof(CurrentPageIcon));
        oldValue?.OnDeactivated();
        newValue?.OnActivated();
        OnPropertyChanged(nameof(ShowOfflinePanel));
        OnPropertyChanged(nameof(ShowPage));
    }

    partial void OnIsSidebarCollapsedChanged(bool value) => OnPropertyChanged(nameof(SidebarWidth));

    private void Activate(PageViewModel page)
    {
        if (!ReferenceEquals(CurrentPage, page))
        {
            CurrentPage = page;
        }
    }

    public void NavigateTo(PageViewModel page)
    {
        var item = TopNav.FirstOrDefault(n => ReferenceEquals(n.Page, page));
        if (item is not null)
        {
            SelectedTopNav = item;
            return;
        }

        var bottom = BottomNav.FirstOrDefault(n => ReferenceEquals(n.Page, page));
        if (bottom is not null)
        {
            SelectedBottomNav = bottom;
        }
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarCollapsed = !IsSidebarCollapsed;

    // ------------------------------------------------------------------ Connection & status

    [ObservableProperty]
    private ConnectionState _connectionState = ConnectionState.Connecting;

    [ObservableProperty]
    private ServiceStatus? _status;

    public bool IsConnected => ConnectionState == ConnectionState.Connected;

    public bool ShowOfflinePanel => !IsConnected && (CurrentPage?.RequiresService ?? true);

    public bool ShowPage => !ShowOfflinePanel;

    public bool IsDesktopMode => IsConnected && Status is { IsWindowsService: false };

    /// <summary>Native title bar text: name and version.</summary>
    public string WindowTitle { get; } = $"WEBit  v{AppEnvironment.Version}";

    public string StatusPillText => ConnectionState switch
    {
        ConnectionState.Connected when Status is not null => Status.IsWindowsService ? Loc.T("PillService") : Loc.T("PillDesktopMode"),
        ConnectionState.Connected => Loc.T("PillConnected"),
        ConnectionState.AccessDenied => Loc.T("PillAccessDenied"),
        ConnectionState.Incompatible => Loc.T("PillIncompatible"),
        ConnectionState.Connecting => Loc.T("PillConnecting"),
        _ => Loc.T("PillOffline")
    };

    /// <summary>Status color at 20 % for the pill background.</summary>
    public IBrush StatusPillBackground => StatusDotBrush is ISolidColorBrush solid
        ? new SolidColorBrush(solid.Color, 0.18)
        : Res.Brush("BrushSurfaceRaised");

    public IBrush StatusDotBrush => ConnectionState switch
    {
        ConnectionState.Connected when Status?.Engine.State == EngineState.Running => Res.Brush("BrushSecondary"),
        ConnectionState.Connected => Res.Brush("BrushWarning"),
        ConnectionState.Connecting => Res.Brush("BrushAccentHover"),
        _ => Res.Brush("BrushDanger")
    };

    public string OfflineTitle => ConnectionState switch
    {
        ConnectionState.AccessDenied => Loc.T("OfflineAccessDeniedTitle"),
        ConnectionState.Incompatible => Loc.T("OfflineIncompatibleTitle"),
        ConnectionState.Connecting => Loc.T("OfflineConnectingTitle"),
        _ => Loc.T("OfflineTitle")
    };

    public string OfflineText => ConnectionState switch
    {
        ConnectionState.AccessDenied => Loc.T("OfflineAccessDeniedText"),
        ConnectionState.Incompatible => Loc.F("OfflineIncompatibleText", AppEnvironment.Version, Connection.Hello?.ServiceVersion ?? "?"),
        ConnectionState.Connecting => Loc.T("OfflineConnectingText"),
        _ => Loc.T("OfflineText")
    };

    public bool CanStartDesktopMode => ConnectionState == ConnectionState.Disconnected;

    /// <summary>An older (or newer) service from another WEBit version is running.</summary>
    public bool CanUpdateService => ConnectionState == ConnectionState.Incompatible;

    public bool CanInstallService => OperatingSystem.IsWindows() && ConnectionState is ConnectionState.Disconnected or ConnectionState.AccessDenied;

    private bool _autoDesktopTried;

    private void OnConnectionState(ConnectionState state)
    {
        ConnectionState = state;
        if (state == ConnectionState.Connected)
        {
            // A service is there: never auto-start desktop mode later in this session.
            _autoDesktopTried = true;
        }

        // Desktop installation: no service reachable → start desktop mode once per app start.
        if (state == ConnectionState.Disconnected && Ui.AutoDesktopModeEffective && !_autoDesktopTried
            && !(OperatingSystem.IsWindows() && WindowsServiceControl.IsInstalled()))
        {
            _autoDesktopTried = true;
            _ = StartDesktopModeCommand.ExecuteAsync(null);
        }

        if (state == ConnectionState.Connected)
        {
            foreach (var page in AllPages())
            {
                page.OnConnected();
            }
        }

        RaiseStatusProperties();
        StatusUpdated?.Invoke();
    }

    private ServiceStatus? _pendingStatus;
    private int _statusQueued;

    /// <summary>
    /// Status pushes can arrive in bursts (downloads, many sites): only the newest one is applied,
    /// once per UI frame, so the UI thread stays free for input and page switches.
    /// </summary>
    private void QueueStatus(ServiceStatus status)
    {
        Volatile.Write(ref _pendingStatus, status);
        if (Interlocked.Exchange(ref _statusQueued, 1) == 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref _statusQueued, 0);
                if (Interlocked.Exchange(ref _pendingStatus, null) is { } latest)
                {
                    ApplyStatus(latest);
                }
            }, DispatcherPriority.Background);
        }
    }

    private void ApplyStatus(ServiceStatus status)
    {
        Status = status;
        foreach (var page in AllPages())
        {
            page.OnStatus(status);
        }

        RaiseStatusProperties();
        StatusUpdated?.Invoke();
    }

    private void RaiseStatusProperties()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(ShowOfflinePanel));
        OnPropertyChanged(nameof(ShowPage));
        OnPropertyChanged(nameof(IsDesktopMode));
        OnPropertyChanged(nameof(StatusPillText));
        OnPropertyChanged(nameof(StatusDotBrush));
        OnPropertyChanged(nameof(StatusPillBackground));
        OnPropertyChanged(nameof(OfflineTitle));
        OnPropertyChanged(nameof(OfflineText));
        OnPropertyChanged(nameof(CanStartDesktopMode));
        OnPropertyChanged(nameof(CanInstallService));
        OnPropertyChanged(nameof(CanUpdateService));
    }

    /// <summary>Modules and sites first: the dashboard reads their cards.</summary>
    private IEnumerable<PageViewModel> AllPages() =>
        [Modules, Dependencies, Sites, Urls, Databases, Dashboard, Network, Logs, Settings, About];

    private void OnLanguageChanged()
    {
        foreach (var item in TopNav.Concat(BottomNav))
        {
            item.RefreshText();
        }

        foreach (var page in AllPages())
        {
            page.OnLanguageChanged();
        }

        RaiseStatusProperties();
        StatusUpdated?.Invoke();
    }

    public void SetLanguage(string code)
    {
        if (code == Loc.Language)
        {
            return;
        }

        Ui.Language = code;
        Ui.Save();
        Loc.Apply(code);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (!IsConnected)
        {
            Connection.Reconnect();
            return;
        }

        try
        {
            var status = await Connection.RequestAsync<ServiceStatus>(Methods.GetStatus);
            if (status is not null)
            {
                // Through the queue: an older pushed status must not overwrite this one.
                QueueStatus(status);
            }

            CurrentPage?.OnActivated();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    [RelayCommand]
    private void Reconnect() => Connection.Reconnect();

    [RelayCommand]
    private void OpenWww() => AppEnvironment.OpenFolder(Status?.WwwRoot);

    // ------------------------------------------------------------------ Service

    [RelayCommand]
    private async Task StartDesktopModeAsync()
    {
        if (!AppEnvironment.StartDesktopMode())
        {
            ShowToast(Loc.F("ToastServiceExeMissing", AppEnvironment.BaseDirectory), isError: true);
            return;
        }

        ShowToast(Loc.T("ToastDesktopModeStarting"));
        await Task.Delay(1200);
        Connection.Reconnect();
    }

    [RelayCommand]
    private async Task StopDesktopModeAsync()
    {
        _autoDesktopTried = true;
        try
        {
            await Connection.RequestAsync(Methods.Shutdown);
            ShowToast(Loc.T("ToastDesktopModeStopped"));
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    [RelayCommand]
    private async Task InstallServiceAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var exe = AppEnvironment.FindServiceExecutable();
        if (exe is null)
        {
            ShowToast(Loc.F("ToastServiceExeMissing", AppEnvironment.BaseDirectory), isError: true);
            return;
        }

        var confirm = await Host.ConfirmAsync(Loc.T("DlgInstallServiceTitle"), Loc.T("DlgInstallServiceText"), Loc.T("BtnInstallService"), danger: false);
        if (!confirm.Confirmed)
        {
            return;
        }

        // Desktop mode holds the control pipe and the ports.
        await StopDesktopModeQuietlyAsync();

        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var code = await Task.Run(() => WindowsServiceControl.RunElevated(exe, $"--install --user \"{user}\"", TimeSpan.FromMinutes(3)));
        ShowResult(code, "ToastServiceInstalled");
        Connection.Reconnect();
    }

    /// <summary>
    /// Replaces a service of another WEBit version with the one next to this app:
    /// Windows service → re-register (elevated), desktop mode → stop the old process and start the new one.
    /// </summary>
    [RelayCommand]
    private async Task UpdateServiceAsync()
    {
        var exe = AppEnvironment.FindServiceExecutable();
        if (exe is null)
        {
            ShowToast(Loc.F("ToastServiceExeMissing", AppEnvironment.BaseDirectory), isError: true);
            return;
        }

        if (OperatingSystem.IsWindows() && (Connection.Hello?.IsWindowsService ?? WindowsServiceControl.IsInstalled()))
        {
            var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
            var code = await Task.Run(() => WindowsServiceControl.RunElevated(exe, $"--install --user \"{user}\"", TimeSpan.FromMinutes(3)));
            ShowResult(code, "ToastServiceUpdated");
            Connection.Reconnect();
            return;
        }

        // Old desktop mode of this user: end it (its job object ends Caddy, PHP, Mailpit), then start ours.
        // No automatic desktop start in between: we start it ourselves below.
        _autoDesktopTried = true;
        var stopped = await Task.Run(AppEnvironment.StopServiceProcesses);
        if (!stopped)
        {
            ShowToast(Loc.T("ToastServiceUpdateDenied"), isError: true);
            return;
        }

        await StartDesktopModeAsync();
    }

    [RelayCommand]
    private async Task UninstallServiceAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var exe = AppEnvironment.FindServiceExecutable();
        if (exe is null)
        {
            ShowToast(Loc.F("ToastServiceExeMissing", AppEnvironment.BaseDirectory), isError: true);
            return;
        }

        var confirm = await Host.ConfirmAsync(Loc.T("DlgUninstallServiceTitle"), Loc.T("DlgUninstallServiceText"), Loc.T("BtnUninstallService"), danger: true);
        if (!confirm.Confirmed)
        {
            return;
        }

        var code = await Task.Run(() => WindowsServiceControl.RunElevated(exe, "--uninstall", TimeSpan.FromMinutes(2)));
        ShowResult(code, "ToastServiceUninstalled");
        Connection.Reconnect();
    }

    [RelayCommand]
    private async Task StartServiceAsync()
    {
        var code = await Task.Run(WindowsServiceControl.StartElevated);
        ShowResult(code, "ToastServiceStarted");
        Connection.Reconnect();
    }

    [RelayCommand]
    private async Task StopServiceAsync()
    {
        var code = await Task.Run(WindowsServiceControl.StopElevated);
        ShowResult(code, "ToastServiceStopped");
    }

    [RelayCommand]
    private async Task RestartServiceAsync()
    {
        var code = await Task.Run(() => WindowsServiceControl.RunElevated("cmd.exe", $"/c sc stop {WindowsServiceControl.ServiceName} & timeout /t 3 /nobreak >nul & sc start {WindowsServiceControl.ServiceName}"));
        ShowResult(code, "ToastServiceRestarted");
        Connection.Reconnect();
    }

    /// <summary>Removes WEBit completely: service, group, autostart, optionally data and the portable folder.</summary>
    [RelayCommand]
    private async Task UninstallWebitAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Installed with setup: its uninstaller does everything.
        var setupUninstaller = AppEnvironment.FindSetupUninstaller();
        if (setupUninstaller is not null)
        {
            var ok = await Host.ConfirmAsync(Loc.T("DlgUninstallAppTitle"), Loc.T("DlgUninstallAppSetupText"), Loc.T("BtnUninstallApp"), danger: true);
            if (ok.Confirmed && AppEnvironment.Launch(setupUninstaller))
            {
                Host.ExitApplication?.Invoke();
            }

            return;
        }

        var exe = AppEnvironment.FindServiceExecutable();
        if (exe is null)
        {
            ShowToast(Loc.F("ToastServiceExeMissing", AppEnvironment.BaseDirectory), isError: true);
            return;
        }

        var portable = AppEnvironment.IsPortable;
        var result = await Host.ConfirmAsync(
            Loc.T("DlgUninstallAppTitle"),
            Loc.T("DlgUninstallAppText"),
            Loc.T("BtnUninstallApp"),
            danger: true,
            optionText: portable ? Loc.T("ChkDeleteProgramFolder") : Loc.T("ChkDeleteData"),
            optionDefault: true);
        if (!result.Confirmed)
        {
            return;
        }

        await StopDesktopModeQuietlyAsync();
        var purge = result.OptionChecked;
        var code = await Task.Run(() => WindowsServiceControl.RunElevated(exe, purge ? "--uninstall --purge" : "--uninstall", TimeSpan.FromMinutes(3)));
        if (code != 0)
        {
            ShowResult(code, "ToastAppUninstalled");
            return;
        }

        try
        {
            Autostart.Set(false, AppEnvironment.ExecutablePath);
        }
        catch
        {
            // Already removed by the service setup.
        }

        if (portable && purge)
        {
            AppEnvironment.ScheduleFolderRemoval();
        }

        Host.ExitApplication?.Invoke();
    }

    private async Task StopDesktopModeQuietlyAsync()
    {
        _autoDesktopTried = true;
        if (!IsConnected || !IsDesktopMode)
        {
            return;
        }

        try
        {
            await Connection.RequestAsync(Methods.Shutdown);
            await Task.Delay(1500);
        }
        catch
        {
            // Already gone.
        }
    }

    private void ShowResult(int code, string successKey) =>
        ShowToast(
            code switch
            {
                0 => Loc.T(successKey),
                -2 => Loc.T("ToastCancelled"),
                _ => Loc.F("ToastServiceActionFailed", code)
            },
            isError: code is not (0 or -2));

    // ------------------------------------------------------------------ Toasts

    [ObservableProperty]
    private string? _toast;

    [ObservableProperty]
    private bool _toastIsError;

    public bool HasToast => !string.IsNullOrEmpty(Toast);

    partial void OnToastChanged(string? value) => OnPropertyChanged(nameof(HasToast));

    public void ShowToast(string message, bool isError = false, bool force = false)
    {
        // Settings → Notifications: off, errors only, durations (force: test button).
        if (!force && (!Ui.NotificationsEnabled || (Ui.NotificationsErrorsOnly && !isError)))
        {
            return;
        }

        ToastIsError = isError;
        Toast = message;
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(isError ? Ui.ErrorToastSeconds : Ui.ToastSeconds, 1, 120));
        _toastTimer.Start();
    }

    public void ShowError(Exception ex) => ShowToast(Loc.F("ToastError", ex.Message), isError: true);

    [RelayCommand]
    private void CloseToast()
    {
        _toastTimer.Stop();
        Toast = null;
    }

    public async Task CopyAsync(string? text, string? toastKey = "ToastCopied")
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        await Host.CopyAsync(text);
        if (toastKey is not null)
        {
            ShowToast(Loc.T(toastKey));
        }
    }

    public string MessageTemplate =>
        string.IsNullOrWhiteSpace(Ui.MessageTemplate) ? Loc.T("TplMessage") : Ui.MessageTemplate!;
}
