using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WEBit.App.Localization;
using WEBit.Contracts;
using WEBit.Core.Analysis;
using WEBit.Core.Models;

namespace WEBit.App.ViewModels;

/// <summary>Card color choice.</summary>
public sealed partial class ColorOption(string hex, IBrush brush) : ObservableObject
{
    public string Hex { get; } = hex;

    public IBrush Brush { get; } = brush;

    /// <summary>The "random" choice shows a dice instead of a color.</summary>
    public bool IsRandom => Core.Sites.SiteColors.IsRandom(Hex);

    public string Tip => IsRandom ? Loc.T("TipRandomColor") : Hex;

    [ObservableProperty]
    private bool _isSelected;
}

public sealed record OptionItem<T>(T Value, string Title)
{
    public override string ToString() => Title;
}

/// <summary>Check message with icon and color.</summary>
public sealed class IssueViewModel(ValidationIssue issue)
{
    public IssueLevel Level { get; } = issue.Level;

    public string Text { get; } = Loc.Message(issue.Message);

    public IBrush Brush => Res.LevelBrush(Level);

    public Geometry? Icon => Res.LevelIcon(Level);
}

/// <summary>Binding row (IP, port, host name) in the editor.</summary>
public sealed partial class BindingRowViewModel(SiteEditorViewModel owner) : ObservableObject
{
    [ObservableProperty]
    private string _address = "0.0.0.0";

    [ObservableProperty]
    private decimal? _port;

    [ObservableProperty]
    private string? _host;

    /// <summary>HTTPS with a certificate of the local WEBit CA.</summary>
    [ObservableProperty]
    private bool _tls;

    partial void OnTlsChanged(bool value) => OnPropertyChanged(nameof(PreviewUrl));

    [ObservableProperty]
    private string? _checkText;

    [ObservableProperty]
    private IBrush? _checkBrush;

    [ObservableProperty]
    private Geometry? _checkIcon;

    public bool HasCheck => !string.IsNullOrEmpty(CheckText);

    partial void OnCheckTextChanged(string? value) => OnPropertyChanged(nameof(HasCheck));

    public ObservableCollection<string> AddressOptions => owner.AddressOptions;

    public string PreviewUrl =>
        Port is null or < 1 ? "" : Core.Urls.UrlBuilder.ForBinding(ToBinding(), new Core.Urls.UrlContext(owner.MachineFqdn, null));

    partial void OnAddressChanged(string value) => OnPropertyChanged(nameof(PreviewUrl));

    partial void OnPortChanged(decimal? value) => OnPropertyChanged(nameof(PreviewUrl));

    partial void OnHostChanged(string? value) => OnPropertyChanged(nameof(PreviewUrl));

    public SiteBinding ToBinding() => new()
    {
        Address = string.IsNullOrWhiteSpace(Address) ? "0.0.0.0" : Address.Trim(),
        Port = (int)(Port ?? 0),
        Host = string.IsNullOrWhiteSpace(Host) ? null : Host.Trim().ToLowerInvariant(),
        Tls = Tls
    };

    [RelayCommand]
    private void Remove() => owner.RemoveBinding(this);

    [RelayCommand]
    private Task SuggestAsync() => owner.SuggestPortAsync(this);

    [RelayCommand]
    private Task CheckAsync() => owner.CheckBindingAsync(this);
}

/// <summary>Deep link row in the editor.</summary>
public sealed partial class LinkRowViewModel(SiteEditorViewModel owner) : ObservableObject
{
    [ObservableProperty]
    private string _label = "";

    [ObservableProperty]
    private string _path = "/";

    [RelayCommand]
    private void Remove() => owner.Links.Remove(this);
}

/// <summary>Create/edit site dialog.</summary>
public sealed partial class SiteEditorViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly SiteDefinition _original;

    public SiteEditorViewModel(MainViewModel main, SiteDefinition site, bool isNew)
    {
        _main = main;
        _original = site;
        IsNew = isNew;
        MachineFqdn = main.Status?.Fqdn;

        _name = site.Name;
        _description = site.Description;
        _color = string.IsNullOrWhiteSpace(site.Color) ? Core.Sites.SiteColors.Random : site.Color;
        _root = site.Root;
        _redirectTarget = site.RedirectTarget;
        _redirectPermanent = site.RedirectPermanent;
        _directoryBrowsing = site.Content.DirectoryBrowsing;
        _compression = site.Content.Compression;
        _liveReload = site.LiveReload;
        _autoReload = site.ReloadInterval > 0;
        _reloadInterval = site.ReloadInterval > 0 ? site.ReloadInterval : 5;
        _reloadDelayMs = site.ReloadDelayMs;
        _defaultDocuments = string.Join(Environment.NewLine, site.Content.DefaultDocuments);
        _hiddenPaths = string.Join(Environment.NewLine, site.Content.HiddenPaths);
        _allowNetworks = string.Join(Environment.NewLine, site.Access.AllowNetworks);
        _publicUrl = site.PublicUrl;
        _proxyTarget = site.ProxyTarget;
        _database = site.Database;
        _appCommand = site.App.Command;
        _keepAlive = site.App.KeepAlive;
        _restartOnChange = site.App.RestartOnChange;
        _pythonVersion = site.App.PythonVersion;
        _appEnvironment = string.Join(Environment.NewLine, site.App.Environment.Select(p => $"{p.Key}={p.Value}"));
        DetectedCommand = main.Status?.Sites.FirstOrDefault(s => s.Site.Id == site.Id)?.AppCommand;

        TypeOptions =
        [
            new OptionItem<SiteType>(SiteType.Static, Loc.T("TypeStaticLong")),
            new OptionItem<SiteType>(SiteType.Php, Loc.T("TypePhpLong")),
            new OptionItem<SiteType>(SiteType.Node, Loc.T("TypeNodeLong")),
            new OptionItem<SiteType>(SiteType.Python, Loc.T("TypePythonLong")),
            new OptionItem<SiteType>(SiteType.Proxy, Loc.T("TypeProxyLong")),
            new OptionItem<SiteType>(SiteType.Redirect, Loc.T("TypeRedirectLong"))
        ];
        _selectedType = TypeOptions.First(o => o.Value == site.Type);

        CharsetOptions =
        [
            new OptionItem<CharsetMode>(CharsetMode.Auto, Loc.T("CharsetAuto")),
            new OptionItem<CharsetMode>(CharsetMode.Utf8, Loc.T("CharsetUtf8")),
            new OptionItem<CharsetMode>(CharsetMode.Windows1252, Loc.T("CharsetWindows1252")),
            new OptionItem<CharsetMode>(CharsetMode.Iso88591, Loc.T("CharsetIso88591"))
        ];
        _selectedCharset = CharsetOptions.First(o => o.Value == site.Content.Charset);

        // PHP version: default module first, then the additional versions; a missing one stays selectable.
        var status = main.Status;
        var phpOptions = new List<OptionItem<string?>>
        {
            new(null, status?.PhpDefaultVersion is { } defaultVersion ? Loc.F("PhpVersionDefault", defaultVersion) : Loc.T("PhpVersionDefaultNone"))
        };
        phpOptions.AddRange((status?.PhpVersions ?? []).Where(v => v != status?.PhpDefaultVersion).Select(v => new OptionItem<string?>(v, "PHP " + v)));
        if (site.PhpVersion is { } wanted && phpOptions.All(o => o.Value != wanted) && wanted != status?.PhpDefaultVersion)
        {
            phpOptions.Add(new OptionItem<string?>(wanted, Loc.F("PhpVersionMissing", wanted)));
        }

        PhpVersionOptions = phpOptions;
        _selectedPhpVersion = phpOptions.FirstOrDefault(o => o.Value == site.PhpVersion) ?? phpOptions[0];
        _xdebug = site.Xdebug;
        XdebugInstalled = status?.XdebugInstalled == true;

        AddressOptions = ["0.0.0.0", "127.0.0.1"];
        foreach (var binding in site.Bindings)
        {
            AddAddressOption(binding.Address);
            Bindings.Add(new BindingRowViewModel(this)
            {
                Address = binding.Address,
                Port = binding.Port > 0 ? binding.Port : null,
                Host = binding.Host,
                Tls = binding.Tls
            });
        }

        foreach (var link in site.Links)
        {
            Links.Add(new LinkRowViewModel(this) { Label = link.Label, Path = link.Path });
        }

        // New site in the www folder: the folder follows the name until the path is changed by hand.
        _www = main.Status?.WwwRoot;
        _rootFollowsName = isNew && !string.IsNullOrEmpty(_www) && !string.IsNullOrEmpty(site.Root)
            && string.Equals(Path.GetDirectoryName(site.Root.TrimEnd('\\', '/')), _www.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

        MarkSelectedColor();
        _ = InitializeAsync();
    }

    /// <summary>Closes the dialog; null = cancelled.</summary>
    public event Action<SaveSiteResult?>? CloseRequested;

    public bool IsNew { get; }

    public string? MachineFqdn { get; }

    public string WindowTitle => IsNew ? Loc.T("EditorTitleNew") : Loc.F("EditorTitleEdit", _original.Name);

    /// <summary>"Random" first (default for new sites), then the palette.</summary>
    public IReadOnlyList<ColorOption> ColorOptions { get; } =
        new[] { Core.Sites.SiteColors.Random }.Concat(Core.Sites.SiteColors.Palette)
            .Select(hex => new ColorOption(hex, Core.Sites.SiteColors.IsRandom(hex) ? Res.Brush("BrushSurfaceRaised") : Res.ParseBrush(hex)))
            .ToList();

    public IReadOnlyList<OptionItem<SiteType>> TypeOptions { get; }

    public IReadOnlyList<OptionItem<CharsetMode>> CharsetOptions { get; }

    public IReadOnlyList<OptionItem<string?>> PhpVersionOptions { get; }

    [ObservableProperty]
    private OptionItem<string?> _selectedPhpVersion;

    /// <summary>Requests of this site run through a php-cgi with Xdebug.</summary>
    [ObservableProperty]
    private bool _xdebug;

    public bool XdebugInstalled { get; }

    public ObservableCollection<string> AddressOptions { get; }

    public ObservableCollection<BindingRowViewModel> Bindings { get; } = [];

    public ObservableCollection<LinkRowViewModel> Links { get; } = [];

    public ObservableCollection<IssueViewModel> Issues { get; } = [];

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string? _description;

    [ObservableProperty]
    private string _color;

    [ObservableProperty]
    private OptionItem<SiteType> _selectedType;

    [ObservableProperty]
    private string _root;

    [ObservableProperty]
    private string? _redirectTarget;

    [ObservableProperty]
    private bool _redirectPermanent;

    [ObservableProperty]
    private OptionItem<CharsetMode> _selectedCharset;

    [ObservableProperty]
    private bool _directoryBrowsing;

    [ObservableProperty]
    private bool _compression;

    [ObservableProperty]
    private bool _liveReload;

    /// <summary>Reload every <see cref="ReloadInterval"/> seconds.</summary>
    [ObservableProperty]
    private bool _autoReload;

    [ObservableProperty]
    private decimal? _reloadInterval;

    [ObservableProperty]
    private decimal? _reloadDelayMs;

    [ObservableProperty]
    private string _defaultDocuments;

    [ObservableProperty]
    private string _hiddenPaths;

    [ObservableProperty]
    private string _allowNetworks;

    [ObservableProperty]
    private string? _publicUrl;

    [ObservableProperty]
    private string? _proxyTarget;

    [ObservableProperty]
    private string? _database;

    [ObservableProperty]
    private string? _appCommand;

    [ObservableProperty]
    private bool _keepAlive;

    [ObservableProperty]
    private bool _restartOnChange;

    [ObservableProperty]
    private string? _pythonVersion;

    [ObservableProperty]
    private string _appEnvironment;

    /// <summary>Command WEBit currently runs for this site (shown as watermark of the command field).</summary>
    public string? DetectedCommand { get; }

    public string CommandWatermark => DetectedCommand ?? Loc.T("WatermarkAppCommand");

    public string AppPortText => _original.App.Port > 0 ? $"127.0.0.1:{_original.App.Port}" : Loc.T("AppPortOnSave");

    [ObservableProperty]
    private string? _analysisText;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorText;

    public bool IsStatic => SelectedType.Value is SiteType.Static or SiteType.Php;

    public bool IsRedirect => SelectedType.Value == SiteType.Redirect;

    /// <summary>Folder field: static, PHP, Node.js and Python sites.</summary>
    public bool HasRoot => SelectedType.Value is SiteType.Static or SiteType.Php or SiteType.Node or SiteType.Python;

    public bool IsApp => SelectedType.Value is SiteType.Node or SiteType.Python;

    public bool IsPython => SelectedType.Value == SiteType.Python;

    public bool IsPhp => SelectedType.Value == SiteType.Php;

    public bool IsProxy => SelectedType.Value == SiteType.Proxy;

    public bool HasAnalysis => !string.IsNullOrEmpty(AnalysisText);

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public bool HasIssues => Issues.Count > 0;

    public IBrush ColorBrush => Core.Sites.SiteColors.IsRandom(Color) ? Res.Brush("BrushAccent") : Res.ParseBrush(Color);

    partial void OnSelectedTypeChanged(OptionItem<SiteType> value)
    {
        OnPropertyChanged(nameof(IsStatic));
        OnPropertyChanged(nameof(IsRedirect));
        OnPropertyChanged(nameof(HasRoot));
        OnPropertyChanged(nameof(IsApp));
        OnPropertyChanged(nameof(IsPython));
        OnPropertyChanged(nameof(IsPhp));
        OnPropertyChanged(nameof(IsProxy));
    }

    private readonly string? _www;
    private bool _rootFollowsName;
    private bool _syncingRoot;

    partial void OnNameChanged(string value)
    {
        if (_rootFollowsName && _www is not null)
        {
            _syncingRoot = true;
            Root = Path.Combine(_www, Core.Sites.SiteFolders.FromName(value));
            _syncingRoot = false;
        }
    }

    partial void OnRootChanged(string value)
    {
        if (!_syncingRoot)
        {
            _rootFollowsName = false;
        }
    }

    /// <summary>Folder named after the site exists already: use it, rename or cancel. False = cancelled.</summary>
    private async Task<bool> ResolveFolderAsync(SiteDefinition site)
    {
        if (!IsNew || !_rootFollowsName || _www is null || !site.HasRoot)
        {
            return true;
        }

        while (Directory.Exists(site.Root))
        {
            var folder = Path.GetFileName(site.Root.TrimEnd('\\', '/'));
            var (ok, newName) = await _main.Host.FolderExistsAsync(
                Loc.T("DlgFolderExistsTitle"),
                Loc.F("DlgFolderExistsText", folder),
                Core.Sites.SiteFolders.Suggest(_www, folder));
            if (!ok)
            {
                return false;
            }

            if (newName is null)
            {
                // Use the existing folder.
                return true;
            }

            site.Root = Path.Combine(_www, Core.Sites.SiteFolders.FromName(newName));
            _syncingRoot = true;
            Root = site.Root;
            _syncingRoot = false;
        }

        return true;
    }

    partial void OnColorChanged(string value)
    {
        OnPropertyChanged(nameof(ColorBrush));
        MarkSelectedColor();
    }

    private void MarkSelectedColor()
    {
        foreach (var option in ColorOptions)
        {
            option.IsSelected = string.Equals(option.Hex, Color, StringComparison.OrdinalIgnoreCase);
        }
    }

    partial void OnAnalysisTextChanged(string? value) => OnPropertyChanged(nameof(HasAnalysis));

    partial void OnErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasError));

    private async Task InitializeAsync()
    {
        try
        {
            var info = await _main.Connection.RequestAsync<NetworkInfo>(Methods.GetNetworkInfo);
            if (info is not null)
            {
                foreach (var address in info.Addresses)
                {
                    AddAddressOption(address.Address);
                }
            }

            // New sites: suggest a free port.
            foreach (var row in Bindings.Where(b => b.Port is null or < 1).ToList())
            {
                await SuggestPortAsync(row);
            }
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    private void AddAddressOption(string? address)
    {
        if (!string.IsNullOrWhiteSpace(address) && !AddressOptions.Contains(address))
        {
            // Specific addresses before 0.0.0.0/127.0.0.1.
            AddressOptions.Insert(0, address);
        }
    }

    [RelayCommand]
    private void PickColor(string color) => Color = color;

    [RelayCommand]
    private async Task BrowseRootAsync()
    {
        var path = await _main.Host.PickFolderAsync(Loc.T("PickRootTitle"), Directory.Exists(Root) ? Root : _main.Status?.WwwRoot);
        if (!string.IsNullOrEmpty(path))
        {
            Root = path;
            await AnalyzeAsync();
        }
    }

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        if (string.IsNullOrWhiteSpace(Root))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var analysis = await _main.Connection.RequestAsync<FolderAnalysis>(
                Methods.AnalyzeFolder,
                new PathRequest { Path = Root, DefaultDocuments = SplitLines(DefaultDocuments) },
                TimeSpan.FromMinutes(3));

            if (analysis is null)
            {
                return;
            }

            if (!analysis.Exists)
            {
                AnalysisText = Loc.T("AnalysisMissing");
                return;
            }

            var parts = new List<string>
            {
                Loc.F("AnalysisFiles", analysis.FileCount, analysis.HtmlCount, Core.Diagnostics.LogReader.FormatBytes(analysis.TotalBytes))
            };

            parts.Add(analysis.StartDocument is null ? Loc.T("AnalysisNoStart") : Loc.F("AnalysisStart", analysis.StartDocument));
            if (!string.IsNullOrEmpty(analysis.Generator))
            {
                parts.Add(Loc.F("AnalysisGenerator", analysis.Generator));
            }

            if (analysis.IsFrontPage)
            {
                parts.Add(Loc.T("AnalysisFrontPage"));
            }

            parts.Add(analysis.MetaCharset is null ? Loc.T("AnalysisNoCharset") : Loc.F("AnalysisCharset", analysis.MetaCharset));
            if (analysis.AccessDenied)
            {
                parts.Add(Loc.T("AnalysisAccessDenied"));
            }

            AnalysisText = string.Join(Environment.NewLine, parts);
            SelectedCharset = CharsetOptions.First(o => o.Value == analysis.SuggestedCharset);
            if (IsNew && SelectedType.Value is not (SiteType.Redirect or SiteType.Proxy))
            {
                SelectedType = TypeOptions.First(o => o.Value == analysis.SuggestedType);
            }

            if (IsNew && (string.IsNullOrWhiteSpace(Name) || Name == Loc.T("DefaultSiteName")))
            {
                Name = System.IO.Path.GetFileName(Root.TrimEnd('\\', '/'));
            }
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void AddBinding()
    {
        var row = new BindingRowViewModel(this)
        {
            Address = Bindings.LastOrDefault()?.Address ?? AddressOptions.FirstOrDefault() ?? "0.0.0.0"
        };
        Bindings.Add(row);
        _ = SuggestPortAsync(row);
    }

    public void RemoveBinding(BindingRowViewModel row) => Bindings.Remove(row);

    public async Task SuggestPortAsync(BindingRowViewModel row)
    {
        try
        {
            var reserved = Bindings.Where(b => b != row && b.Port is > 0).Select(b => (int)b.Port!.Value).ToList();
            var response = await _main.Connection.RequestAsync<SuggestPortResponse>(
                Methods.SuggestPort,
                new SuggestPortRequest { Address = row.Address, ExcludeSiteId = string.IsNullOrEmpty(_original.Id) ? null : _original.Id, Reserved = reserved });

            if (response?.Port is { } port)
            {
                row.Port = port;
                await CheckBindingAsync(row);
            }
            else
            {
                row.CheckText = Loc.T("NoFreePort");
                row.CheckBrush = Res.LevelBrush(IssueLevel.Warning);
                row.CheckIcon = Res.LevelIcon(IssueLevel.Warning);
            }
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    public async Task CheckBindingAsync(BindingRowViewModel row)
    {
        if (row.Port is null or < 1)
        {
            return;
        }

        try
        {
            var issues = await _main.Connection.RequestAsync<List<ValidationIssue>>(
                Methods.CheckEndpoint,
                new EndpointCheckRequest { Address = row.Address, Port = (int)row.Port.Value, ExcludeSiteId = string.IsNullOrEmpty(_original.Id) ? null : _original.Id }) ?? [];

            var worst = issues.OrderByDescending(i => i.Level).FirstOrDefault();
            if (worst is null)
            {
                row.CheckText = null;
                return;
            }

            row.CheckText = Loc.Message(worst.Message);
            row.CheckBrush = Res.LevelBrush(worst.Level);
            row.CheckIcon = Res.LevelIcon(worst.Level);
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    [RelayCommand]
    private void AddLink() => Links.Add(new LinkRowViewModel(this) { Label = "", Path = "/" });

    [RelayCommand]
    private async Task CheckAsync()
    {
        ErrorText = null;
        IsBusy = true;
        try
        {
            var issues = await _main.Connection.RequestAsync<List<ValidationIssue>>(Methods.CheckSite, Build()) ?? [];
            ShowIssues(issues);
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ShowIssues(IEnumerable<ValidationIssue> issues)
    {
        Issues.Clear();
        foreach (var issue in issues.Where(i => i.Level != IssueLevel.Ok).OrderByDescending(i => i.Level))
        {
            Issues.Add(new IssueViewModel(issue));
        }

        if (Issues.Count == 0)
        {
            Issues.Add(new IssueViewModel(ValidationIssue.Ok("AllChecksOk")));
        }

        OnPropertyChanged(nameof(HasIssues));
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        ErrorText = null;
        var site = Build();
        var formatIssues = Core.Sites.SiteValidator.ValidateFormat(site);
        if (Core.Sites.SiteValidator.HasErrors(formatIssues))
        {
            ShowIssues(formatIssues);
            ErrorText = Loc.T("EditorFixErrors");
            return;
        }

        if (!await ResolveFolderAsync(site))
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (site.HasRoot)
            {
                await SitesViewModel.EnsureServiceAccessAsync(_main, site.Root, write: site.IsApp);
            }

            var result = await _main.Connection.RequestAsync<SaveSiteResult>(Methods.SaveSite, site, TimeSpan.FromMinutes(2));
            if (result is null)
            {
                ErrorText = Loc.T("ErrNotConnected");
                return;
            }

            CloseRequested?.Invoke(result);
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);

    public SiteDefinition Build()
    {
        var site = _original.Clone();
        site.Name = Name.Trim();
        site.Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim();
        site.Color = Color;
        site.Type = SelectedType.Value;
        site.Root = Root.Trim();
        site.RedirectTarget = string.IsNullOrWhiteSpace(RedirectTarget) ? null : RedirectTarget.Trim();
        site.RedirectPermanent = RedirectPermanent;
        site.Bindings = Bindings.Select(b => b.ToBinding()).ToList();
        site.Content.Charset = SelectedCharset.Value;
        site.Content.DirectoryBrowsing = DirectoryBrowsing;
        site.Content.Compression = Compression;
        site.LiveReload = LiveReload;
        site.ReloadInterval = AutoReload ? (int)(ReloadInterval ?? 5) : 0;
        site.ReloadDelayMs = (int)(ReloadDelayMs ?? 0);
        site.Content.DefaultDocuments = SplitLines(DefaultDocuments);
        site.Content.HiddenPaths = SplitLines(HiddenPaths);
        site.Access.AllowNetworks = SplitLines(AllowNetworks);
        site.PublicUrl = string.IsNullOrWhiteSpace(PublicUrl) ? null : PublicUrl.Trim();
        site.ProxyTarget = string.IsNullOrWhiteSpace(ProxyTarget) ? null : ProxyTarget.Trim();
        site.PhpVersion = SelectedPhpVersion.Value;
        site.Xdebug = Xdebug;
        site.Database = string.IsNullOrWhiteSpace(Database) ? null : Database.Trim();
        site.App.Command = string.IsNullOrWhiteSpace(AppCommand) ? null : AppCommand.Trim();
        site.App.KeepAlive = KeepAlive;
        site.App.RestartOnChange = RestartOnChange;
        site.App.PythonVersion = string.IsNullOrWhiteSpace(PythonVersion) ? null : PythonVersion.Trim();
        site.App.Environment = ParseEnvironment(AppEnvironment);
        site.Links = Links
            .Where(l => !string.IsNullOrWhiteSpace(l.Path))
            .Select(l => new SiteLink { Label = l.Label.Trim(), Path = l.Path.Trim() })
            .ToList();
        if (IsNew)
        {
            site.Enabled = true;
        }

        return site;
    }

    /// <summary>KEY=VALUE per line; lines without '=' or starting with '#' are ignored.</summary>
    private static Dictionary<string, string> ParseEnvironment(string? text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf('=');
            if (line.StartsWith('#') || separator < 1)
            {
                continue;
            }

            result[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        return result;
    }

    private static List<string> SplitLines(string? text) =>
        (text ?? "")
            .Split(['\r', '\n', ';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
