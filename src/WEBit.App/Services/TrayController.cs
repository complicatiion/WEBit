using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using WEBit.App.Localization;
using WEBit.App.ViewModels;
using WEBit.App.Views;
using WEBit.Contracts;

namespace WEBit.App.Services;

/// <summary>
/// System tray: WEBit logo with status dot, quick panel on left click, full control menu on right click.
/// Every menu entry has an icon.
/// </summary>
public sealed class TrayController : IDisposable
{
    private static readonly Color IconColor = Color.Parse("#F2F2ED");

    // Native tray menu items have no background: the icons carry the colors (start green, stop red, new accent).
    private static readonly Color StartColor = Color.Parse("#3CC38A");
    private static readonly Color StopColor = Color.Parse("#E5606B");
    private static readonly Color AccentColor = Color.Parse("#1A9BC6");
    private static readonly Color RunningColor = Color.Parse("#7CD5C7");
    private static readonly Color ErrorColor = Color.Parse("#E5606B");

    private readonly MainViewModel _main;
    private readonly Action _showMainWindow;
    private readonly Action _exit;
    private readonly TrayIcon _trayIcon;
    private readonly Dictionary<string, WindowIcon> _icons = new();
    private TrayFlyoutWindow? _flyout;
    private string _menuSignature = "";

    public TrayController(Application app, MainViewModel main, Action showMainWindow, Action exit)
    {
        _main = main;
        _showMainWindow = showMainWindow;
        _exit = exit;

        _trayIcon = new TrayIcon
        {
            Icon = LoadIcon("offline"),
            ToolTipText = "WEBit",
            IsVisible = true
        };
        _trayIcon.Clicked += (_, _) => ToggleFlyout();
        TrayIcon.SetIcons(app, new TrayIcons { _trayIcon });

        _main.StatusUpdated += Update;
        Update();
    }

    private WindowIcon LoadIcon(string state)
    {
        if (_icons.TryGetValue(state, out var icon))
        {
            return icon;
        }

        using var stream = AssetLoader.Open(new Uri($"avares://WEBit/Assets/tray-{state}.ico"));
        icon = new WindowIcon(stream);
        _icons[state] = icon;
        return icon;
    }

    private void Update()
    {
        var status = _main.Status;
        var connected = _main.IsConnected;
        var running = status?.Sites.Count(s => s.State == SiteRunState.Running) ?? 0;
        var total = status?.Sites.Count ?? 0;
        var errors = (status?.Sites.Count(s => s.State == SiteRunState.Error) ?? 0)
                     + (status?.Modules.Count(m => m.Enabled && m.State == ModuleState.Error) ?? 0);

        var state = !connected || status is null ? "offline"
            : status.Engine.State != EngineState.Running || errors > 0 ? "error"
            : running < status.Sites.Count(s => s.Site.Enabled) ? "warning"
            : "ok";

        _trayIcon.Icon = LoadIcon(state);
        _trayIcon.ToolTipText = connected ? Loc.F("TrayTooltip", running, total) : "WEBit – " + _main.StatusPillText;

        // Rebuild only when content or language changed.
        var signature = string.Join("|",
            Loc.Language,
            connected,
            _main.IsDesktopMode,
            status?.Engine.State,
            string.Join(",", status?.Sites.Select(s => $"{s.Site.Id}:{s.Site.Name}:{s.State}:{s.Site.Enabled}:{s.PrimaryUrl}") ?? []),
            string.Join(",", status?.Modules.Select(m => $"{m.Id}:{m.State}:{m.Enabled}:{m.Download?.State}") ?? []));
        if (signature != _menuSignature)
        {
            _menuSignature = signature;
            _trayIcon.Menu = BuildMenu(status, connected, running, total);
        }
    }

    private NativeMenu BuildMenu(ServiceStatus? status, bool connected, int running, int total)
    {
        var menu = new NativeMenu();

        var header = connected ? Loc.F("TrayHeader", running, total) : "WEBit – " + _main.StatusPillText;
        menu.Add(Item(header, connected && status?.Engine.State == EngineState.Running ? "IconCheckCircle" : "IconAlertCircle", _showMainWindow, AccentColor));
        menu.Add(new NativeMenuItemSeparator());

        menu.Add(Item(Loc.T("TrayOpenManager"), "IconWindow", _showMainWindow));
        menu.Add(Item(Loc.T("NavDashboard"), "IconDashboard", () => Navigate(_main.Dashboard)));
        menu.Add(new NativeMenuItemSeparator());

        if (connected)
        {
            menu.Add(Item(Loc.T("BtnStartAll"), "IconPlayCircle", () => _ = _main.Sites.StartAllCommand.ExecuteAsync(null), StartColor));
            menu.Add(Item(Loc.T("BtnStopAll"), "IconStopCircle", () =>
            {
                _showMainWindow();
                _ = _main.Sites.StopAllCommand.ExecuteAsync(null);
            }, StopColor));
            menu.Add(Submenu(Loc.T("TraySites"), "IconWeb", BuildSitesMenu(status)));
            menu.Add(Submenu(Loc.T("NavModules"), "IconPuzzle", BuildModulesMenu()));
            menu.Add(Item(Loc.T("TrayNewSite"), "IconPlus", () =>
            {
                _showMainWindow();
                _ = _main.Sites.NewSiteAsync();
            }, AccentColor));
            menu.Add(new NativeMenuItemSeparator());
        }

        menu.Add(Item(Loc.T("TrayWww"), "IconFolderOpen", () => AppEnvironment.OpenFolder(_main.Status?.WwwRoot)));
        menu.Add(Item(Loc.T("NavUrls"), "IconLink", () => Navigate(_main.Urls)));
        menu.Add(Item(Loc.T("NavLogs"), "IconLog", () => Navigate(_main.Logs)));
        menu.Add(Item(Loc.T("NavSettings"), "IconCog", () => Navigate(_main.Settings)));
        menu.Add(Submenu(Loc.T("LblLanguage"), "IconWeb", BuildLanguageMenu()));
        if (OperatingSystem.IsWindows())
        {
            menu.Add(Submenu(Loc.T("HdrService"), "IconShield", BuildServiceMenu(connected)));
        }

        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item(Loc.T("NavAbout"), "IconInfo", () => Navigate(_main.About)));
        menu.Add(Item(Loc.T("TrayExit"), "IconExit", _exit));
        return menu;
    }

    private NativeMenu BuildSitesMenu(ServiceStatus? status)
    {
        var menu = new NativeMenu();
        if (status is null || status.Sites.Count == 0)
        {
            menu.Add(Item(Loc.T("TrayNoSites"), "IconInfo", _showMainWindow));
            return menu;
        }

        foreach (var card in _main.Sites.Cards)
        {
            var c = card;
            var siteMenu = new NativeMenu();
            siteMenu.Add(Item(Loc.T("TipOpenBrowser"), "IconOpenInNew", () => AppEnvironment.OpenUrl(c.OpenTarget)));
            siteMenu.Add(Item(Loc.T("MenuCopyUrl"), "IconCopy", () => _ = _main.CopyAsync(c.PrimaryUrl, "ToastUrlCopied")));
            siteMenu.Add(Item(Loc.T("TipCopyMessage"), "IconMessage", () => _ = _main.Sites.CopyMessageAsync(c)));
            siteMenu.Add(Item(c.ToggleRunText, c.IsEnabled ? "IconStop" : "IconPlay", () => _ = _main.Sites.ToggleRunAsync(c)));
            siteMenu.Add(Item(Loc.T("TipEdit"), "IconPencil", () =>
            {
                _showMainWindow();
                _ = _main.Sites.EditAsync(c);
            }));
            if (c.IsApp)
            {
                siteMenu.Add(Item(Loc.T("MenuRestartApp"), "IconRestart", () => _ = _main.Sites.RestartAppAsync(c)));
            }

            siteMenu.Add(Item(Loc.T("TipOpenFolder"), "IconFolderOpen", () => AppEnvironment.OpenFolder(c.Site.Root)));
            siteMenu.Add(Item(Loc.T("TipShowLogs"), "IconLog", () =>
            {
                _showMainWindow();
                _main.Sites.ShowLogs(c);
            }));

            var (icon, color) = c.State switch
            {
                SiteRunState.Running => ("IconCheckCircle", RunningColor),
                SiteRunState.Error => ("IconCloseCircle", ErrorColor),
                _ => ("IconStopCircle", IconColor)
            };
            menu.Add(new NativeMenuItem($"{c.Name}  ·  {c.StateText}") { Icon = IconRenderer.Render(icon, color), Menu = siteMenu });
        }

        return menu;
    }

    private NativeMenu BuildModulesMenu()
    {
        var menu = new NativeMenu();
        foreach (var card in _main.Modules.Active)
        {
            var c = card;
            var moduleMenu = new NativeMenu();
            if (c.Status.CanToggle)
            {
                moduleMenu.Add(c.IsOn
                    ? Item(Loc.T("TrayModuleOff"), "IconStop", () => c.IsOn = false)
                    : Item(Loc.T("TrayModuleOn"), "IconPlay", () => c.IsOn = true));
            }

            if (c.ShowDownload)
            {
                moduleMenu.Add(Item(Loc.T("BtnDownload"), "IconDownload", () => _ = _main.Modules.DownloadAsync(c)));
            }

            if (c.CanRestart)
            {
                moduleMenu.Add(Item(Loc.T("BtnRestart"), "IconRestart", () => _ = _main.Modules.RestartAsync(c)));
            }

            if (c.HasUrl)
            {
                moduleMenu.Add(Item(Loc.T("BtnOpen"), "IconOpenInNew", () => c.OpenCommand.Execute(null)));
            }

            moduleMenu.Add(Item(Loc.T("BtnLog"), "IconLog", () =>
            {
                _showMainWindow();
                _main.Modules.ShowLog(c);
            }));

            var (icon, color) = c.Status.State switch
            {
                ModuleState.Running or ModuleState.Ready => ("IconCheckCircle", RunningColor),
                ModuleState.Error or ModuleState.NotInstalled => ("IconCloseCircle", ErrorColor),
                _ => ("IconStopCircle", IconColor)
            };
            menu.Add(new NativeMenuItem($"{c.Name}  ·  {c.StateText}") { Icon = IconRenderer.Render(icon, color), Menu = moduleMenu });
        }

        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item(Loc.T("NavModules"), "IconPuzzle", () => Navigate(_main.Modules)));
        return menu;
    }

    private NativeMenu BuildLanguageMenu()
    {
        var menu = new NativeMenu();
        foreach (var language in LanguageOption.All)
        {
            var code = language.Code;
            var header = language.Code == Loc.Language ? $"{language.Name}  ✓" : language.Name;
            var item = new NativeMenuItem(header) { Icon = language.Flag ?? IconRenderer.Render("IconWeb", IconColor) };
            item.Click += (_, _) => _main.SetLanguage(code);
            menu.Add(item);
        }

        return menu;
    }

    private NativeMenu BuildServiceMenu(bool connected)
    {
        var menu = new NativeMenu();
        if (connected && _main.IsDesktopMode)
        {
            menu.Add(Item(Loc.T("BtnInstallService"), "IconShield", () => Run(_main.InstallServiceCommand.ExecuteAsync)));
            menu.Add(Item(Loc.T("BtnStopDesktopMode"), "IconExit", () => _ = _main.StopDesktopModeCommand.ExecuteAsync(null)));
        }
        else if (connected)
        {
            menu.Add(Item(Loc.T("BtnRestartService"), "IconRestart", () => _ = _main.RestartServiceCommand.ExecuteAsync(null)));
            menu.Add(Item(Loc.T("BtnStopService"), "IconStop", () => _ = _main.StopServiceCommand.ExecuteAsync(null)));
        }
        else
        {
            menu.Add(Item(Loc.T("BtnStartService"), "IconPlay", () => _ = _main.StartServiceCommand.ExecuteAsync(null)));
            menu.Add(Item(Loc.T("BtnStartDesktopMode"), "IconConsole", () => _ = _main.StartDesktopModeCommand.ExecuteAsync(null)));
            menu.Add(Item(Loc.T("BtnInstallService"), "IconShield", () => Run(_main.InstallServiceCommand.ExecuteAsync)));
        }

        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item(Loc.T("BtnReconnect"), "IconRefresh", _main.Connection.Reconnect));
        return menu;
    }

    /// <summary>Dialog-based actions need the main window as owner.</summary>
    private void Run(Func<object?, Task> command)
    {
        _showMainWindow();
        _ = command(null);
    }

    private void Navigate(PageViewModel page)
    {
        _showMainWindow();
        _main.NavigateTo(page);
    }

    private static NativeMenuItem Item(string header, string iconKey, Action action, Color? color = null)
    {
        var item = new NativeMenuItem(header) { Icon = IconRenderer.Render(iconKey, color ?? IconColor) };
        item.Click += (_, _) => action();
        return item;
    }

    private static NativeMenuItem Submenu(string header, string iconKey, NativeMenu menu) =>
        new(header) { Icon = IconRenderer.Render(iconKey, IconColor), Menu = menu };

    private void ToggleFlyout()
    {
        _flyout ??= CreateFlyout();
        if (_flyout.IsVisible)
        {
            _flyout.Hide();
        }
        else if ((DateTime.UtcNow - _flyout.LastHidden).TotalMilliseconds > 350)
        {
            _flyout.ShowNearTray();
        }
    }

    private TrayFlyoutWindow CreateFlyout()
    {
        var flyout = new TrayFlyoutWindow { DataContext = _main };
        flyout.OpenManagerRequested += _showMainWindow;
        flyout.OpenUrlsRequested += () => Navigate(_main.Urls);
        return flyout;
    }

    public void Dispose()
    {
        _main.StatusUpdated -= Update;
        _flyout?.Close();
        _trayIcon.IsVisible = false;
        _trayIcon.Dispose();
    }
}
