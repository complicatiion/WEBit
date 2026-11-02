using CommunityToolkit.Mvvm.ComponentModel;
using WEBit.App.Localization;
using WEBit.App.Services;
using WEBit.Contracts;

namespace WEBit.App.ViewModels;

/// <summary>Base of all sidebar pages.</summary>
public abstract class PageViewModel(MainViewModel main) : ObservableObject
{
    public MainViewModel Main { get; } = main;

    protected ServiceConnection Connection => Main.Connection;

    protected UiHost Host => Main.Host;

    public abstract string TitleKey { get; }

    public string Title => Loc.T(TitleKey);

    /// <summary>Page needs the service; otherwise the offline card is shown.</summary>
    public virtual bool RequiresService => true;

    public virtual void OnStatus(ServiceStatus status)
    {
    }

    public virtual void OnActivated()
    {
    }

    public virtual void OnDeactivated()
    {
    }

    public virtual void OnConnected()
    {
    }

    public virtual void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(Title));
    }
}
