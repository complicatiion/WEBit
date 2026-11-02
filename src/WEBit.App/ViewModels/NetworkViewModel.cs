using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WEBit.App.Localization;
using WEBit.Contracts;
using WEBit.Core.Models;
using WEBit.Core.Net;

namespace WEBit.App.ViewModels;

/// <summary>Port usage row.</summary>
public sealed record ListenerRow(string Endpoint, int Port, string Process, int Pid, bool IsEngine, string? AvoidReason)
{
    public string PidText => Pid.ToString();
}

/// <summary>Network page: addresses, port ranges, port usage, port check.</summary>
public sealed partial class NetworkViewModel(MainViewModel main) : PageViewModel(main)
{
    private NetworkInfo? _info;
    private WebitSettings? _settings;

    public override string TitleKey => "NavNetwork";


    public ObservableCollection<HostAddress> Addresses { get; } = [];

    public ObservableCollection<string> AddressOptions { get; } = [];

    public ObservableCollection<ListenerRow> Listeners { get; } = [];

    public ObservableCollection<IssueViewModel> CheckResults { get; } = [];

    [ObservableProperty]
    private string? _listenerFilter;

    [ObservableProperty]
    private bool _onlyRelevant = true;

    [ObservableProperty]
    private string? _checkAddress;

    [ObservableProperty]
    private decimal? _checkPort = 47100;

    [ObservableProperty]
    private bool _isBusy;

    partial void OnListenerFilterChanged(string? value) => ApplyListenerFilter();

    partial void OnOnlyRelevantChanged(bool value) => ApplyListenerFilter();

    public string MachineText => _info is null ? "–" : _info.Fqdn ?? _info.MachineName;

    public string SiteRangeText => _settings is null ? "–" : $"{_settings.SitePortStart}–{_settings.SitePortEnd}";

    public string InternalRangeText => _settings is null ? "–" : $"{_settings.InternalPortStart}–{_settings.InternalPortEnd}";

    public string AdminPortText => _settings is null ? "–" : $"127.0.0.1:{_settings.EngineAdminPort}";

    public string ExcludedText => _info is null || _info.ExcludedRanges.Count == 0
        ? Loc.T("NoExcludedRanges")
        : string.Join(", ", _info.ExcludedRanges.Select(r => r.Start == r.End ? r.Start.ToString() : $"{r.Start}–{r.End}"));

    public bool ListenerInfoMissing => _info is { ListenerInfoAvailable: false };

    public override void OnActivated() => _ = RefreshAsync();

    public override void OnConnected()
    {
        if (ReferenceEquals(Main.CurrentPage, this))
        {
            _ = RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (!Connection.IsConnected)
        {
            return;
        }

        IsBusy = true;
        try
        {
            _info = await Connection.RequestAsync<NetworkInfo>(Methods.GetNetworkInfo);
            _settings = await Connection.RequestAsync<WebitSettings>(Methods.GetSettings);

            var keepAddress = CheckAddress;
            Addresses.Clear();
            AddressOptions.Clear();
            foreach (var address in _info?.Addresses ?? [])
            {
                Addresses.Add(address);
                AddressOptions.Add(address.Address);
            }

            AddressOptions.Add("0.0.0.0");
            AddressOptions.Add("127.0.0.1");
            CheckAddress = keepAddress is not null && AddressOptions.Contains(keepAddress) ? keepAddress : AddressOptions.FirstOrDefault();

            ApplyListenerFilter();
            OnPropertyChanged(nameof(MachineText));
            OnPropertyChanged(nameof(SiteRangeText));
            OnPropertyChanged(nameof(InternalRangeText));
            OnPropertyChanged(nameof(AdminPortText));
            OnPropertyChanged(nameof(ExcludedText));
            OnPropertyChanged(nameof(ListenerInfoMissing));
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

    private void ApplyListenerFilter()
    {
        Listeners.Clear();
        if (_info is null)
        {
            return;
        }

        var filter = ListenerFilter?.Trim();
        foreach (var l in _info.Listeners)
        {
            var relevant = l.IsEngine
                           || l.Port is 80 or 443
                           || (_settings is not null && ((l.Port >= _settings.SitePortStart && l.Port <= _settings.SitePortEnd) ||
                                                         (l.Port >= _settings.InternalPortStart && l.Port <= _settings.EngineAdminPort)))
                           || (l.Port is >= 1024 and < 49152 && PortPolicy.AvoidReason(l.Port) is not null);

            if (OnlyRelevant && !relevant)
            {
                continue;
            }

            var endpoint = Endpoint.Display(l.Address == "0.0.0.0" ? null : l.Address, l.Port);
            var process = l.ProcessName ?? "?";
            if (!string.IsNullOrEmpty(filter) &&
                !endpoint.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !process.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Listeners.Add(new ListenerRow(endpoint, l.Port, l.IsEngine ? process + " (WEBit)" : process, l.ProcessId, l.IsEngine, PortPolicy.AvoidReason(l.Port)?.Reason));
        }
    }

    [RelayCommand]
    private async Task CheckPortNowAsync()
    {
        if (string.IsNullOrWhiteSpace(CheckAddress) || CheckPort is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var issues = await Connection.RequestAsync<List<ValidationIssue>>(
                Methods.CheckEndpoint,
                new EndpointCheckRequest { Address = CheckAddress, Port = (int)CheckPort.Value }) ?? [];
            CheckResults.Clear();
            foreach (var issue in issues.OrderByDescending(i => i.Level))
            {
                CheckResults.Add(new IssueViewModel(issue));
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
    private async Task SuggestPortNowAsync()
    {
        try
        {
            var response = await Connection.RequestAsync<SuggestPortResponse>(Methods.SuggestPort, new SuggestPortRequest { Address = CheckAddress });
            if (response?.Port is { } port)
            {
                CheckPort = port;
                await CheckPortNowAsync();
            }
            else
            {
                Main.ShowToast(Loc.T("NoFreePort"), isError: true);
            }
        }
        catch (Exception ex)
        {
            Main.ShowError(ex);
        }
    }

    [RelayCommand]
    private Task CopyAddressAsync(string? address) => Main.CopyAsync(address);
}
