using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WEBit.App.Localization;
using WEBit.App.Services;
using WEBit.Contracts;
using WEBit.Core.Diagnostics;
using WEBit.Core.Models;

namespace WEBit.App.ViewModels;

/// <summary>One MariaDB database: size, sites using it, Adminer, backup, delete.</summary>
public sealed partial class DatabaseItemViewModel(DatabasesViewModel owner, DatabaseInfo info) : ObservableObject
{
    public DatabaseInfo Info { get; } = info;

    public string Name => Info.Name;

    public string SizeText => LogReader.FormatBytes(Info.SizeBytes);

    public bool HasSites => Info.Sites.Count > 0;

    public string SitesText => string.Join(", ", Info.Sites);

    [RelayCommand]
    private Task OpenAdminerAsync() => owner.Main.Modules.OpenAdminerAsync(Name);

    [RelayCommand]
    private Task BackupAsync() => owner.BackupAsync(Name);

    [RelayCommand]
    private Task DeleteAsync() => owner.DeleteAsync(this);
}

/// <summary>One backup file: restore into its database or delete.</summary>
public sealed partial class BackupItemViewModel(DatabasesViewModel owner, BackupInfo info) : ObservableObject
{
    public BackupInfo Info { get; } = info;

    public string DatabaseText => Info.Database ?? Loc.T("BackupAllDatabases");

    public string DateText => Info.CreatedAt.LocalDateTime.ToString("g", System.Globalization.CultureInfo.CurrentCulture);

    public string SizeText => LogReader.FormatBytes(Info.SizeBytes);

    public string KindText => Info.Automatic ? Loc.T("BackupAutomatic") : Loc.T("BackupManual");

    public bool CanRestore => Info.Database is not null;

    [RelayCommand]
    private Task RestoreAsync() => owner.RestoreAsync(this);

    [RelayCommand]
    private Task DeleteAsync() => owner.DeleteBackupAsync(this);
}

/// <summary>Databases page: MariaDB databases with Adminer, backup, new database and delete.</summary>
public sealed partial class DatabasesViewModel(MainViewModel main) : PageViewModel(main)
{
    private bool _active;
    private bool? _wasRunning;
    private bool _reloadPending;

    public override string TitleKey => "NavDatabases";

    public ObservableCollection<DatabaseItemViewModel> Items { get; } = [];

    public ObservableCollection<BackupItemViewModel> Backups { get; } = [];

    public bool HasBackups => Backups.Count > 0;

    [ObservableProperty]
    private string? _nextBackupText;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isLoading;

    public bool HasItems => Items.Count > 0;

    public bool IsEmpty => IsRunning && !IsLoading && Items.Count == 0;

    public bool HasAdminer => Main.Modules.AdminerUrl is not null;

    public override void OnActivated()
    {
        _active = true;
        _ = LoadAsync();
    }

    public override void OnDeactivated() => _active = false;

    public override void OnStatus(ServiceStatus status)
    {
        // MariaDB went up or down: reload the list while the page is open.
        var running = status.Modules.Any(m => m.Id == ModuleIds.MariaDb && m.State == ModuleState.Running);
        if (_wasRunning != running)
        {
            _wasRunning = running;
            if (_active)
            {
                _ = LoadAsync();
            }
        }

        OnPropertyChanged(nameof(HasAdminer));
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (!Connection.IsConnected)
        {
            return;
        }

        if (IsLoading)
        {
            // Reload once more after the running one (new, delete, status change).
            _reloadPending = true;
            return;
        }

        IsLoading = true;
        try
        {
            var list = await Connection.RequestAsync<DatabaseList>(Methods.GetDatabases, null, TimeSpan.FromMinutes(1));
            IsRunning = list?.Running == true;
            Items.Clear();
            foreach (var item in list?.Items ?? [])
            {
                Items.Add(new DatabaseItemViewModel(this, item));
            }

            await LoadBackupsAsync();
        }
        catch (Exception ex)
        {
            Main.ShowError(ex);
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(IsEmpty));
        }

        if (_reloadPending)
        {
            _reloadPending = false;
            await LoadAsync();
        }
    }

    private async Task LoadBackupsAsync()
    {
        var backups = await Connection.RequestAsync<BackupList>(Methods.GetBackups);
        Backups.Clear();
        foreach (var item in backups?.Items ?? [])
        {
            Backups.Add(new BackupItemViewModel(this, item));
        }

        NextBackupText = backups?.NextAutomatic is { } next
            ? Loc.F("BackupNext", next.LocalDateTime.ToString("g", System.Globalization.CultureInfo.CurrentCulture))
            : null;
        OnPropertyChanged(nameof(HasBackups));
    }

    public async Task RestoreAsync(BackupItemViewModel item)
    {
        var confirm = await Host.ConfirmAsync(Loc.T("DlgRestoreTitle"), Loc.F("DlgRestoreText", item.DatabaseText, item.DateText),
            Loc.T("BtnRestore"), danger: true, confirmIcon: "IconBackup");
        if (!confirm.Confirmed)
        {
            return;
        }

        try
        {
            Main.ShowToast(Loc.T("ToastRestoreStarted"));
            await Connection.RequestAsync(Methods.RestoreBackup, new BackupFileRequest { FileName = item.Info.FileName, Automatic = item.Info.Automatic },
                TimeSpan.FromMinutes(30));
            Main.ShowToast(Loc.F("ToastRestoreDone", item.DatabaseText));
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Main.ShowError(ex);
        }
    }

    public async Task DeleteBackupAsync(BackupItemViewModel item)
    {
        var confirm = await Host.ConfirmAsync(Loc.T("DlgDeleteBackupTitle"), Loc.F("DlgDeleteBackupText", item.Info.FileName),
            Loc.T("MenuDelete"), danger: true);
        if (!confirm.Confirmed)
        {
            return;
        }

        try
        {
            await Connection.RequestAsync(Methods.DeleteBackup, new BackupFileRequest { FileName = item.Info.FileName, Automatic = item.Info.Automatic });
            await LoadBackupsAsync();
        }
        catch (Exception ex)
        {
            Main.ShowError(ex);
        }
    }

    [RelayCommand]
    private async Task NewDatabaseAsync()
    {
        await Main.Modules.CreateDatabaseAsync();
        await LoadAsync();
    }

    [RelayCommand]
    private Task CopyPasswordAsync() => Main.Modules.CopyDbPasswordAsync();

    [RelayCommand]
    private Task OpenAdminerAsync() => Main.Modules.OpenAdminerAsync(null);

    [RelayCommand]
    private Task BackupAllAsync() => BackupAsync(null);

    [RelayCommand]
    private void OpenBackups() =>
        AppEnvironment.OpenFolder(Main.Status is { } s ? Path.Combine(s.DataRoot, "backups") : null);

    [RelayCommand]
    private void OpenModules() => Main.NavigateTo(Main.Modules);

    public async Task BackupAsync(string? name)
    {
        try
        {
            Main.ShowToast(Loc.T("ToastDbBackupStarted"));
            var result = await Connection.RequestAsync<BackupResult>(Methods.BackupDatabase, new BackupRequest { Name = name }, TimeSpan.FromMinutes(30));
            if (result is not null)
            {
                Main.ShowToast(Loc.F("ToastDbBackupDone", Path.GetFileName(result.Path)));
                await LoadBackupsAsync();
            }
        }
        catch (Exception ex)
        {
            Main.ShowError(ex);
        }
    }

    public async Task DeleteAsync(DatabaseItemViewModel item)
    {
        var text = item.HasSites ? Loc.F("DlgDropDbUsedText", item.Name, item.SitesText) : Loc.F("DlgDropDbText", item.Name);
        var confirm = await Host.ConfirmAsync(Loc.T("DlgDropDbTitle"), text, Loc.T("MenuDelete"), danger: true);
        if (!confirm.Confirmed)
        {
            return;
        }

        try
        {
            await Connection.RequestAsync(Methods.DropDatabase, new IdRequest { Id = item.Name }, TimeSpan.FromMinutes(1));
            Main.ShowToast(Loc.F("ToastDbDropped", item.Name));
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Main.ShowError(ex);
        }
    }
}
