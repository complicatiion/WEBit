using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WEBit.App.Localization;
using WEBit.Contracts;
using WEBit.Core.Models;

namespace WEBit.App.ViewModels;

/// <summary>Site card (sites page, tray panel, tray menu).</summary>
public sealed partial class SiteCardViewModel : ObservableObject
{
    private readonly SitesViewModel _owner;

    private bool _syncing;

    public SiteCardViewModel(SitesViewModel owner, SiteStatus status)
    {
        _owner = owner;
        _status = status;
        _isOn = status.Site.Enabled;
    }

    [ObservableProperty]
    private SiteStatus _status;

    /// <summary>On/off switch of the card; user changes start or stop the site.</summary>
    [ObservableProperty]
    private bool _isOn;

    partial void OnStatusChanged(SiteStatus value)
    {
        ResetSwitch();
        RefreshAll();
    }

    partial void OnIsOnChanged(bool value)
    {
        if (!_syncing && value != Site.Enabled)
        {
            _ = _owner.ToggleRunAsync(this);
        }
    }

    public void ResetSwitch()
    {
        _syncing = true;
        IsOn = Site.Enabled;
        _syncing = false;
    }

    public SiteDefinition Site => Status.Site;

    public string Id => Site.Id;

    public string Name => Site.Name;

    public string? Description => Site.Description;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Site.Description);

    public IBrush ColorBrush => Res.ParseBrush(Site.Color);

    public SiteRunState State => Status.State;

    public bool IsRunning => State is SiteRunState.Running or SiteRunState.Starting;

    public bool IsEnabled => Site.Enabled;

    public string StateText => State switch
    {
        SiteRunState.Running => Loc.T("StateRunning"),
        SiteRunState.Starting => Loc.T("StateStarting"),
        SiteRunState.Error => Loc.T("StateError"),
        _ => Loc.T("StateStopped")
    };

    public IBrush StateBrush => Res.StateBrush(State);

    public IBrush StateBackground => Res.StateSoftBrush(State);

    public string TypeText => Site.Type switch
    {
        SiteType.Redirect => Loc.T("TypeRedirect"),
        SiteType.Php => "PHP",
        SiteType.Node => "Node.js",
        SiteType.Python => "Python",
        SiteType.Proxy => Loc.T("TypeProxy"),
        _ => Loc.T("TypeStatic")
    };

    /// <summary>Node.js/Python: WEBit runs a process for the site.</summary>
    public bool IsApp => Site.IsApp;

    public bool IsWordPress => Site.Framework == SiteTemplates.WordPress;

    public bool HasDatabase => !string.IsNullOrEmpty(Site.Database);

    /// <summary>npm install / uv sync / composer install.</summary>
    public bool CanInstallPackages => IsApp || Site.Framework == SiteTemplates.Laravel;

    /// <summary>Badge: dev server (testing) or continuous operation.</summary>
    public string AppModeText => Site.App.KeepAlive ? Loc.T("BadgeKeepAlive") : Loc.T("BadgeDevServer");

    public string? AppCommandTip => Status.AppCommand is { } command ? $"{command}  ·  127.0.0.1:{Site.App.Port}" : null;

    public string? CharsetText => Site.ServesFiles && Site.Content.Charset != CharsetMode.Utf8
        ? Site.Content.Charset switch
        {
            CharsetMode.Auto => "Charset: Meta",
            CharsetMode.Windows1252 => "Windows-1252",
            CharsetMode.Iso88591 => "ISO-8859-1",
            _ => null
        }
        : null;

    public bool HasCharsetBadge => CharsetText is not null;

    public string PrimaryUrl => Status.PrimaryUrl;

    public bool HasUrl => !string.IsNullOrEmpty(PrimaryUrl);

    /// <summary>Live reload on: the browser reloads on every saved change.</summary>
    public bool IsLive => Site.HasLiveView;

    /// <summary>Address opened by "Open": the live wrapper for live reload sites.</summary>
    public string OpenTarget => IsLive && HasUrl ? WEBit.Core.Urls.UrlBuilder.LiveUrl(PrimaryUrl) : PrimaryUrl;

    public string RootDisplay => Site.Type switch
    {
        SiteType.Redirect => "→ " + Site.RedirectTarget,
        SiteType.Proxy => "→ " + Site.ProxyTarget,
        _ => Site.Root
    };

    public string? Message => Status.Message is null ? null : Loc.Message(Status.Message);

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    public Geometry? ToggleRunIcon => Res.Icon(IsEnabled ? "IconStop" : "IconPlay");

    public string ToggleRunTip => IsEnabled ? Loc.T("TipStopSite") : Loc.T("TipStartSite");

    public string ToggleRunText => IsEnabled ? Loc.T("BtnStop") : Loc.T("BtnStart");

    public bool Matches(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        var f = filter.Trim();
        return Name.Contains(f, StringComparison.CurrentCultureIgnoreCase)
               || (Description?.Contains(f, StringComparison.CurrentCultureIgnoreCase) ?? false)
               || PrimaryUrl.Contains(f, StringComparison.OrdinalIgnoreCase)
               || Site.Root.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    public void RefreshAll() => OnPropertyChanged(string.Empty);

    [RelayCommand]
    private void Open() => Services.AppEnvironment.OpenUrl(OpenTarget);

    [RelayCommand]
    private Task ToggleRunAsync() => _owner.ToggleRunAsync(this);

    [RelayCommand]
    private Task EditAsync() => _owner.EditAsync(this);

    [RelayCommand]
    private Task CopyUrlAsync() => _owner.Main.CopyAsync(PrimaryUrl, "ToastUrlCopied");

    [RelayCommand]
    private Task CopyMessageAsync() => _owner.CopyMessageAsync(this);

    [RelayCommand]
    private void OpenFolder() => Services.AppEnvironment.OpenFolder(Site.Root);

    [RelayCommand]
    private void ShowLogs() => _owner.ShowLogs(this);

    [RelayCommand]
    private Task DuplicateAsync() => _owner.DuplicateAsync(this);

    [RelayCommand]
    private Task RestartAppAsync() => _owner.RestartAppAsync(this);

    [RelayCommand]
    private Task InstallAppAsync() => _owner.InstallAppAsync(this);

    [RelayCommand]
    private Task ShowQrAsync() => _owner.Main.Host.ShowQrAsync(Name,
        Status.Urls.Where(u => u.Kind != WEBit.Core.Urls.SiteUrlKind.DeepLink).Select(u => u.Url).Distinct().ToList());

    [RelayCommand]
    private Task ExportAsync() => _owner.Main.Settings.Transfer.ExportAsync([Id], databases: true, packages: false, settings: false);

    [RelayCommand]
    private void OpenWpAdmin() => Services.AppEnvironment.OpenUrl(PrimaryUrl.TrimEnd('/') + "/wp-admin/");

    [RelayCommand]
    private Task OpenDatabaseAsync() => _owner.Main.Modules.OpenAdminerAsync(Site.Database);

    [RelayCommand]
    private Task DeleteAsync() => _owner.DeleteAsync(this);
}
