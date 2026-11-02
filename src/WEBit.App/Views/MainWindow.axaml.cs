using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using WEBit.App.ViewModels;

namespace WEBit.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
        WEBit.App.Services.WindowChrome.Apply(this, maximize: false);

        // Notifications slide in from the right.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainViewModel.Toast) && vm.HasToast)
                    {
                        WEBit.App.Controls.Motion.FadeIn(ToastHost, offsetX: 24, offsetY: 0, milliseconds: 220);
                    }
                };
            }
        };

        // Quick test: drop a project folder anywhere on the window.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private static List<string> DroppedFolders(DragEventArgs e)
    {
        var folders = new List<string>();
        foreach (var item in e.DataTransfer.TryGetFiles() ?? [])
        {
            var path = item.TryGetLocalPath();
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            // A dropped file (e.g. index.html) stands for its folder.
            var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder) && !folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
            {
                folders.Add(folder);
            }
        }

        return folders;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Formats.Contains(DataFormat.File) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DataContext is MainViewModel main)
        {
            var folders = DroppedFolders(e);
            if (folders.Count > 0)
            {
                main.NavigateTo(main.Sites);
                await main.Sites.CreateQuickSitesAsync(folders);
            }
        }
    }

    /// <summary>Set on app exit; otherwise closing hides the window to the tray.</summary>
    public bool AllowClose { get; set; }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (AllowClose || e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
        {
            return;
        }

        // Service and sites keep running.
        e.Cancel = true;
        Hide();
    }
}
