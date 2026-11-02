using System.Text;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using WEBit.App.Localization;
using WEBit.App.ViewModels;
using WEBit.App.Views;
using WEBit.Contracts;

namespace WEBit.App.Services;

/// <summary>Result of a confirmation dialog.</summary>
public readonly record struct ConfirmResult(bool Confirmed, bool OptionChecked);

/// <summary>Windows, dialogs, clipboard and pickers for view models.</summary>
public sealed class UiHost
{
    public Window? MainWindow { get; set; }

    /// <summary>Brings the main window to front (set by the app).</summary>
    public Action? ShowMainWindow { get; set; }

    /// <summary>Exits the app including the tray (set by the app).</summary>
    public Action? ExitApplication { get; set; }

    public async Task CopyAsync(string? text)
    {
        if (string.IsNullOrEmpty(text) || MainWindow?.Clipboard is not { } clipboard)
        {
            return;
        }

        await clipboard.SetTextAsync(text);
    }

    public async Task<string?> PickFolderAsync(string title, string? startPath)
    {
        var window = EnsureWindow();
        if (window is null)
        {
            return null;
        }

        IStorageFolder? start = null;
        if (!string.IsNullOrWhiteSpace(startPath) && Directory.Exists(startPath))
        {
            start = await window.StorageProvider.TryGetFolderFromPathAsync(startPath);
        }

        var result = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = start
        });

        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickFileAsync(string title, string typeName, IReadOnlyList<string> patterns)
    {
        var window = EnsureWindow();
        if (window is null)
        {
            return null;
        }

        var result = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(typeName) { Patterns = patterns }]
        });

        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    /// <summary>Save dialog; returns the chosen local path (null = cancelled).</summary>
    public async Task<string?> PickSavePathAsync(string title, string suggestedName, string extension, string typeName)
    {
        var window = EnsureWindow();
        if (window is null)
        {
            return null;
        }

        var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(typeName) { Patterns = ["*." + extension] }]
        });

        return file?.TryGetLocalPath();
    }

    public async Task<bool> SaveTextAsync(string title, string suggestedName, string extension, string typeName, string content)
    {
        var window = EnsureWindow();
        if (window is null)
        {
            return false;
        }

        var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(typeName) { Patterns = ["*." + extension] }]
        });

        if (file is null)
        {
            return false;
        }

        await using var stream = await file.OpenWriteAsync();
        if (stream.CanSeek)
        {
            stream.SetLength(0);
        }

        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(content);
        return true;
    }

    public async Task<ConfirmResult> ConfirmAsync(string title, string message, string confirmText, bool danger, string? optionText = null, bool optionDefault = false,
        string? confirmIcon = null)
    {
        var window = EnsureWindow();
        if (window is null)
        {
            return default;
        }

        var dialog = new ConfirmWindow(title, message, confirmText, danger, optionText, optionDefault, confirmIcon);
        var confirmed = await dialog.ShowDialog<bool>(window);
        return new ConfirmResult(confirmed, confirmed && dialog.OptionChecked);
    }

    /// <summary>Asks for one value; null = cancelled.</summary>
    public async Task<string?> PromptAsync(string title, string message, string confirmText, string watermark)
    {
        var window = EnsureWindow();
        if (window is null)
        {
            return null;
        }

        var dialog = new ConfirmWindow(title, message, confirmText, watermark);
        return await dialog.ShowDialog<bool>(window) ? dialog.Input : null;
    }

    /// <summary>Asks for a password (masked, not trimmed); null = cancelled.</summary>
    public async Task<string?> PromptPasswordAsync(string title, string message, string confirmText)
    {
        var window = EnsureWindow();
        if (window is null)
        {
            return null;
        }

        var dialog = new ConfirmWindow(title, message, confirmText, Loc.T("WatermarkPassword"));
        dialog.MaskInput();
        return await dialog.ShowDialog<bool>(window) ? dialog.RawInput : null;
    }

    /// <summary>Folder of a new site exists: (true, null) = use it, (true, name) = rename, (false, _) = cancel.</summary>
    public async Task<(bool Ok, string? NewName)> FolderExistsAsync(string title, string message, string suggestion)
    {
        var window = EnsureWindow();
        if (window is null)
        {
            return (false, null);
        }

        var dialog = new ConfirmWindow(title, message, Localization.Loc.T("BtnRenameFolder"), suggestion, suggestion,
            Localization.Loc.T("BtnUseExistingFolder"), "IconFolderOpen");
        if (!await dialog.ShowDialog<bool>(window))
        {
            return (false, null);
        }

        return dialog.SecondaryChosen ? (true, null) : (true, dialog.Input);
    }

    public async Task<TemplateRequest?> TemplateAsync(string template, string defaultName)
    {
        var window = EnsureWindow();
        if (window is null)
        {
            return null;
        }

        var dialog = new TemplateWindow(template, defaultName);
        return await dialog.ShowDialog<bool>(window) ? dialog.Request : null;
    }

    public async Task ShowQrAsync(string siteName, IReadOnlyList<string> urls)
    {
        var window = EnsureWindow();
        if (window is not null && urls.Count > 0)
        {
            await new QrWindow(siteName, urls).ShowDialog(window);
        }
    }

    public async Task ShowTextAsync(string title, string text)
    {
        var window = EnsureWindow();
        if (window is not null)
        {
            await new TextWindow(title, text).ShowDialog(window);
        }
    }

    public async Task<SaveSiteResult?> EditSiteAsync(SiteEditorViewModel viewModel)
    {
        var window = EnsureWindow();
        if (window is null)
        {
            return null;
        }

        var dialog = new SiteEditorWindow { DataContext = viewModel };
        return await dialog.ShowDialog<SaveSiteResult?>(window);
    }

    private Window? EnsureWindow()
    {
        if (MainWindow is { IsVisible: false })
        {
            ShowMainWindow?.Invoke();
        }

        // An open dialog (e.g. the site editor) owns pickers and nested dialogs.
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            var dialog = desktop.Windows.LastOrDefault(w => w.IsVisible && w.IsActive && w != MainWindow && w is not TrayFlyoutWindow);
            if (dialog is not null)
            {
                return dialog;
            }
        }

        return MainWindow;
    }
}
