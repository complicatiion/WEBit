using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using WEBit.App.Localization;

namespace WEBit.App.ViewModels;

/// <summary>Sidebar entry.</summary>
public sealed class NavItem : ObservableObject
{
    public NavItem(string titleKey, string iconKey, PageViewModel page)
    {
        TitleKey = titleKey;
        IconKey = iconKey;
        Page = page;
        Icon = Res.Icon(iconKey);
    }

    public string TitleKey { get; }

    public string IconKey { get; }

    public PageViewModel Page { get; }

    public Geometry? Icon { get; }

    public string Title => Loc.T(TitleKey);

    public void RefreshText() => OnPropertyChanged(nameof(Title));
}
