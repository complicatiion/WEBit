using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WEBit.App.Localization;
using WEBit.App.Services;
using WEBit.Contracts;
using WEBit.Core.Models;

namespace WEBit.App.ViewModels;

/// <summary>One dependency (Caddy, PHP, Mailpit, VC++ runtime): state, version, actions.</summary>
public sealed partial class ComponentCardViewModel(DependenciesViewModel owner, ComponentStatus status) : ObservableObject
{
    [ObservableProperty]
    private ComponentStatus _status = status;

    partial void OnStatusChanged(ComponentStatus value) => OnPropertyChanged(string.Empty);

    public void Refresh() => OnPropertyChanged(string.Empty);

    public string Id => Status.Id;

    public string Name => Status.Name;

    public Geometry? Icon => Res.Icon(Status.Id switch
    {
        ModuleIds.Webit => "IconWeb",
        ModuleIds.WebServer => "IconServer",
        ModuleIds.Mailpit => "IconEmail",
        ModuleIds.Php => "IconWeb",
        ModuleIds.MariaDb => "IconDatabase",
        ModuleIds.Node => "IconNodejs",
        ModuleIds.Python => "IconPython",
        ModuleIds.Composer => "IconPackage",
        ModuleIds.WpCli => "IconConsole",
        ModuleIds.Adminer => "IconDatabase",
        ModuleIds.Xdebug => "IconBug",
        _ when ModuleIds.IsPhpVersion(Status.Id) => "IconWeb",
        _ => "IconShield"
    });

    public string KindText => Status.Kind switch
    {
        ComponentKind.Bundled => Loc.T("DepKindBundled"),
        ComponentKind.Download => Loc.T("DepKindDownload"),
        ComponentKind.Application => Loc.T("DepKindApplication"),
        _ => Loc.T("DepKindSystem")
    };

    public bool IsDownloading => Status.Download?.State == ModuleDownloadState.Running;

    public string StateText => IsDownloading
        ? Loc.T("DepStateDownloading")
        : Status.UpdateAvailable
            ? Loc.F("DepStateUpdate", Status.LatestVersion ?? "")
            : Status.Installed ? Loc.T("DepStateInstalled") : Loc.T("StateNotInstalled");

    public IBrush StateBrush => IsDownloading
        ? Res.Brush("BrushAccentHover")
        : Status.UpdateAvailable
            ? Res.Brush("BrushWarning")
            : Status.Installed ? Res.Brush("BrushSecondary") : Res.Brush("BrushDanger");

    public IBrush StateSoftBrush => IsDownloading
        ? Res.Brush("BrushAccentSoft")
        : Status.UpdateAvailable
            ? Res.Brush("BrushWarningSoft")
            : Status.Installed ? Res.Brush("BrushSecondarySoft") : Res.Brush("BrushDangerSoft");

    public string Version => string.IsNullOrEmpty(Status.Version) ? "–" : Status.Version!;

    public string Latest => string.IsNullOrEmpty(Status.LatestVersion) ? "–" : Status.LatestVersion!;

    public string PathText => string.IsNullOrEmpty(Status.Path) ? "–" : Status.Path!;

    public bool HasPath => !string.IsNullOrEmpty(Status.Path);

    public double DownloadPercent => Status.Download?.Percent ?? 0;

    public bool DownloadIndeterminate => IsDownloading && Status.Download?.Percent is null;

    public string? DownloadText => Status.Download?.Message is { } m ? Loc.Message(m) : null;

    public bool DownloadFailed => Status.Download?.State == ModuleDownloadState.Failed;

    // Actions: modules install/update/remove, the VC++ runtime installs into Windows.
    public bool IsModule => Status.Kind == ComponentKind.Download;

    public bool IsVcRuntime => Status.Kind == ComponentKind.SystemWide;

    public bool ShowInstall => Status.CanDownload && !IsDownloading && !Status.Installed;

    /// <summary>Optional runtimes (MariaDB, Node.js, Python) are installed one by one, not with "download missing".</summary>
    public bool IsOptional => Status.Id is ModuleIds.MariaDb or ModuleIds.Node or ModuleIds.Python
        or ModuleIds.Composer or ModuleIds.WpCli or ModuleIds.Adminer or ModuleIds.Xdebug
        || ModuleIds.IsPhpVersion(Status.Id);

    public bool ShowUpdate => !IsDownloading && Status.Installed && (IsModule || Status.UpdateAvailable);

    /// <summary>Uninstall: downloaded files (PHP, Mailpit, downloaded Caddy) or the VC++ runtime from Windows.</summary>
    public bool ShowRemove => !IsDownloading && (Status.CanRemove || (IsVcRuntime && Status.Installed));

    public bool UpdateIsPrimary => Status.UpdateAvailable;

    [RelayCommand]
    private Task InstallAsync() => owner.InstallAsync(this);

    /// <summary>WEBit itself: opens the release page (installer or portable ZIP); modules are downloaded.</summary>
    [RelayCommand]
    private Task UpdateAsync()
    {
        if (Status.Kind == ComponentKind.Application)
        {
            AppEnvironment.OpenUrl(Status.Source);
            return Task.CompletedTask;
        }

        return owner.DownloadAsync(this);
    }

    [RelayCommand]
    private Task RemoveAsync() => owner.RemoveAsync(this);

    [RelayCommand]
    private void OpenFolder()
    {
        if (Status.Path is { } path)
        {
            AppEnvironment.OpenFolder(Path.GetDirectoryName(path));
        }
    }

    [RelayCommand]
    private void OpenSource() => AppEnvironment.OpenUrl(Status.Source);
}

/// <summary>Dependencies page: install, update, remove and check all components in one place.</summary>
public sealed partial class DependenciesViewModel(MainViewModel main) : PageViewModel(main)
{
    /// <summary>VC++ installer requested on this page: run it as soon as its download is done.</summary>
    private bool _installVcWhenReady;

    /// <summary>VC++ uninstall requested without installer on disk: run it after the download.</summary>
    private bool _uninstallVcWhenReady;

    public override string TitleKey => "NavDependencies";

    public ObservableCollection<ComponentCardViewModel> Cards { get; } = [];

    public bool IsChecking => Main.Status?.CheckingUpdates == true;

    public string LastCheckText => Main.Status?.LastUpdateCheck is { } t
        ? Loc.F("DepLastCheck", t.ToLocalTime().ToString("g"))
        : "";

    /// <summary>Only after a check: no "never checked" notice.</summary>
    public bool HasLastCheck => Main.Status?.LastUpdateCheck is not null;

    public bool HasMissing => Cards.Any(c => c.ShowInstall && c.IsModule && !c.IsOptional);

    public bool HasUpdates => Cards.Any(c => c.Status.UpdateAvailable && c.Status.CanDownload);

    public override void OnStatus(ServiceStatus status)
    {
        var incoming = status.Components;
        for (var i = Cards.Count - 1; i >= 0; i--)
        {
            if (incoming.All(c => c.Id != Cards[i].Id))
            {
                Cards.RemoveAt(i);
            }
        }

        ModuleDownloadState? vcBefore = null;
        ModuleDownloadState? vcAfter = null;
        for (var i = 0; i < incoming.Count; i++)
        {
            var existing = Cards.FirstOrDefault(c => c.Id == incoming[i].Id);
            if (existing is null)
            {
                Cards.Insert(Math.Min(i, Cards.Count), new ComponentCardViewModel(this, incoming[i]));
            }
            else
            {
                if (existing.IsVcRuntime)
                {
                    vcBefore = existing.Status.Download?.State;
                    vcAfter = incoming[i].Download?.State;
                }

                existing.Status = incoming[i];
            }
        }

        OnPropertyChanged(nameof(IsChecking));
        OnPropertyChanged(nameof(LastCheckText));
        OnPropertyChanged(nameof(HasLastCheck));
        OnPropertyChanged(nameof(HasMissing));
        OnPropertyChanged(nameof(HasUpdates));

        // VC++: installer download just finished → run it (administrator prompt).
        if ((_installVcWhenReady || _uninstallVcWhenReady) && vcBefore == ModuleDownloadState.Running
            && vcAfter is ModuleDownloadState.Completed or ModuleDownloadState.Failed)
        {
            var uninstall = _uninstallVcWhenReady;
            _installVcWhenReady = _uninstallVcWhenReady = false;
            if (vcAfter == ModuleDownloadState.Completed && Cards.FirstOrDefault(c => c.IsVcRuntime) is { } vc)
            {
                _ = Main.Modules.InstallVcRuntimeAsync(vc.Status.Path, uninstall);
            }
        }
    }

    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        foreach (var card in Cards)
        {
            card.Refresh();
        }

        OnPropertyChanged(nameof(LastCheckText));
    }

    public async Task InstallAsync(ComponentCardViewModel card)
    {
        if (card.IsVcRuntime && card.HasPath)
        {
            // Installer already downloaded: run it directly.
            await Main.Modules.InstallVcRuntimeAsync(card.Status.Path);
            return;
        }

        if (await DownloadAsync(card) && card.IsVcRuntime)
        {
            _installVcWhenReady = true;
        }
    }

    public async Task<bool> DownloadAsync(ComponentCardViewModel card)
    {
        try
        {
            await Connection.RequestAsync(Methods.DownloadModule, new IdRequest { Id = card.Id });
            Main.ShowToast(Loc.F("ToastDownloadStarted", card.Name));
            return true;
        }
        catch (Exception ex)
        {
            Main.ShowError(ex);
            return false;
        }
    }

    public async Task RemoveAsync(ComponentCardViewModel card)
    {
        if (card.IsVcRuntime)
        {
            // Windows component: Microsoft's installer removes it (needs the installer on disk).
            if (card.HasPath)
            {
                await Main.Modules.InstallVcRuntimeAsync(card.Status.Path, uninstall: true);
            }
            else if (await DownloadAsync(card))
            {
                _uninstallVcWhenReady = true;
            }

            return;
        }

        var confirm = await Host.ConfirmAsync(
            Loc.T("DlgRemoveModuleTitle"), Loc.F("DlgRemoveModuleText", card.Name), Loc.T("BtnRemove"), danger: true);
        if (!confirm.Confirmed)
        {
            return;
        }

        try
        {
            await Connection.RequestAsync(Methods.RemoveModule, new IdRequest { Id = card.Id }, TimeSpan.FromMinutes(2));
            Main.ShowToast(Loc.F("ToastModuleRemoved", card.Name));
        }
        catch (Exception ex)
        {
            Main.ShowError(ex);
        }
    }

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        try
        {
            await Connection.RequestAsync(Methods.CheckUpdates);
        }
        catch (Exception ex)
        {
            Main.ShowError(ex);
        }
    }

    [RelayCommand]
    private async Task InstallMissingAsync()
    {
        foreach (var card in Cards.Where(c => c.ShowInstall && c.IsModule && !c.IsOptional).ToList())
        {
            await DownloadAsync(card);
        }
    }

    [RelayCommand]
    private async Task UpdateAllAsync()
    {
        foreach (var card in Cards.Where(c => c.Status.UpdateAvailable && c.Status.CanDownload).ToList())
        {
            await DownloadAsync(card);
        }
    }

    [RelayCommand]
    private void OpenModulesFolder()
    {
        var first = Cards.FirstOrDefault(c => c.IsModule && c.HasPath)?.Status.Path;
        AppEnvironment.OpenFolder(first is null ? Main.Status?.DataRoot : Path.GetDirectoryName(Path.GetDirectoryName(first)));
    }
}
