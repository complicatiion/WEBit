using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WEBit.App.Localization;
using WEBit.Contracts;
using WEBit.Core.Analysis;
using WEBit.Core.Models;
using WEBit.Core.Net;
using WEBit.Core.Sites;
using WEBit.Core.Urls;

namespace WEBit.App.ViewModels;

/// <summary>Sites page: cards of all sites.</summary>
public sealed partial class SitesViewModel(MainViewModel main) : PageViewModel(main)
{
    public override string TitleKey => "NavSites";


    /// <summary>All cards (also used by tray and dashboard).</summary>
    public ObservableCollection<SiteCardViewModel> Cards { get; } = [];

    /// <summary>Filtered cards.</summary>
    public ObservableCollection<SiteCardViewModel> VisibleCards { get; } = [];

    [ObservableProperty]
    private string? _search;

    partial void OnSearchChanged(string? value) => ApplyFilter();

    public bool HasNoSites => Cards.Count == 0;

    public bool HasNoMatches => Cards.Count > 0 && VisibleCards.Count == 0;

    public int RunningCount => Cards.Count(c => c.State == SiteRunState.Running);

    public int ErrorCount => Cards.Count(c => c.State == SiteRunState.Error);

    public override void OnStatus(ServiceStatus status)
    {
        var incoming = status.Sites.ToDictionary(s => s.Site.Id, StringComparer.OrdinalIgnoreCase);

        for (var i = Cards.Count - 1; i >= 0; i--)
        {
            if (!incoming.ContainsKey(Cards[i].Id))
            {
                Cards.RemoveAt(i);
            }
        }

        var index = 0;
        foreach (var siteStatus in status.Sites)
        {
            var existing = Cards.FirstOrDefault(c => string.Equals(c.Id, siteStatus.Site.Id, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                Cards.Insert(Math.Min(index, Cards.Count), new SiteCardViewModel(this, siteStatus));
            }
            else
            {
                existing.Status = siteStatus;
                var current = Cards.IndexOf(existing);
                if (current != index && index < Cards.Count)
                {
                    Cards.Move(current, index);
                }
            }

            index++;
        }

        ApplyFilter();
    }

    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        foreach (var card in Cards)
        {
            card.RefreshAll();
        }
    }

    private void ApplyFilter()
    {
        var visible = Cards.Where(c => c.Matches(Search)).ToList();
        if (!visible.SequenceEqual(VisibleCards))
        {
            VisibleCards.Clear();
            foreach (var card in visible)
            {
                VisibleCards.Add(card);
            }
        }

        OnPropertyChanged(nameof(HasNoSites));
        OnPropertyChanged(nameof(HasNoMatches));
        OnPropertyChanged(nameof(RunningCount));
        OnPropertyChanged(nameof(ErrorCount));
    }

    // ------------------------------------------------------------------ Actions

    [RelayCommand]
    public async Task NewSiteAsync()
    {
        var status = Main.Status;
        var name = Loc.T("DefaultSiteName");
        var www = status?.WwwRoot ?? "";

        // The folder follows the name in the editor (www\<name>).
        var site = new SiteDefinition
        {
            Name = name,
            Root = string.IsNullOrEmpty(www) ? "" : Path.Combine(www, SiteFolders.FromName(name)),
            Bindings = [new SiteBinding { Address = HostNetwork.FirstIPv4() ?? "0.0.0.0", Port = 0 }]
        };

        await OpenEditorAsync(site, isNew: true);
    }

    public Task EditAsync(SiteCardViewModel card) => OpenEditorAsync(card.Site.Clone(), isNew: false);

    /// <summary>A template site is being created (downloads, installation).</summary>
    [ObservableProperty]
    private bool _isCreating;

    [RelayCommand]
    private Task NewWordPressAsync() => NewFromTemplateAsync(SiteTemplates.WordPress);

    [RelayCommand]
    private Task NewLaravelAsync() => NewFromTemplateAsync(SiteTemplates.Laravel);

    /// <summary>WordPress (WP-CLI, MariaDB) or Laravel (Composer): missing modules are offered for download first.</summary>
    public async Task NewFromTemplateAsync(string template)
    {
        if (!Connection.IsConnected || Main.Status is not { } status)
        {
            Main.ShowToast(Loc.T("ErrNotConnected"), isError: true);
            return;
        }

        var wordpress = template == SiteTemplates.WordPress;
        string[] required = wordpress ? [ModuleIds.Php, ModuleIds.MariaDb, ModuleIds.WpCli] : [ModuleIds.Php, ModuleIds.Composer];
        var missing = Main.Modules.Cards.Where(c => required.Contains(c.Id) && c.Status.State == ModuleState.NotInstalled).ToList();
        if (missing.Count > 0)
        {
            var confirm = await Host.ConfirmAsync(Loc.T("DlgTemplateMissingTitle"),
                Loc.F("DlgTemplateMissingText", string.Join(", ", missing.Select(m => m.Name))), Loc.T("BtnDownload"), danger: false);
            if (confirm.Confirmed)
            {
                foreach (var card in missing)
                {
                    await Main.Modules.DownloadAsync(card);
                }
            }

            return;
        }

        if (wordpress && !status.Modules.Any(m => m.Id == ModuleIds.MariaDb && m.State == ModuleState.Running))
        {
            Main.ShowToast(Loc.T("ToastMariaDbNotRunning"), isError: true);
            return;
        }

        var request = await Host.TemplateAsync(template, wordpress ? "WordPress" : "Laravel");
        if (request is null)
        {
            return;
        }

        // The folder is named after the site: an existing one gets the next free name.
        if (!string.IsNullOrEmpty(status.WwwRoot))
        {
            var folder = SiteFolders.FromName(request.Name);
            var free = SiteFolders.Suggest(status.WwwRoot, folder);
            if (!string.Equals(free, folder, StringComparison.OrdinalIgnoreCase))
            {
                var rename = await Host.ConfirmAsync(Loc.T("DlgFolderExistsTitle"), Loc.F("DlgTemplateFolderText", folder, free), Loc.T("BtnCreate"), danger: false);
                if (!rename.Confirmed)
                {
                    return;
                }

                request.Name = free;
            }
        }

        IsCreating = true;
        Main.ShowToast(Loc.F("ToastTemplateStarted", request.Name));
        try
        {
            var result = await Connection.RequestAsync<TemplateResult>(Methods.CreateFromTemplate, request, TimeSpan.FromMinutes(25));
            if (result is null)
            {
                return;
            }

            Main.ShowToast(Loc.F("ToastQuickSite", result.Site.Name));
            var text = wordpress
                ? Loc.F("TplWordPressDone", result.Url, result.AdminUrl ?? "", result.AdminUser ?? "", result.AdminPassword ?? "",
                    result.Database ?? "", result.DatabaseUser ?? "", result.DatabasePassword ?? "")
                : Loc.F("TplLaravelDone", result.Url, result.Site.Root);
            await Host.ShowTextAsync(result.Site.Name, text);
            Services.AppEnvironment.OpenUrl(wordpress ? result.AdminUrl ?? result.Url : result.Url);
        }
        catch (Exception ex)
        {
            Main.ShowError(ex);
        }
        finally
        {
            IsCreating = false;
        }
    }

    /// <summary>
    /// The Windows service runs as NT SERVICE\WEBit: give it read access (apps: modify) to folders outside
    /// the WEBit data folder (user's own folders need no administrator rights).
    /// </summary>
    public static async Task EnsureServiceAccessAsync(MainViewModel main, string? folder, bool write = false)
    {
        if (!OperatingSystem.IsWindows() || main.Status is not { IsWindowsService: true } status
            || string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return;
        }

        var full = Path.GetFullPath(folder);
        if (!string.IsNullOrEmpty(status.DataRoot) && full.StartsWith(Path.GetFullPath(status.DataRoot), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Node.js/Python apps write packages and caches into their folder (node_modules, .venv).
        await Task.Run(() => write
            ? WEBit.Platform.Windows.FolderAccess.GrantServiceModify(full)
            : WEBit.Platform.Windows.FolderAccess.GrantServiceRead(full));
    }

    /// <summary>
    /// Quick test: dropped folders become sites with live reload (type from the folder: PHP or static)
    /// and the first one opens in the browser.
    /// </summary>
    public async Task CreateQuickSitesAsync(IReadOnlyList<string> folders)
    {
        if (folders.Count == 0)
        {
            return;
        }

        if (!Connection.IsConnected)
        {
            Main.ShowToast(Loc.T("ErrNotConnected"), isError: true);
            return;
        }

        var reserved = new List<int>();
        string? openUrl = null;
        foreach (var folder in folders)
        {
            var existing = Cards.FirstOrDefault(c => string.Equals(
                c.Site.Root.TrimEnd('\\', '/'), folder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                // Already a site: just open it.
                openUrl ??= existing.OpenTarget;
                continue;
            }

            try
            {
                var analysis = await Connection.RequestAsync<FolderAnalysis>(Methods.AnalyzeFolder, new PathRequest { Path = folder });
                var address = HostNetwork.FirstIPv4() ?? "127.0.0.1";
                var port = (await Connection.RequestAsync<SuggestPortResponse>(
                    Methods.SuggestPort, new SuggestPortRequest { Address = address, Reserved = reserved }))?.Port;
                if (port is null)
                {
                    Main.ShowToast(Loc.T("ToastNoFreePort"), isError: true);
                    return;
                }

                reserved.Add(port.Value);
                var type = analysis?.SuggestedType ?? SiteType.Static;
                var site = new SiteDefinition
                {
                    Name = Path.GetFileName(folder.TrimEnd('\\', '/')),
                    Description = type switch
                    {
                        SiteType.Php => "PHP · quick test",
                        SiteType.Node => "Node.js · dev server",
                        SiteType.Python => "Python · dev server",
                        _ => "Quick test"
                    },
                    Type = type,
                    Root = folder,
                    LiveReload = type is SiteType.Static or SiteType.Php,
                    // Color: random, resolved by the service (unused colors first).
                    Bindings = [new SiteBinding { Address = address, Port = port.Value }]
                };
                site.Content.Charset = analysis?.SuggestedCharset ?? CharsetMode.Auto;
                await EnsureServiceAccessAsync(Main, folder, write: site.IsApp);

                var result = await Connection.RequestAsync<SaveSiteResult>(Methods.SaveSite, site, TimeSpan.FromMinutes(2));
                if (result is null)
                {
                    continue;
                }

                Main.ShowToast(Loc.F("ToastQuickSite", result.Site.Name));

                // Apps need a moment (packages, dev server): opened from the card once they run.
                if (result.Site.ServesFiles)
                {
                    var url = UrlBuilder.Primary(result.Site, new UrlContext(Main.Status?.Fqdn, null));
                    openUrl ??= UrlBuilder.LiveUrl(url);
                }
            }
            catch (Exception ex)
            {
                Main.ShowError(ex);
                return;
            }
        }

        if (openUrl is not null)
        {
            Services.AppEnvironment.OpenUrl(openUrl);
        }
    }

    public async Task DuplicateAsync(SiteCardViewModel card)
    {
        var copy = card.Site.Clone();
        copy.Id = "";
        copy.Name = Loc.F("CopyOfName", card.Name);
        copy.Enabled = false;
        foreach (var binding in copy.Bindings)
        {
            binding.Port = 0;
            binding.Host = null;
        }

        await OpenEditorAsync(copy, isNew: true);
    }

    private async Task OpenEditorAsync(SiteDefinition site, bool isNew)
    {
        if (!Connection.IsConnected)
        {
            Main.ShowToast(Loc.T("ErrNotConnected"), isError: true);
            return;
        }

        var editor = new SiteEditorViewModel(Main, site, isNew);
        var result = await Host.EditSiteAsync(editor);
        if (result is null)
        {
            return;
        }

        var errors = result.Issues.Where(i => i.Level == IssueLevel.Error).ToList();
        Main.ShowToast(
            errors.Count == 0 ? Loc.F("ToastSiteSaved", result.Site.Name) : Loc.F("ToastSiteSavedWithErrors", result.Site.Name, Loc.Message(errors[0].Message)),
            isError: errors.Count > 0);
    }

    public async Task ToggleRunAsync(SiteCardViewModel card)
    {
        try
        {
            await Connection.RequestAsync(card.IsEnabled ? Methods.StopSite : Methods.StartSite, new IdRequest { Id = card.Id });
        }
        catch (Exception ex)
        {
            Main.ShowError(ex);
            card.ResetSwitch();
        }
    }

    public async Task DeleteAsync(SiteCardViewModel card)
    {
        var confirmed = await Host.ConfirmAsync(
            Loc.T("DlgDeleteSiteTitle"),
            Loc.F("DlgDeleteSiteText", card.Name),
            Loc.F("BtnDeleteSiteNamed", card.Name),
            danger: true);

        if (!confirmed.Confirmed)
        {
            return;
        }

        try
        {
            await Connection.RequestAsync(Methods.DeleteSite, new IdRequest { Id = card.Id });
            Main.ShowToast(Loc.F("ToastSiteDeleted", card.Name));
        }
        catch (Exception ex)
        {
            Main.ShowError(ex);
        }
    }

    public Task CopyMessageAsync(SiteCardViewModel card)
    {
        var links = card.Status.Urls
            .Where(u => u.Kind == SiteUrlKind.DeepLink)
            .Select(u => new ExportLink(u.Label ?? u.Url, u.Url))
            .ToList();
        var text = LinkExport.RenderMessage(Main.MessageTemplate, card.Name, card.PrimaryUrl, card.Description, links);
        return Main.CopyAsync(text, "ToastMessageCopied");
    }

    /// <summary>App sites open the output of their process, the others their access log.</summary>
    public void ShowLogs(SiteCardViewModel card)
    {
        Main.Logs.ShowSource((card.IsApp ? "app:" : "site:") + card.Id);
        Main.NavigateTo(Main.Logs);
    }

    public async Task RestartAppAsync(SiteCardViewModel card)
    {
        try
        {
            await Connection.RequestAsync(Methods.RestartApp, new IdRequest { Id = card.Id });
            Main.ShowToast(Loc.F("ToastAppRestarted", card.Name));
        }
        catch (Exception ex)
        {
            Main.ShowError(ex);
        }
    }

    public async Task InstallAppAsync(SiteCardViewModel card)
    {
        try
        {
            Main.ShowToast(Loc.F("ToastAppInstallStarted", card.Name));
            var ok = await Connection.RequestAsync<bool>(Methods.InstallApp, new IdRequest { Id = card.Id }, TimeSpan.FromMinutes(25));
            Main.ShowToast(Loc.F(ok ? "ToastAppInstallDone" : "ToastAppInstallFailed", card.Name), isError: !ok);
        }
        catch (Exception ex)
        {
            Main.ShowError(ex);
        }
    }

    [RelayCommand]
    private async Task StartAllAsync()
    {
        try
        {
            await Connection.RequestAsync(Methods.StartAll);
        }
        catch (Exception ex)
        {
            Main.ShowError(ex);
        }
    }

    [RelayCommand]
    private async Task StopAllAsync()
    {
        var confirmed = await Host.ConfirmAsync(Loc.T("DlgStopAllTitle"), Loc.T("DlgStopAllText"), Loc.T("BtnStopAll"), danger: true, confirmIcon: "IconStopCircle");
        if (!confirmed.Confirmed)
        {
            return;
        }

        try
        {
            await Connection.RequestAsync(Methods.StopAll);
        }
        catch (Exception ex)
        {
            Main.ShowError(ex);
        }
    }
}
