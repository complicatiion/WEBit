using Avalonia.Controls;
using WEBit.App.ViewModels;
using WEBit.Contracts;

namespace WEBit.App.Views;

/// <summary>Create/edit site dialog.</summary>
public partial class SiteEditorWindow : Window
{
    private SiteEditorViewModel? _viewModel;

    public SiteEditorWindow()
    {
        InitializeComponent();
        WEBit.App.Services.WindowChrome.Apply(this, maximize: true);
        DataContextChanged += (_, _) => Attach(DataContext as SiteEditorViewModel);
        Closed += (_, _) => Attach(null);
    }

    private void Attach(SiteEditorViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.CloseRequested -= OnCloseRequested;
        }

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.CloseRequested += OnCloseRequested;
        }
    }

    private void OnCloseRequested(SaveSiteResult? result) => Close(result);

    // Switching tabs (General, Address, Content …): the new page slides in.
    private void OnTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.Source == Tabs && Tabs.SelectedItem is TabItem { Content: Avalonia.Visual content })
        {
            WEBit.App.Controls.Motion.FadeIn(content, offsetX: 16, offsetY: 0);
        }
    }
}
