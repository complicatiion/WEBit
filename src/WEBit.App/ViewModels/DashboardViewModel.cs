using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using WEBit.App.Localization;
using WEBit.Contracts;

namespace WEBit.App.ViewModels;

public sealed record ProblemItem(string Title, string Text, IBrush Brush, Geometry? Icon)
{
    public string Key => Title + "\n" + Text;
}

/// <summary>Home page (Laragon style): actions, services with switches, key figures, notices.</summary>
public sealed partial class DashboardViewModel(MainViewModel main) : PageViewModel(main)
{
    public override string TitleKey => "NavDashboard";

    public ObservableCollection<ProblemItem> Problems { get; } = [];

    public ObservableCollection<ModuleCardViewModel> Services { get; } = [];

    public int SitesTotal => Main.Status?.Sites.Count ?? 0;

    public int SitesRunning => Main.Status?.Sites.Count(s => s.State == SiteRunState.Running) ?? 0;

    public int SitesStopped => Main.Status?.Sites.Count(s => s.State == SiteRunState.Stopped) ?? 0;

    public int SitesError => Main.Status?.Sites.Count(s => s.State == SiteRunState.Error) ?? 0;

    public string ModeText => Main.Status is null ? "–" : Main.Status.IsWindowsService ? Loc.T("ModeService") : Loc.T("ModeDesktop");

    public string UptimeText
    {
        get
        {
            if (Main.Status is null)
            {
                return "–";
            }

            var span = DateTimeOffset.Now - Main.Status.StartedAt;
            return span.TotalDays >= 1
                ? Loc.F("UptimeDays", (int)span.TotalDays, span.Hours)
                : Loc.F("UptimeHours", (int)span.TotalHours, span.Minutes);
        }
    }

    public string MachineText => Main.Status?.Fqdn ?? Main.Status?.MachineName ?? "–";

    public string WwwRoot => Main.Status?.WwwRoot ?? "–";

    public bool HasNoProblems => Problems.Count == 0;

    public override void OnStatus(ServiceStatus status) => Refresh();

    public override void OnActivated() => Refresh();

    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        Refresh();
    }

    private void Refresh()
    {
        // Module cards are shared with the modules page so switches stay in sync.
        var active = Main.Modules.Active.ToList();
        if (!active.SequenceEqual(Services))
        {
            Services.Clear();
            foreach (var card in active)
            {
                Services.Add(card);
            }
        }

        Problems.Clear();
        var current = new List<ProblemItem>();
        if (Main.Status is { } status)
        {
            foreach (var module in status.Modules.Where(m => m.Enabled && m.State is ModuleState.Error or ModuleState.NotInstalled))
            {
                current.Add(new ProblemItem(module.Name, Loc.Message(module.Message) is { Length: > 0 } msg ? msg : Loc.T("StateError"), Res.Brush("BrushDanger"), Res.Icon("IconCloseCircle")));
            }

            foreach (var site in status.Sites.Where(s => s.State == SiteRunState.Error))
            {
                current.Add(new ProblemItem(site.Site.Name, Loc.Message(site.Message), Res.Brush("BrushWarning"), Res.Icon("IconAlertCircle")));
            }
        }

        // Dismissed notices stay hidden until their text changes (also across short recoveries, e.g. crash loops).
        foreach (var problem in current.Where(p => !_dismissed.Contains(p.Key)))
        {
            Problems.Add(problem);
        }

        OnPropertyChanged(string.Empty);
    }

    private readonly HashSet<string> _dismissed = new(StringComparer.Ordinal);

    [RelayCommand]
    private void Dismiss(ProblemItem? item)
    {
        if (item is not null)
        {
            _dismissed.Add(item.Key);
            Refresh();
        }
    }

    [RelayCommand]
    private void DismissAll()
    {
        foreach (var item in Problems)
        {
            _dismissed.Add(item.Key);
        }

        Refresh();
    }

    [RelayCommand]
    private Task NewSiteAsync() => Main.Sites.NewSiteAsync();

    [RelayCommand]
    private Task StartAllAsync() => Main.Sites.StartAllCommand.ExecuteAsync(null);

    [RelayCommand]
    private Task StopAllAsync() => Main.Sites.StopAllCommand.ExecuteAsync(null);

    [RelayCommand]
    private void OpenUrls() => Main.NavigateTo(Main.Urls);

    [RelayCommand]
    private void OpenLogs() => Main.NavigateTo(Main.Logs);

    [RelayCommand]
    private void OpenModules() => Main.NavigateTo(Main.Modules);
}
