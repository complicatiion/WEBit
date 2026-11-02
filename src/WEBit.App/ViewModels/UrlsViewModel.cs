using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WEBit.App.Localization;
using WEBit.App.Services;
using WEBit.Contracts;
using WEBit.Core.Models;
using WEBit.Core.Urls;

namespace WEBit.App.ViewModels;

/// <summary>Row of the URLs page.</summary>
public sealed partial class UrlRowViewModel(UrlsViewModel owner, SiteStatus site, SiteUrl url) : ObservableObject
{
    public SiteStatus SiteStatus { get; } = site;

    public string SiteId => SiteStatus.Site.Id;

    public string SiteName => SiteStatus.Site.Name;

    public string Url { get; } = url.Url;

    public SiteUrlKind Kind { get; } = url.Kind;

    public string? Label { get; } = url.Label;

    public string Title => Kind == SiteUrlKind.DeepLink && !string.IsNullOrWhiteSpace(Label) ? $"{SiteName} – {Label}" : SiteName;

    public string KindText => Kind switch
    {
        SiteUrlKind.Primary => Loc.T("UrlKindPrimary"),
        SiteUrlKind.Binding => Loc.T("UrlKindBinding"),
        SiteUrlKind.Alternative => Loc.T("UrlKindAlternative"),
        _ => Loc.T("UrlKindDeepLink")
    };

    public IBrush KindBrush => Kind == SiteUrlKind.Primary ? Res.Brush("BrushAccentSoft") : Res.Brush("BrushSurfaceRaised");

    public bool SiteRunning => SiteStatus.State == SiteRunState.Running;

    public IBrush SiteStateBrush => Res.StateBrush(SiteStatus.State);

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _dnsText = "–";

    [ObservableProperty]
    private IBrush? _dnsBrush;

    [ObservableProperty]
    private string _httpText = "–";

    [ObservableProperty]
    private IBrush? _httpBrush;

    public ExportLink ToExport() => new(Title, Url, null);

    public void Apply(UrlCheckResult result)
    {
        DnsText = string.IsNullOrEmpty(result.DnsText) ? (result.DnsLevel == IssueLevel.Ok ? "OK" : "–") : result.DnsText!;
        DnsBrush = Res.LevelBrush(result.DnsLevel);
        HttpText = result.HttpStatus is { } code ? $"{code} · {result.ElapsedMs} ms" : result.Error ?? "–";
        HttpBrush = Res.LevelBrush(result.HttpLevel);
    }

    public void CopyStateFrom(UrlRowViewModel other)
    {
        IsSelected = other.IsSelected;
        DnsText = other.DnsText;
        DnsBrush = other.DnsBrush;
        HttpText = other.HttpText;
        HttpBrush = other.HttpBrush;
    }

    [RelayCommand]
    private Task CopyAsync() => owner.Main.CopyAsync(Url, "ToastUrlCopied");

    [RelayCommand]
    private void Open() => AppEnvironment.OpenUrl(Url);

    [RelayCommand]
    private Task CopyMessageAsync() => owner.CopyMessageAsync([this]);

    [RelayCommand]
    private Task CopyHtmlAsync() => owner.Main.CopyAsync(LinkExport.HtmlLink(ToExport()));

    [RelayCommand]
    private Task CopyMarkdownAsync() => owner.Main.CopyAsync(LinkExport.MarkdownLink(ToExport()));

    [RelayCommand]
    private Task SaveShortcutAsync() => owner.SaveShortcutAsync(this);

    [RelayCommand]
    private Task CheckAsync() => owner.CheckAsync([this]);
}

/// <summary>URLs page: generate, check, copy and export the addresses of all sites.</summary>
public sealed partial class UrlsViewModel(MainViewModel main) : PageViewModel(main)
{
    private readonly List<UrlRowViewModel> _all = [];
    private string _signature = "";

    public override string TitleKey => "NavUrls";


    public ObservableCollection<UrlRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    private string? _search;

    [ObservableProperty]
    private bool _onlyPrimary;

    [ObservableProperty]
    private bool _isBusy;

    partial void OnSearchChanged(string? value) => ApplyFilter();

    partial void OnOnlyPrimaryChanged(bool value) => ApplyFilter();

    public bool HasRows => Rows.Count > 0;

    public int SelectedCount => _all.Count(r => r.IsSelected);

    public string SelectionText => Loc.F("UrlsSelection", SelectedCount, _all.Count);

    public override void OnStatus(ServiceStatus status)
    {
        // Unchanged: keep rows (selection, check results, scroll position).
        var signature = string.Join("|", status.Sites.SelectMany(s => s.Urls.Select(u => $"{s.Site.Id}:{s.Site.Name}:{s.State}:{u.Kind}:{u.Url}:{u.Label}")));
        if (signature == _signature && _all.Count > 0)
        {
            return;
        }

        _signature = signature;
        var previous = _all.ToDictionary(r => r.SiteId + "|" + r.Url, StringComparer.OrdinalIgnoreCase);
        foreach (var row in _all)
        {
            row.PropertyChanged -= OnRowChanged;
        }

        _all.Clear();
        foreach (var site in status.Sites)
        {
            foreach (var url in site.Urls)
            {
                var row = new UrlRowViewModel(this, site, url);
                if (previous.TryGetValue(row.SiteId + "|" + row.Url, out var old))
                {
                    row.CopyStateFrom(old);
                }

                row.PropertyChanged += OnRowChanged;
                _all.Add(row);
            }
        }

        ApplyFilter();
    }

    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        _signature = "";
        if (Main.Status is { } status)
        {
            OnStatus(status);
        }
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UrlRowViewModel.IsSelected))
        {
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(SelectionText));
        }
    }

    private void ApplyFilter()
    {
        Rows.Clear();
        var filter = Search?.Trim();
        foreach (var row in _all)
        {
            if (OnlyPrimary && row.Kind != SiteUrlKind.Primary)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(filter) &&
                !row.Url.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !row.SiteName.Contains(filter, StringComparison.CurrentCultureIgnoreCase) &&
                !(row.Label?.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ?? false))
            {
                continue;
            }

            Rows.Add(row);
        }

        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionText));
    }

    /// <summary>Selection; without selection all visible rows.</summary>
    private List<UrlRowViewModel> Selection()
    {
        var selected = _all.Where(r => r.IsSelected).ToList();
        return selected.Count > 0 ? selected : Rows.ToList();
    }

    [RelayCommand]
    private void SelectAll()
    {
        var target = !Rows.All(r => r.IsSelected);
        foreach (var row in Rows)
        {
            row.IsSelected = target;
        }
    }

    [RelayCommand]
    private Task CheckAllAsync() => CheckAsync(Rows.ToList());

    public async Task CheckAsync(IReadOnlyList<UrlRowViewModel> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        IsBusy = true;
        foreach (var row in rows)
        {
            row.DnsText = "…";
            row.HttpText = "…";
        }

        try
        {
            var results = await Connection.RequestAsync<List<UrlCheckResult>>(
                Methods.CheckUrls,
                new UrlCheckRequest { Urls = rows.Select(r => r.Url).ToList() },
                TimeSpan.FromMinutes(2)) ?? [];

            var byUrl = results
                .GroupBy(r => r.Url, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            // Rows may have been replaced by a status update meanwhile.
            foreach (var row in rows.Concat(_all).Distinct())
            {
                if (byUrl.TryGetValue(row.Url, out var result))
                {
                    row.Apply(result);
                }
            }
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
    private Task CopySelectedAsync() =>
        Main.CopyAsync(string.Join(Environment.NewLine, Selection().Select(r => r.Url)), "ToastUrlsCopied");

    [RelayCommand]
    private Task CopySelectedMessageAsync() => CopyMessageAsync(Selection());

    public Task CopyMessageAsync(IReadOnlyList<UrlRowViewModel> rows)
    {
        if (rows.Count == 0)
        {
            return Task.CompletedTask;
        }

        string text;
        var sites = rows.Select(r => r.SiteStatus).DistinctBy(s => s.Site.Id).ToList();
        if (sites.Count == 1 && rows.Count == 1)
        {
            var site = sites[0];
            text = LinkExport.RenderMessage(Main.MessageTemplate, site.Site.Name, rows[0].Url, site.Site.Description);
        }
        else if (sites.Count == 1)
        {
            var site = sites[0];
            var primary = rows.FirstOrDefault(r => r.Kind == SiteUrlKind.Primary)?.Url ?? site.PrimaryUrl;
            var links = rows.Where(r => r.Url != primary).Select(r => new ExportLink(r.Label ?? r.KindText, r.Url));
            text = LinkExport.RenderMessage(Main.MessageTemplate, site.Site.Name, primary, site.Site.Description, links);
        }
        else
        {
            var links = rows.Select(r => r.ToExport());
            text = LinkExport.RenderMessage(Loc.T("TplMessageMulti"), "", "", null, links);
        }

        return Main.CopyAsync(text, "ToastMessageCopied");
    }

    [RelayCommand]
    private async Task ExportBookmarksAsync()
    {
        var links = Selection().Select(r => r.ToExport()).ToList();
        if (links.Count == 0)
        {
            return;
        }

        if (await Host.SaveTextAsync(Loc.T("ExportBookmarksTitle"), "webit_bookmarks.html", "html", "HTML", LinkExport.BookmarksHtml(links)))
        {
            Main.ShowToast(Loc.T("ToastExported"));
        }
    }

    [RelayCommand]
    private async Task ExportGpoAsync()
    {
        var links = Selection().Select(r => r.ToExport()).ToList();
        if (links.Count == 0)
        {
            return;
        }

        if (await Host.SaveTextAsync(Loc.T("ExportGpoTitle"), "webit_managed_bookmarks.json", "json", "JSON", LinkExport.ManagedBookmarksJson(links)))
        {
            Main.ShowToast(Loc.T("ToastExported"));
        }
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        var links = Selection().Select(r => r.ToExport()).ToList();
        if (links.Count == 0)
        {
            return;
        }

        if (await Host.SaveTextAsync(Loc.T("ExportCsvTitle"), "webit_urls.csv", "csv", "CSV", LinkExport.Csv(links)))
        {
            Main.ShowToast(Loc.T("ToastExported"));
        }
    }

    public async Task SaveShortcutAsync(UrlRowViewModel row)
    {
        var name = string.Concat(row.Title.Split(Path.GetInvalidFileNameChars())).Trim();
        if (await Host.SaveTextAsync(Loc.T("ExportShortcutTitle"), name + ".url", "url", Loc.T("FileTypeShortcut"), LinkExport.InternetShortcut(row.Url)))
        {
            Main.ShowToast(Loc.T("ToastExported"));
        }
    }
}
