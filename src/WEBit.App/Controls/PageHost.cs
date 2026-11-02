using Avalonia;
using Avalonia.Controls;
using WEBit.App.ViewModels;
using WEBit.App.Views;

namespace WEBit.App.Controls;

/// <summary>
/// Hosts all page views at once and only toggles their visibility. Views stay in the visual tree,
/// so switching pages neither re-creates nor re-styles them (much faster than swapping content).
/// Each view is created on its first visit.
/// </summary>
public sealed class PageHost : Grid
{
    public static readonly StyledProperty<PageViewModel?> PageProperty =
        AvaloniaProperty.Register<PageHost, PageViewModel?>(nameof(Page));

    private readonly Dictionary<PageViewModel, Control> _views = new();

    static PageHost()
    {
        PageProperty.Changed.AddClassHandler<PageHost>((host, _) => host.ShowPage());
    }

    public PageViewModel? Page
    {
        get => GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    private void ShowPage()
    {
        var page = Page;
        if (page is not null && !_views.ContainsKey(page))
        {
            var view = Create(page);
            view.DataContext = page;
            _views[page] = view;
            Children.Add(view);
        }

        foreach (var (model, view) in _views)
        {
            var visible = ReferenceEquals(model, page);
            if (visible && !view.IsVisible)
            {
                view.IsVisible = true;
                Motion.FadeIn(view, offsetY: 12);
            }
            else
            {
                view.IsVisible = visible;
            }
        }
    }

    private static Control Create(PageViewModel page) => page switch
    {
        DashboardViewModel => new DashboardView(),
        SitesViewModel => new SitesView(),
        UrlsViewModel => new UrlsView(),
        ModulesViewModel => new ModulesView(),
        DatabasesViewModel => new DatabasesView(),
        DependenciesViewModel => new DependenciesView(),
        NetworkViewModel => new NetworkView(),
        LogsViewModel => new LogsView(),
        SettingsViewModel => new SettingsView(),
        AboutViewModel => new AboutView(),
        _ => new TextBlock { Text = page.GetType().Name }
    };
}
