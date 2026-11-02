using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WEBit.App.Localization;
using WEBit.App.Services;
using WEBit.Contracts;
using WEBit.Core.Models;
using WEBit.Platform.Windows;

namespace WEBit.App.ViewModels;

/// <summary>Settings page: language, ports, paths, message template, service.</summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    private WebitSettings? _loaded;
    private bool _suppressLanguage;
    private DateTime _lastServiceQuery = DateTime.MinValue;

    public SettingsViewModel(MainViewModel main) : base(main)
    {
        _messageTemplate = main.MessageTemplate;
        _trayAutostart = OperatingSystem.IsWindows() && Autostart.IsEnabled();
        _autoDesktopMode = main.Ui.AutoDesktopModeEffective;
        _notificationsEnabled = main.Ui.NotificationsEnabled;
        _notificationsErrorsOnly = main.Ui.NotificationsErrorsOnly;
        _toastSeconds = (decimal)main.Ui.ToastSeconds;
        _errorToastSeconds = (decimal)main.Ui.ErrorToastSeconds;
        _logRefreshSeconds = main.Ui.LogRefreshSeconds;
        LogLevels = ["Debug", "Information", "Warning", "Error"];
        RefreshIntervals = [1, 2, 3, 5, 10, 30, 60];
        main.StatusUpdated += SyncDesktopModeSwitch;
        Transfer = new TransferViewModel(main);
    }

    /// <summary>Export &amp; import card.</summary>
    public TransferViewModel Transfer { get; }

    // ------------------------------------------------------------------ HTTPS (local CA)

    [ObservableProperty]
    private bool _httpsEnabled;

    [ObservableProperty]
    private string _certText = "–";

    [ObservableProperty]
    private bool _certAvailable;

    [ObservableProperty]
    private bool _certTrusted;

    private CertificateInfo? _cert;

    public bool CanTrust => CertAvailable && !CertTrusted && OperatingSystem.IsWindows();

    public bool CanUntrust => CertAvailable && CertTrusted && OperatingSystem.IsWindows();

    partial void OnCertAvailableChanged(bool value) => NotifyCert();

    partial void OnCertTrustedChanged(bool value) => NotifyCert();

    private void NotifyCert()
    {
        OnPropertyChanged(nameof(CanTrust));
        OnPropertyChanged(nameof(CanUntrust));
    }

    [RelayCommand]
    private async Task RefreshCertAsync()
    {
        try
        {
            _cert = await Connection.RequestAsync<CertificateInfo>(Methods.GetRootCertificate);
            CertAvailable = _cert?.Available == true;
            CertTrusted = CertAvailable && OperatingSystem.IsWindows() && IsTrustedOnThisPc(_cert!.Thumbprint);
            CertText = CertAvailable
                ? Loc.F("CertInfo", _cert!.Subject ?? "", _cert.NotAfter?.ToLocalTime().ToString("d") ?? "–")
                : Loc.T(HttpsEnabled ? "CertPending" : "CertOff");
        }
        catch (Exception ex)
        {
            CertText = ex.Message;
        }
    }

    private static bool IsTrustedOnThisPc(string? thumbprint)
    {
        if (string.IsNullOrEmpty(thumbprint))
        {
            return false;
        }

        try
        {
            using var store = new System.Security.Cryptography.X509Certificates.X509Store(
                System.Security.Cryptography.X509Certificates.StoreName.Root, System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine);
            store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);
            return store.Certificates.Find(System.Security.Cryptography.X509Certificates.X509FindType.FindByThumbprint, thumbprint, validOnly: false).Count > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Adds the root to the trusted roots of this PC (certutil, administrator prompt).</summary>
    [RelayCommand]
    private Task TrustCertAsync() => RunCertutilAsync(trust: true);

    [RelayCommand]
    private Task UntrustCertAsync() => RunCertutilAsync(trust: false);

    private async Task RunCertutilAsync(bool trust)
    {
        if (_cert?.Pem is not { } pem || !OperatingSystem.IsWindows())
        {
            return;
        }

        if (trust)
        {
            var confirm = await Host.ConfirmAsync(Loc.T("DlgTrustCertTitle"), Loc.F("DlgTrustCertText", _cert.Subject ?? "", _cert.Thumbprint ?? ""),
                Loc.T("BtnTrustCert"), danger: false, confirmIcon: "IconShield");
            if (!confirm.Confirmed)
            {
                return;
            }
        }

        var file = Path.Combine(Path.GetTempPath(), $"webit-root-{Guid.NewGuid():N}.crt");
        try
        {
            await File.WriteAllTextAsync(file, pem);
            var certutil = Path.Combine(Environment.SystemDirectory, "certutil.exe");
            var code = await Task.Run(() => WindowsServiceControl.RunElevated(certutil,
                trust ? $"-addstore -f Root \"{file}\"" : $"-delstore Root {_cert.Thumbprint}", TimeSpan.FromMinutes(1)));
            if (code == 0)
            {
                Main.ShowToast(Loc.T(trust ? "ToastCertTrusted" : "ToastCertUntrusted"));
            }
            else if (code != -2)
            {
                Main.ShowToast(Loc.F("ToastServiceActionFailed", code), isError: true);
            }
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception)
            {
                // Temp folder.
            }
        }

        await RefreshCertAsync();
    }

    /// <summary>Root certificate as .crt for other devices (phones, other PCs).</summary>
    [RelayCommand]
    private async Task ExportCertAsync()
    {
        if (_cert?.Pem is { } pem && await Host.SaveTextAsync(Loc.T("CertExportTitle"), "webit-local-ca.crt", "crt", Loc.T("CertFileType"), pem))
        {
            Main.ShowToast(Loc.T("ToastCertExported"));
        }
    }

    public override string TitleKey => "NavSettings";

    public override bool RequiresService => false;

    public IReadOnlyList<LanguageOption> Languages => LanguageOption.All;

    public ObservableCollection<OptionItem<string>> BindAddressOptions { get; } = [];

    [ObservableProperty]
    private LanguageOption? _selectedLanguage;

    [ObservableProperty]
    private bool _trayAutostart;

    [ObservableProperty]
    private bool _autoDesktopMode;

    // ------------------------------------------------------------------ Notifications (per user, saved at once)

    [ObservableProperty]
    private bool _notificationsEnabled;

    [ObservableProperty]
    private bool _notificationsErrorsOnly;

    [ObservableProperty]
    private decimal? _toastSeconds;

    [ObservableProperty]
    private decimal? _errorToastSeconds;

    partial void OnNotificationsEnabledChanged(bool value) => SaveUi(ui => ui.NotificationsEnabled = value);

    partial void OnNotificationsErrorsOnlyChanged(bool value) => SaveUi(ui => ui.NotificationsErrorsOnly = value);

    partial void OnToastSecondsChanged(decimal? value) => SaveUi(ui => ui.ToastSeconds = (double)(value ?? 4));

    partial void OnErrorToastSecondsChanged(decimal? value) => SaveUi(ui => ui.ErrorToastSeconds = (double)(value ?? 8));

    [RelayCommand]
    private void TestNotification() => Main.ShowToast(Loc.T("ToastTestNotification"), force: true);

    private void SaveUi(Action<UiSettings> change)
    {
        change(Main.Ui);
        Main.Ui.Save();
    }

    // ------------------------------------------------------------------ Logs

    public IReadOnlyList<string> LogLevels { get; }

    public IReadOnlyList<int> RefreshIntervals { get; }

    [ObservableProperty]
    private bool _serviceLog = true;

    [ObservableProperty]
    private string _logLevel = "Information";

    [ObservableProperty]
    private bool _accessLogs = true;

    [ObservableProperty]
    private bool _moduleLogs = true;

    [ObservableProperty]
    private decimal? _logMaxSizeMb = 10;

    [ObservableProperty]
    private decimal? _logKeep = 5;

    /// <summary>Logs page auto refresh (per user, saved at once).</summary>
    [ObservableProperty]
    private int _logRefreshSeconds;

    partial void OnLogRefreshSecondsChanged(int value)
    {
        SaveUi(ui => ui.LogRefreshSeconds = value);
        Main.Logs.SetRefreshInterval(value);
    }

    [RelayCommand]
    private void OpenLogsFolder() => AppEnvironment.OpenFolder(Main.Status?.LogsDir);

    [RelayCommand]
    private async Task ClearLogsAsync()
    {
        var confirm = await Host.ConfirmAsync(Loc.T("DlgClearLogsTitle"), Loc.T("DlgClearLogsText"), Loc.T("BtnClearLogs"), danger: true);
        if (!confirm.Confirmed)
        {
            return;
        }

        try
        {
            var count = await Connection.RequestAsync<int>(Methods.ClearLogs);
            Main.ShowToast(Loc.F("ToastLogsCleared", count));
        }
        catch (Exception ex)
        {
            Main.ShowError(ex);
        }
    }

    // ------------------------------------------------------------------ Desktop mode switch

    private bool _syncingDesktop;

    /// <summary>On = the service runs in desktop mode for this user.</summary>
    [ObservableProperty]
    private bool _desktopModeSwitch;

    /// <summary>Switchable unless the Windows service is installed or connected (or the switch is busy).</summary>
    public bool CanSwitchDesktopMode =>
        !_desktopBusy && !IsServiceInstalled
        && (Main.ConnectionState == WEBit.App.Services.ConnectionState.Disconnected || Main.IsDesktopMode);

    private bool _desktopBusy;

    private void SyncDesktopModeSwitch()
    {
        OnPropertyChanged(nameof(CanStartService));
        OnPropertyChanged(nameof(CanStopService));

        if (_desktopBusy)
        {
            // Keep the requested position until the switch action is done.
            return;
        }

        _syncingDesktop = true;
        DesktopModeSwitch = Main.IsDesktopMode;
        _syncingDesktop = false;
        OnPropertyChanged(nameof(CanSwitchDesktopMode));
    }

    partial void OnDesktopModeSwitchChanged(bool value)
    {
        if (_syncingDesktop || value == Main.IsDesktopMode)
        {
            return;
        }

        _ = SwitchDesktopModeAsync(value);
    }

    private async Task SwitchDesktopModeAsync(bool on)
    {
        _desktopBusy = true;
        OnPropertyChanged(nameof(CanSwitchDesktopMode));
        try
        {
            if (on)
            {
                await Main.StartDesktopModeCommand.ExecuteAsync(null);
            }
            else
            {
                await Main.StopDesktopModeCommand.ExecuteAsync(null);
            }
        }
        finally
        {
            // Give the connection a moment to follow (desktop mode starts / stops asynchronously).
            for (var i = 0; i < 20 && Main.IsDesktopMode != on; i++)
            {
                await Task.Delay(250);
            }

            _desktopBusy = false;
            SyncDesktopModeSwitch();
        }
    }

    partial void OnAutoDesktopModeChanged(bool value)
    {
        Main.Ui.AutoDesktopMode = value;
        Main.Ui.Save();
    }

    [ObservableProperty]
    private string _messageTemplate;

    [ObservableProperty]
    private decimal? _sitePortStart;

    [ObservableProperty]
    private decimal? _sitePortEnd;

    [ObservableProperty]
    private decimal? _internalPortStart;

    [ObservableProperty]
    private decimal? _internalPortEnd;

    [ObservableProperty]
    private decimal? _engineAdminPort;

    [ObservableProperty]
    private decimal? _mailpitSmtpPort;

    [ObservableProperty]
    private decimal? _mailpitUiPort;

    [ObservableProperty]
    private decimal? _phpFastCgiPort;

    [ObservableProperty]
    private decimal? _mariaDbPort;

    /// <summary>MariaDB on all addresses; off = 127.0.0.1 only.</summary>
    [ObservableProperty]
    private bool _mariaDbNetworkAccess;

    /// <summary>Adminer on the default network address; off = 127.0.0.1 only.</summary>
    [ObservableProperty]
    private bool _adminerNetworkAccess;

    // ------------------------------------------------------------------ MariaDB backups

    [ObservableProperty]
    private bool _dbBackupEnabled;

    [ObservableProperty]
    private decimal? _dbBackupInterval = 24;

    [ObservableProperty]
    private decimal? _dbBackupKeep = 7;

    [ObservableProperty]
    private bool _autoSites = true;

    // ------------------------------------------------------------------ PHP

    public ObservableCollection<PhpExtensionItem> PhpExtensions { get; } = [];

    public bool HasPhpExtensions => PhpExtensions.Count > 0;

    private List<string> _phpDefaults = [];

    [ObservableProperty]
    private string _phpMemoryLimit = "512M";

    [ObservableProperty]
    private string _phpUploadMax = "128M";

    [ObservableProperty]
    private string _phpPostMax = "128M";

    [ObservableProperty]
    private decimal? _phpMaxExecution = 120;

    [ObservableProperty]
    private bool _phpDisplayErrors = true;

    [ObservableProperty]
    private bool _phpOpcache = true;

    [ObservableProperty]
    private decimal? _phpXdebugPort = 9003;

    [ObservableProperty]
    private string? _phpVersionText;

    /// <summary>Back to the extensions WEBit loads by default.</summary>
    [RelayCommand]
    private void ResetPhpExtensions()
    {
        foreach (var item in PhpExtensions)
        {
            item.IsOn = _phpDefaults.Contains(item.Name);
        }
    }

    private async Task LoadPhpAsync(PhpSettings php)
    {
        PhpMemoryLimit = php.MemoryLimit;
        PhpUploadMax = php.UploadMaxFilesize;
        PhpPostMax = php.PostMaxSize;
        PhpMaxExecution = php.MaxExecutionTime;
        PhpDisplayErrors = php.DisplayErrors;
        PhpOpcache = php.Opcache;
        PhpXdebugPort = php.XdebugPort;

        PhpExtensions.Clear();
        var info = await Connection.RequestAsync<PhpInfo>(Methods.GetPhpInfo);
        PhpVersionText = info?.Version is { } v ? "PHP " + v : Loc.T("StateNotInstalled");
        _phpDefaults = info?.Defaults ?? [];
        foreach (var name in info?.Available ?? [])
        {
            PhpExtensions.Add(new PhpExtensionItem(name, php.EffectiveExtensions.Contains(name, StringComparer.OrdinalIgnoreCase)));
        }

        OnPropertyChanged(nameof(HasPhpExtensions));
    }

    private PhpSettings BuildPhp() => new()
    {
        // Without a PHP installation the list is unknown: keep the stored choice.
        Extensions = PhpExtensions.Count > 0 ? PhpExtensions.Where(e => e.IsOn).Select(e => e.Name).ToList() : _loaded?.Php.Extensions,
        MemoryLimit = PhpMemoryLimit,
        UploadMaxFilesize = PhpUploadMax,
        PostMaxSize = PhpPostMax,
        MaxExecutionTime = (int)(PhpMaxExecution ?? 120),
        DisplayErrors = PhpDisplayErrors,
        Opcache = PhpOpcache,
        XdebugPort = (int)(PhpXdebugPort ?? 9003)
    };

    [ObservableProperty]
    private OptionItem<string>? _selectedBindAddress;

    [ObservableProperty]
    private string? _dnsSuffix;

    [ObservableProperty]
    private string? _wwwRoot;

    [ObservableProperty]
    private string? _enginePath;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _serviceStateText = "–";

    [ObservableProperty]
    private bool _isServiceInstalled;

    partial void OnIsServiceInstalledChanged(bool value) => OnPropertyChanged(nameof(CanSwitchDesktopMode));

    public bool ServiceSettingsAvailable => Connection.IsConnected && _loaded is not null;

    public bool IsWindows => OperatingSystem.IsWindows();

    partial void OnSelectedLanguageChanged(LanguageOption? value)
    {
        if (_suppressLanguage || value is null)
        {
            return;
        }

        Main.SetLanguage(value.Code);
        if (string.IsNullOrWhiteSpace(Main.Ui.MessageTemplate))
        {
            MessageTemplate = Main.MessageTemplate;
        }
    }

    partial void OnTrayAutostartChanged(bool value)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            Autostart.Set(value, AppEnvironment.ExecutablePath);
        }
        catch (Exception ex)
        {
            Main.ShowError(ex);
        }
    }

    public override void OnActivated()
    {
        SyncLanguage();
        RefreshServiceState(force: true);
        Transfer.Refresh();
        _ = LoadAsync();
    }

    public override void OnConnected()
    {
        if (ReferenceEquals(Main.CurrentPage, this))
        {
            _ = LoadAsync();
        }
    }

    public override void OnStatus(ServiceStatus status)
    {
        if (ReferenceEquals(Main.CurrentPage, this))
        {
            RefreshServiceState();
        }

        OnPropertyChanged(nameof(ServiceSettingsAvailable));
    }

    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        SyncLanguage();
    }

    /// <summary>Selection follows language changes made from the tray.</summary>
    private void SyncLanguage()
    {
        _suppressLanguage = true;
        SelectedLanguage = LanguageOption.Current;
        _suppressLanguage = false;
    }

    private void RefreshServiceState(bool force = false)
    {
        if (!OperatingSystem.IsWindows())
        {
            ServiceStateText = "–";
            return;
        }

        if (!force && (DateTime.UtcNow - _lastServiceQuery).TotalSeconds < 5)
        {
            return;
        }

        _lastServiceQuery = DateTime.UtcNow;
        _ = Task.Run(() =>
        {
            var state = WindowsServiceControl.Query();
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                IsServiceInstalled = state != WindowsServiceState.NotInstalled;
                ServiceStateBrush = state switch
                {
                    WindowsServiceState.Running => Res.Brush("BrushSecondary"),
                    WindowsServiceState.StartPending or WindowsServiceState.StopPending => Res.Brush("BrushAccentHover"),
                    WindowsServiceState.NotInstalled when Main.IsDesktopMode => Res.Brush("BrushSecondary"),
                    WindowsServiceState.Stopped => Res.Brush("BrushDanger"),
                    WindowsServiceState.NotInstalled => Res.Brush("BrushWarning"),
                    _ => Res.Brush("BrushTextDisabled")
                };
                OnPropertyChanged(nameof(CanStartService));
                OnPropertyChanged(nameof(CanStopService));
                ServiceStateText = state switch
                {
                    WindowsServiceState.NotInstalled => Main.IsDesktopMode ? Loc.T("SvcDesktopMode") : Loc.T("SvcNotInstalled"),
                    WindowsServiceState.Running => Loc.T("SvcRunning"),
                    WindowsServiceState.Stopped => Loc.T("SvcStopped"),
                    WindowsServiceState.StartPending => Loc.T("SvcStarting"),
                    WindowsServiceState.StopPending => Loc.T("SvcStopping"),
                    _ => Loc.T("SvcUnknown")
                };
            });
        });
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        MessageTemplate = Main.MessageTemplate;
        if (!Connection.IsConnected)
        {
            OnPropertyChanged(nameof(ServiceSettingsAvailable));
            return;
        }

        try
        {
            _loaded = await Connection.RequestAsync<WebitSettings>(Methods.GetSettings);
            var info = await Connection.RequestAsync<NetworkInfo>(Methods.GetNetworkInfo);
            if (_loaded is null)
            {
                return;
            }

            SitePortStart = _loaded.SitePortStart;
            SitePortEnd = _loaded.SitePortEnd;
            InternalPortStart = _loaded.InternalPortStart;
            InternalPortEnd = _loaded.InternalPortEnd;
            EngineAdminPort = _loaded.EngineAdminPort;
            MailpitSmtpPort = _loaded.MailpitSmtpPort;
            MailpitUiPort = _loaded.MailpitUiPort;
            PhpFastCgiPort = _loaded.PhpFastCgiPort;
            MariaDbPort = _loaded.MariaDbPort;
            MariaDbNetworkAccess = _loaded.MariaDbNetworkAccess;
            AdminerNetworkAccess = _loaded.AdminerNetworkAccess;
            DbBackupEnabled = _loaded.DbBackup.Enabled;
            DbBackupInterval = _loaded.DbBackup.IntervalHours;
            DbBackupKeep = _loaded.DbBackup.Keep;
            AutoSites = _loaded.AutoSites;
            DnsSuffix = _loaded.DnsSuffix;
            WwwRoot = _loaded.WwwRoot;
            EnginePath = _loaded.EnginePath;
            ServiceLog = _loaded.Logging.ServiceLog;
            LogLevel = _loaded.Logging.Level;
            AccessLogs = _loaded.Logging.AccessLogs;
            ModuleLogs = _loaded.Logging.ModuleLogs;
            LogMaxSizeMb = _loaded.Logging.MaxSizeMb;
            LogKeep = _loaded.Logging.Keep;
            HttpsEnabled = _loaded.HttpsEnabled;
            await LoadPhpAsync(_loaded.Php);
            await RefreshCertAsync();

            BindAddressOptions.Clear();
            BindAddressOptions.Add(new OptionItem<string>("", Loc.T("BindAddressAuto")));
            foreach (var address in info?.Addresses ?? [])
            {
                BindAddressOptions.Add(new OptionItem<string>(address.Address, $"{address.Address}  ({address.InterfaceName})"));
            }

            if (!string.IsNullOrEmpty(_loaded.DefaultBindAddress) && BindAddressOptions.All(o => o.Value != _loaded.DefaultBindAddress))
            {
                BindAddressOptions.Add(new OptionItem<string>(_loaded.DefaultBindAddress, _loaded.DefaultBindAddress));
            }

            SelectedBindAddress = BindAddressOptions.FirstOrDefault(o => o.Value == (_loaded.DefaultBindAddress ?? "")) ?? BindAddressOptions[0];
        }
        catch (Exception ex)
        {
            Main.ShowError(ex);
        }

        OnPropertyChanged(nameof(ServiceSettingsAvailable));
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        Main.Ui.MessageTemplate = string.Equals(MessageTemplate?.Trim(), Loc.T("TplMessage").Trim(), StringComparison.Ordinal) ? null : MessageTemplate;
        Main.Ui.Save();

        if (_loaded is null || !Connection.IsConnected)
        {
            Main.ShowToast(Loc.T("ToastSettingsSavedLocal"));
            return;
        }

        var settings = new WebitSettings
        {
            SitePortStart = (int)(SitePortStart ?? 47100),
            SitePortEnd = (int)(SitePortEnd ?? 47199),
            InternalPortStart = (int)(InternalPortStart ?? 47200),
            InternalPortEnd = (int)(InternalPortEnd ?? 47289),
            EngineAdminPort = (int)(EngineAdminPort ?? 47299),
            MailpitSmtpPort = (int)(MailpitSmtpPort ?? 47201),
            MailpitUiPort = (int)(MailpitUiPort ?? 47202),
            PhpFastCgiPort = (int)(PhpFastCgiPort ?? 47203),
            MariaDbPort = (int)(MariaDbPort ?? 47204),
            MariaDbNetworkAccess = MariaDbNetworkAccess,
            AutoSites = AutoSites,
            DefaultBindAddress = SelectedBindAddress is null
                ? _loaded.DefaultBindAddress
                : string.IsNullOrEmpty(SelectedBindAddress.Value) ? null : SelectedBindAddress.Value,
            DnsSuffix = DnsSuffix,
            WwwRoot = WwwRoot,
            EnginePath = EnginePath,
            AdminerPort = _loaded.AdminerPort,
            AdminerNetworkAccess = AdminerNetworkAccess,
            DbBackup = new DbBackupSettings
            {
                Enabled = DbBackupEnabled,
                IntervalHours = (int)(DbBackupInterval ?? 24),
                Keep = (int)(DbBackupKeep ?? 7)
            },
            Php = BuildPhp(),
            HttpsEnabled = HttpsEnabled,
            Logging = new LogSettings
            {
                ServiceLog = ServiceLog,
                Level = LogLevel,
                AccessLogs = AccessLogs,
                ModuleLogs = ModuleLogs,
                MaxSizeMb = (int)(LogMaxSizeMb ?? 10),
                Keep = (int)(LogKeep ?? 5)
            }
        };

        IsBusy = true;
        try
        {
            _loaded = await Connection.RequestAsync<WebitSettings>(Methods.SaveSettings, settings, TimeSpan.FromMinutes(2));
            Main.ShowToast(Loc.T("ToastSettingsSaved"));
        }
        catch (Exception ex)
        {
            Main.ShowError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ResetTemplate()
    {
        Main.Ui.MessageTemplate = null;
        Main.Ui.Save();
        MessageTemplate = Loc.T("TplMessage");
    }

    [RelayCommand]
    private async Task BrowseWwwAsync()
    {
        var path = await Host.PickFolderAsync(Loc.T("PickWwwTitle"), WwwRoot ?? Main.Status?.WwwRoot);
        if (!string.IsNullOrEmpty(path))
        {
            WwwRoot = path;
        }
    }

    [RelayCommand]
    private async Task BrowseEngineAsync()
    {
        var path = await Host.PickFileAsync(Loc.T("PickEngineTitle"), "caddy", ["WEBit-Caddy.exe", "caddy.exe", "caddy"]);
        if (!string.IsNullOrEmpty(path))
        {
            EnginePath = path;
        }
    }

    [RelayCommand]
    private async Task StartServiceAsync()
    {
        // Windows service installed → start it (UAC), otherwise start desktop mode.
        // Asked directly (registry), the cached state may not be loaded yet.
        if (OperatingSystem.IsWindows() && WindowsServiceControl.IsInstalled())
        {
            await Main.StartServiceCommand.ExecuteAsync(null);
        }
        else
        {
            await Main.StartDesktopModeCommand.ExecuteAsync(null);
        }

        await Task.Delay(1500);
        RefreshServiceState(force: true);
    }

    [RelayCommand]
    private async Task StopServiceAsync()
    {
        if (OperatingSystem.IsWindows() && WindowsServiceControl.IsInstalled() && Main.Status is not { IsWindowsService: false })
        {
            await Main.StopServiceCommand.ExecuteAsync(null);
        }
        else
        {
            await Main.StopDesktopModeCommand.ExecuteAsync(null);
        }

        await Task.Delay(1000);
        RefreshServiceState(force: true);
    }

    public bool CanStartService => !Main.IsConnected;

    public bool CanStopService => Main.IsConnected;

    /// <summary>Status pill color: running green, starting blue, stopped/not installed red.</summary>
    [ObservableProperty]
    private Avalonia.Media.IBrush _serviceStateBrush = Res.Brush("BrushTextDisabled");

    public Avalonia.Media.IBrush ServiceStateBackground => ServiceStateBrush is Avalonia.Media.ISolidColorBrush solid
        ? new Avalonia.Media.SolidColorBrush(solid.Color, 0.18)
        : Res.Brush("BrushSurfaceRaised");

    partial void OnServiceStateBrushChanged(Avalonia.Media.IBrush value) => OnPropertyChanged(nameof(ServiceStateBackground));

    [RelayCommand]
    private void OpenAppDataFolder() => AppEnvironment.OpenFolder(Path.GetDirectoryName(UiSettings.FilePath));
}

/// <summary>PHP extension with on/off switch (Settings → PHP).</summary>
public sealed partial class PhpExtensionItem(string name, bool isOn) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    private bool _isOn = isOn;
}
