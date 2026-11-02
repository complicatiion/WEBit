using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WEBit.App.Localization;
using WEBit.App.Services;
using WEBit.Contracts;

namespace WEBit.App.ViewModels;

public sealed record LogSource(string Key, string Title)
{
    public override string ToString() => Title;
}

/// <summary>Logs page: service, engine, module and site access logs.</summary>
public sealed partial class LogsViewModel : PageViewModel
{
    private readonly DispatcherTimer _timer;
    private string? _pendingSource;
    private bool _active;

    public LogsViewModel(MainViewModel main) : base(main)
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Clamp(main.Ui.LogRefreshSeconds, 1, 300)) };
        _timer.Tick += async (_, _) => await LoadAsync();
        _selectedLines = main.Ui.LogLines is > 0 ? main.Ui.LogLines : 300;
        RebuildSources();
    }

    public override string TitleKey => "NavLogs";


    public ObservableCollection<LogSource> Sources { get; } = [];

    public IReadOnlyList<int> LineOptions { get; } = [100, 300, 1000, 3000];

    [ObservableProperty]
    private LogSource? _selectedSource;

    [ObservableProperty]
    private int _selectedLines;

    [ObservableProperty]
    private bool _autoRefresh = true;

    [ObservableProperty]
    private string _text = "";

    [ObservableProperty]
    private string? _filePath;

    partial void OnSelectedSourceChanged(LogSource? oldValue, LogSource? newValue)
    {
        if (newValue is null && !_rebuilding && oldValue is not null && Sources.Contains(oldValue))
        {
            // ComboBox pushes null while rebuilding; keep the previous selection.
            SelectedSource = oldValue;
            return;
        }

        _ = LoadAsync();
    }

    private bool _rebuilding;

    partial void OnSelectedLinesChanged(int value)
    {
        Main.Ui.LogLines = value;
        Main.Ui.Save();
        _ = LoadAsync();
    }

    partial void OnAutoRefreshChanged(bool value) => UpdateTimer();

    public void ShowSource(string key)
    {
        var source = Sources.FirstOrDefault(s => s.Key == key);
        if (source is null)
        {
            _pendingSource = key;
            return;
        }

        SelectedSource = source;
    }

    public override void OnStatus(ServiceStatus status) => RebuildSources();

    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        RebuildSources();
    }

    /// <summary>Settings → Logs changed the refresh interval.</summary>
    public void SetRefreshInterval(int seconds) => _timer.Interval = TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 300));

    public override void OnActivated()
    {
        _active = true;
        UpdateTimer();
        _ = LoadAsync();
    }

    public override void OnDeactivated()
    {
        _active = false;
        UpdateTimer();
    }

    private void UpdateTimer()
    {
        if (_active && AutoRefresh)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    private void RebuildSources()
    {
        var selectedKey = _pendingSource ?? SelectedSource?.Key ?? "service";
        var list = new List<LogSource>
        {
            new("service", Loc.T("LogService")),
            new("engine", Loc.T("LogEngine"))
        };

        if (Main.Status is { } status)
        {
            list.AddRange(status.Modules
                .Where(m => m.Id is not (WEBit.Core.Models.ModuleIds.WebServer or WEBit.Core.Models.ModuleIds.Node or WEBit.Core.Models.ModuleIds.Python
                                    or WEBit.Core.Models.ModuleIds.Composer or WEBit.Core.Models.ModuleIds.WpCli or WEBit.Core.Models.ModuleIds.Adminer
                                    or WEBit.Core.Models.ModuleIds.Xdebug)
                            && m.State is not (ModuleState.Planned or ModuleState.NotInstalled))
                .Select(m => new LogSource("module:" + m.Id, Loc.F("LogModule", m.Name))));
            list.AddRange(status.Sites.Where(s => s.Site.IsApp).Select(s => new LogSource("app:" + s.Site.Id, Loc.F("LogApp", s.Site.Name))));
            list.AddRange(status.Sites.Select(s => new LogSource("site:" + s.Site.Id, Loc.F("LogSite", s.Site.Name))));
        }

        if (Sources.Select(s => s.Key + s.Title).SequenceEqual(list.Select(s => s.Key + s.Title)))
        {
            if (_pendingSource is not null && Sources.FirstOrDefault(s => s.Key == _pendingSource) is { } pending)
            {
                _pendingSource = null;
                SelectedSource = pending;
            }

            return;
        }

        _rebuilding = true;
        Sources.Clear();
        foreach (var source in list)
        {
            Sources.Add(source);
        }

        _rebuilding = false;

        var match = Sources.FirstOrDefault(s => s.Key == selectedKey);
        if (match is not null)
        {
            _pendingSource = null;
        }

        SelectedSource = match ?? Sources[0];
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (!Connection.IsConnected || SelectedSource is null)
        {
            return;
        }

        try
        {
            var response = await Connection.RequestAsync<LogResponse>(
                Methods.GetLog,
                new LogRequest { Source = SelectedSource.Key, Lines = SelectedLines });

            FilePath = response?.FilePath;
            var text = response is null || response.Lines.Count == 0
                ? Loc.T("LogEmpty")
                : string.Join(Environment.NewLine, response.Lines);

            if (text != Text)
            {
                Text = text;
            }
        }
        catch (Exception ex)
        {
            Text = ex.Message;
        }
    }

    [RelayCommand]
    private Task CopyAllAsync() => Main.CopyAsync(Text);

    [RelayCommand]
    private void OpenFolder() => AppEnvironment.OpenFolder(FilePath is not null && File.Exists(FilePath) ? FilePath : Main.Status?.LogsDir);
}
