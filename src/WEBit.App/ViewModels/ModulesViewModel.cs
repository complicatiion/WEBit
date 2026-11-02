using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WEBit.App.Localization;
using WEBit.App.Services;
using WEBit.Contracts;
using WEBit.Core.Models;

namespace WEBit.App.ViewModels;

/// <summary>One module card (web server, Mailpit, planned runtimes) with on/off switch.</summary>
public sealed partial class ModuleCardViewModel : ObservableObject
{
    private readonly ModulesViewModel _owner;
    private bool _syncing;

    public ModuleCardViewModel(ModulesViewModel owner, ModuleStatus status)
    {
        _owner = owner;
        _status = status;
        _isOn = status.Enabled;
    }

    [ObservableProperty]
    private ModuleStatus _status;

    /// <summary>Switch state; user changes are sent to the service.</summary>
    [ObservableProperty]
    private bool _isOn;

    [ObservableProperty]
    private bool _isBusy;

    partial void OnStatusChanged(ModuleStatus value)
    {
        _syncing = true;
        IsOn = value.Enabled;
        _syncing = false;
        OnPropertyChanged(string.Empty);
    }

    partial void OnIsOnChanged(bool value)
    {
        if (!_syncing)
        {
            _ = _owner.ToggleAsync(this, value);
        }
    }

    public string Id => Status.Id;

    public string Name => Status.Name;

    public string Description => Loc.T("Mod_" + Status.Id);

    public bool IsPlanned => Status.State == ModuleState.Planned;

    public bool IsAvailable => !IsPlanned;

    public bool IsOptional => Status.Id is ModuleIds.MariaDb or ModuleIds.Node or ModuleIds.Python
        or ModuleIds.Composer or ModuleIds.WpCli or ModuleIds.Adminer or ModuleIds.Xdebug;

    /// <summary>Tools without a process (Composer, WP-CLI, Xdebug) have no switch.</summary>
    public bool IsTool => Status.Id is ModuleIds.Composer or ModuleIds.WpCli or ModuleIds.Xdebug;

    public bool ShowToggle => !IsTool;

    public bool CanToggle => Status.CanToggle && !IsBusy;

    public Geometry? Icon => Res.Icon(Status.Id switch
    {
        ModuleIds.WebServer => "IconServer",
        ModuleIds.Mailpit => "IconEmail",
        ModuleIds.MariaDb => "IconDatabase",
        ModuleIds.Node => "IconNodejs",
        ModuleIds.Python => "IconPython",
        ModuleIds.Composer => "IconPackage",
        ModuleIds.WpCli => "IconConsole",
        ModuleIds.Adminer => "IconDatabase",
        ModuleIds.Xdebug => "IconBug",
        _ when ModuleIds.IsPhpVersion(Status.Id) => "IconWeb",
        ModuleIds.Php => "IconWeb",
        _ => "IconConsole"
    });

    public string StateText => Status.State switch
    {
        ModuleState.Running => Loc.T("StateRunning"),
        ModuleState.Ready => Loc.T("StateReady"),
        ModuleState.Starting => Loc.T("StateStarting"),
        ModuleState.Error => Loc.T("StateError"),
        ModuleState.Disabled => Loc.T("StateDisabled"),
        ModuleState.NotInstalled => Loc.T("StateNotInstalled"),
        ModuleState.Planned => Loc.F("StatePlanned", Status.PlannedFor),
        _ => Loc.T("StateStopped")
    };

    public IBrush StateBrush => Status.State switch
    {
        ModuleState.Running or ModuleState.Ready => Res.Brush("BrushSecondary"),
        ModuleState.Starting => Res.Brush("BrushAccentHover"),
        ModuleState.Error or ModuleState.NotInstalled => Res.Brush("BrushDanger"),
        _ => Res.Brush("BrushTextDisabled")
    };

    public string Version => string.IsNullOrEmpty(Status.Version) ? "–" : Status.Version!;

    public string Endpoint => Status.Endpoint ?? "–";

    public string License => Status.License;

    public string? Message => Status.Message is null ? null : Loc.Message(Status.Message);

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    public bool HasUrl => !string.IsNullOrEmpty(Status.Url) && Status.State == ModuleState.Running;

    public bool HasPath => !string.IsNullOrEmpty(Status.ExecutablePath);

    public bool CanRestart => !IsTool && Status.Enabled && Status.State is ModuleState.Running or ModuleState.Error or ModuleState.Ready;

    /// <summary>MariaDB card: Adminer is running.</summary>
    public bool ShowAdminer => ShowDatabaseActions && _owner.AdminerUrl is not null;

    /// <summary>MariaDB is running: password, databases and backup.</summary>
    public bool ShowDatabaseActions => Status.Id == ModuleIds.MariaDb && Status.State == ModuleState.Running;

    // ------------------------------------------------------------------ Download

    public bool IsDownloading => Status.Download?.State == ModuleDownloadState.Running;

    /// <summary>Missing module: the main action of the card is "Download".</summary>
    public bool ShowDownload => Status.CanDownload && !IsDownloading && Status.State == ModuleState.NotInstalled;

    /// <summary>Installed module: download the latest version again.</summary>
    public bool ShowUpdate => Status.CanDownload && !IsDownloading && Status.State != ModuleState.NotInstalled;

    public double DownloadPercent => Status.Download?.Percent ?? 0;

    public bool DownloadIndeterminate => IsDownloading && Status.Download?.Percent is null;

    public string? DownloadText => Status.Download is { } d
        ? (d.Percent is { } p && d.State == ModuleDownloadState.Running ? $"{Loc.Message(d.Message!)}  {p} %" : d.Message is null ? null : Loc.Message(d.Message))
        : null;

    public bool HasDownloadText => IsDownloading && !string.IsNullOrEmpty(DownloadText);

    public bool DownloadFailed => Status.Download?.State == ModuleDownloadState.Failed;

    public string? DownloadError => DownloadFailed ? DownloadText : null;

    /// <summary>PHP: Visual C++ runtime missing, installer downloaded.</summary>
    public bool NeedsVcRuntime => !string.IsNullOrEmpty(Status.VcRedistInstaller);

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanToggle));

    public void RefreshTexts() => OnPropertyChanged(string.Empty);

    public void ResetSwitch()
    {
        _syncing = true;
        IsOn = Status.Enabled;
        _syncing = false;
    }

    [RelayCommand]
    private void Open() => AppEnvironment.OpenUrl(Status.Url);

    [RelayCommand]
    private Task RestartAsync() => _owner.RestartAsync(this);

    [RelayCommand]
    private void OpenFolder()
    {
        if (Status.ExecutablePath is { } path)
        {
            AppEnvironment.OpenFolder(Path.GetDirectoryName(path));
        }
    }

    [RelayCommand]
    private void ShowLog() => _owner.ShowLog(this);

    [RelayCommand]
    private Task DownloadAsync() => _owner.DownloadAsync(this);

    [RelayCommand]
    private Task InstallVcRuntimeAsync() => _owner.InstallVcRuntimeAsync(this);

    [RelayCommand]
    private Task CopyPasswordAsync() => _owner.CopyDbPasswordAsync();

    [RelayCommand]
    private Task CreateDatabaseAsync() => _owner.CreateDatabaseAsync();

    [RelayCommand]
    private Task BackupAsync() => _owner.BackupDatabaseAsync();

    [RelayCommand]
    private Task OpenAdminerAsync() => _owner.OpenAdminerAsync(null);
}

/// <summary>Modules page: switch services on and off.</summary>
public sealed partial class ModulesViewModel(MainViewModel main) : PageViewModel(main)
{
    public override string TitleKey => "NavModules";

    public ObservableCollection<ModuleCardViewModel> Cards { get; } = [];

    /// <summary>Installed or installable modules (dashboard, tray).</summary>
    public IEnumerable<ModuleCardViewModel> Active =>
        Cards.Where(c => c.IsAvailable && !(c.IsOptional && c.Status.State == ModuleState.NotInstalled));

    /// <summary>Switched-on modules without files (optional runtimes are downloaded one by one).</summary>
    public bool HasMissing => Cards.Any(c => c.ShowDownload && !c.IsOptional);

    /// <summary>Set by a PHP download: offer the VC++ runtime once it turns out to be missing.</summary>
    private bool _offerVcRuntime;

    public override void OnStatus(ServiceStatus status)
    {
        var incoming = status.Modules;
        for (var i = Cards.Count - 1; i >= 0; i--)
        {
            if (incoming.All(m => m.Id != Cards[i].Id))
            {
                Cards.RemoveAt(i);
            }
        }

        for (var i = 0; i < incoming.Count; i++)
        {
            var existing = Cards.FirstOrDefault(c => c.Id == incoming[i].Id);
            if (existing is null)
            {
                Cards.Insert(Math.Min(i, Cards.Count), new ModuleCardViewModel(this, incoming[i]));
            }
            else
            {
                var before = existing.Status.Download?.State;
                existing.Status = incoming[i];
                var after = incoming[i].Download;
                if (before == ModuleDownloadState.Running && after is not null && after.State != ModuleDownloadState.Running)
                {
                    Main.ShowToast(
                        after.State == ModuleDownloadState.Completed
                            ? Loc.F("ToastDownloadCompleted", existing.Name, after.Version ?? "")
                            : Loc.F("ToastDownloadFailed", existing.Name, after.Message is null ? "" : Loc.Message(after.Message)),
                        isError: after.State == ModuleDownloadState.Failed);
                }
            }
        }

        OnPropertyChanged(nameof(Active));
        OnPropertyChanged(nameof(HasMissing));

        if (_offerVcRuntime && Cards.FirstOrDefault(c => c.Id == ModuleIds.Php) is { NeedsVcRuntime: true } php)
        {
            _offerVcRuntime = false;
            _ = InstallVcRuntimeAsync(php);
        }
    }

    public async Task DownloadAsync(ModuleCardViewModel card)
    {
        try
        {
            await Connection.RequestAsync(Methods.DownloadModule, new IdRequest { Id = card.Id });
            _offerVcRuntime |= card.Id == ModuleIds.Php;
            Main.ShowToast(Loc.F("ToastDownloadStarted", card.Name));
        }
        catch (Exception ex)
        {
            Main.ShowError(ex);
        }
    }

    /// <summary>Downloads every missing module with one click.</summary>
    [RelayCommand]
    private async Task DownloadMissingAsync()
    {
        foreach (var card in Cards.Where(c => c.ShowDownload && !c.IsOptional).ToList())
        {
            await DownloadAsync(card);
        }
    }

    /// <summary>Runs the downloaded Visual C++ runtime installer with administrator rights, then restarts PHP.</summary>
    public Task InstallVcRuntimeAsync(ModuleCardViewModel card) => InstallVcRuntimeAsync(card.Status.VcRedistInstaller);

    /// <summary>Also used by the dependencies page.</summary>
    public async Task InstallVcRuntimeAsync(string? installer, bool uninstall = false)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(installer))
        {
            return;
        }

        var confirm = uninstall
            ? await Host.ConfirmAsync(Loc.T("DlgVcRuntimeRemoveTitle"), Loc.T("DlgVcRuntimeRemoveText"), Loc.T("BtnUninstall"), danger: true)
            : await Host.ConfirmAsync(Loc.T("DlgVcRuntimeTitle"), Loc.T("DlgVcRuntimeText"), Loc.T("BtnInstallVcRuntime"), danger: false);
        if (!confirm.Confirmed)
        {
            return;
        }

        // The data folder may be writable for other users: run a private copy, and only
        // when it carries a valid Microsoft signature.
        var code = await Task.Run(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "WEBit-vcredist-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                Directory.CreateDirectory(dir);
                var copy = Path.Combine(dir, Path.GetFileName(installer));
                File.Copy(installer, copy, overwrite: true);
                if (!WEBit.Platform.Windows.Authenticode.IsSignedBy(copy, "Microsoft Corporation"))
                {
                    return -3;
                }

                return WEBit.Platform.Windows.WindowsServiceControl.RunElevated(
                    copy, uninstall ? "/uninstall /passive /norestart" : "/install /passive /norestart", TimeSpan.FromMinutes(10));
            }
            catch (Exception)
            {
                return -1;
            }
            finally
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch
                {
                    // Temp folder; cleaned up by Windows.
                }
            }
        });

        // 0 = installed, 1638 = newer version present, 3010 = installed, restart pending, -2 = UAC cancelled.
        if (code is 0 or 1638 or 3010)
        {
            var present = File.Exists(Path.Combine(Environment.SystemDirectory, "vcruntime140_1.dll"));
            if (uninstall)
            {
                // The installer may exit 0 without removing a newer runtime: report what is really there.
                Main.ShowToast(present ? Loc.T("ToastVcRuntimeStillInstalled") : Loc.T("ToastVcRuntimeRemoved"), isError: present);
            }
            else
            {
                Main.ShowToast(Loc.T("ToastVcRuntimeInstalled"));
                if (Cards.FirstOrDefault(c => c.Id == ModuleIds.Php) is { } php && php.Status.Enabled)
                {
                    await RestartAsync(php);
                }
            }

            // Also without PHP restart: the dependencies page must show the runtime as installed.
            await Main.RefreshCommand.ExecuteAsync(null);
        }
        else if (code == -3)
        {
            Main.ShowToast(Loc.T("ToastVcRuntimeUnsigned"), isError: true);
        }
        else if (code != -2)
        {
            Main.ShowToast(Loc.F("ToastVcRuntimeFailed", code), isError: true);
        }
    }

    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        foreach (var card in Cards)
        {
            card.RefreshTexts();
        }
    }

    public async Task ToggleAsync(ModuleCardViewModel card, bool enabled)
    {
        if (!card.Status.CanToggle)
        {
            card.ResetSwitch();
            return;
        }

        card.IsBusy = true;
        try
        {
            await Connection.RequestAsync(Methods.SetModuleEnabled, new ModuleToggleRequest { Id = card.Id, Enabled = enabled }, TimeSpan.FromMinutes(2));
            Main.ShowToast(Loc.F(enabled ? "ToastModuleEnabled" : "ToastModuleDisabled", card.Name));
        }
        catch (Exception ex)
        {
            Main.ShowError(ex);
            card.ResetSwitch();
        }
        finally
        {
            card.IsBusy = false;
        }
    }

    public async Task RestartAsync(ModuleCardViewModel card)
    {
        try
        {
            await Connection.RequestAsync(Methods.RestartModule, new IdRequest { Id = card.Id }, TimeSpan.FromMinutes(2));
            Main.ShowToast(Loc.F("ToastModuleRestarted", card.Name));
        }
        catch (Exception ex)
        {
            Main.ShowError(ex);
        }
    }

    public void ShowLog(ModuleCardViewModel card)
    {
        Main.Logs.ShowSource(card.Id == ModuleIds.WebServer ? "engine" : "module:" + card.Id);
        Main.NavigateTo(Main.Logs);
    }

    // ------------------------------------------------------------------ MariaDB

    /// <summary>Address of the running Adminer site, null when it is off.</summary>
    public string? AdminerUrl => Main.Status?.Modules.FirstOrDefault(m => m.Id == ModuleIds.Adminer) is { State: ModuleState.Running, Url: { } url } ? url : null;

    /// <summary>Opens Adminer with server and user filled in; the root password goes to the clipboard.</summary>
    public async Task OpenAdminerAsync(string? database)
    {
        if (AdminerUrl is not { } url)
        {
            Main.ShowToast(Loc.T("ToastAdminerOff"), isError: true);
            return;
        }

        try
        {
            var credentials = await Connection.RequestAsync<DbCredentials>(Methods.GetDbCredentials);
            var target = $"{url}?server=127.0.0.1:{credentials?.Port ?? 47204}&username=root"
                         + (string.IsNullOrEmpty(database) ? "" : "&db=" + Uri.EscapeDataString(database));
            if (!string.IsNullOrEmpty(credentials?.Password))
            {
                await Main.CopyAsync(credentials.Password, "ToastAdminerPassword");
            }

            AppEnvironment.OpenUrl(target);
        }
        catch (Exception ex)
        {
            Main.ShowError(ex);
        }
    }

    public async Task CopyDbPasswordAsync()
    {
        try
        {
            var credentials = await Connection.RequestAsync<DbCredentials>(Methods.GetDbCredentials);
            if (string.IsNullOrEmpty(credentials?.Password))
            {
                Main.ShowToast(Loc.T("ToastDbNoPassword"), isError: true);
                return;
            }

            await Main.CopyAsync(credentials.Password, "ToastDbPasswordCopied");
        }
        catch (Exception ex)
        {
            Main.ShowError(ex);
        }
    }

    public async Task CreateDatabaseAsync()
    {
        var name = await Host.PromptAsync(Loc.T("DlgCreateDbTitle"), Loc.T("DlgCreateDbText"), Loc.T("BtnCreate"), "my_app");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            var result = await Connection.RequestAsync<CreateDatabaseResult>(Methods.CreateDatabase, new CreateDatabaseRequest { Name = name }, TimeSpan.FromMinutes(1));
            if (result is null)
            {
                return;
            }

            // Address clients use: this computer, or its IP when MariaDB is reachable from the network.
            var credentials = await Connection.RequestAsync<DbCredentials>(Methods.GetDbCredentials);
            var server = credentials is null ? "" : $"{credentials.Host}:{credentials.Port}";
            await Host.ShowTextAsync(
                Loc.T("DlgCreateDbTitle"),
                Loc.F("DbCreatedText", result.Database, result.User, result.Password, server));
        }
        catch (Exception ex)
        {
            Main.ShowError(ex);
        }
    }

    public async Task BackupDatabaseAsync()
    {
        try
        {
            Main.ShowToast(Loc.T("ToastDbBackupStarted"));
            var result = await Connection.RequestAsync<BackupResult>(Methods.BackupDatabase, null, TimeSpan.FromMinutes(30));
            if (result is not null)
            {
                Main.ShowToast(Loc.F("ToastDbBackupDone", Path.GetFileName(result.Path)));
                AppEnvironment.OpenFolder(Path.GetDirectoryName(result.Path));
            }
        }
        catch (Exception ex)
        {
            Main.ShowError(ex);
        }
    }

    [RelayCommand]
    private Task RestartEngineAsync() =>
        Cards.FirstOrDefault(c => c.Id == ModuleIds.WebServer) is { } engine ? RestartAsync(engine) : Task.CompletedTask;
}
