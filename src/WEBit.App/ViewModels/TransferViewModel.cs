using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WEBit.App.Localization;
using WEBit.Contracts;

namespace WEBit.App.ViewModels;

/// <summary>Site in the export list (Settings → Export &amp; import).</summary>
public sealed partial class SiteSelectItem(string id, string name, bool hasDatabase) : ObservableObject
{
    public string Id { get; } = id;

    public string Name { get; } = name;

    public bool HasDatabase { get; } = hasDatabase;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// Export and import of sites (files, databases, settings) as one ZIP. The service writes/reads the ZIP in its
/// data folder; the app copies it from/to the place the user picks.
/// </summary>
public sealed partial class TransferViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<SiteSelectItem> Sites { get; } = [];

    [ObservableProperty]
    private bool _includeDatabases = true;

    [ObservableProperty]
    private bool _includePackages;

    [ObservableProperty]
    private bool _includeSettings;

    [ObservableProperty]
    private bool _applySettings;

    /// <summary>Export ZIP encrypted with AES-256.</summary>
    [ObservableProperty]
    private bool _usePassword;

    [ObservableProperty]
    private string? _password;

    [ObservableProperty]
    private string? _passwordRepeat;

    [ObservableProperty]
    private bool _isBusy;

    public bool HasSites => Sites.Count > 0;

    /// <summary>Refreshes the list from the status; keeps the selection.</summary>
    public void Refresh()
    {
        var selected = Sites.Where(s => s.IsSelected).Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Sites.Clear();
        foreach (var site in (main.Status?.Sites ?? []).Select(s => s.Site).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Sites.Add(new SiteSelectItem(site.Id, site.Name, !string.IsNullOrEmpty(site.Database)) { IsSelected = selected.Contains(site.Id) });
        }

        OnPropertyChanged(nameof(HasSites));
    }

    [RelayCommand]
    private void SelectAll()
    {
        var all = Sites.Any(s => !s.IsSelected);
        foreach (var site in Sites)
        {
            site.IsSelected = all;
        }
    }

    [RelayCommand]
    private Task ExportAsync()
    {
        if (UsePassword && (string.IsNullOrEmpty(Password) || Password.Length < 8))
        {
            main.ShowToast(Loc.T("ToastPasswordTooShort"), isError: true);
            return Task.CompletedTask;
        }

        if (UsePassword && Password != PasswordRepeat)
        {
            main.ShowToast(Loc.T("ToastPasswordMismatch"), isError: true);
            return Task.CompletedTask;
        }

        return ExportAsync(Sites.Where(s => s.IsSelected).Select(s => s.Id).ToList(), IncludeDatabases, IncludePackages, IncludeSettings,
            UsePassword ? Password : null);
    }

    /// <summary>Also used by the site card (one site with its database, without packages).</summary>
    public async Task ExportAsync(List<string> ids, bool databases, bool packages, bool settings, string? password = null)
    {
        if (ids.Count == 0)
        {
            main.ShowToast(Loc.T("ToastExportNothing"), isError: true);
            return;
        }

        IsBusy = true;
        try
        {
            main.ShowToast(Loc.T("ToastExportStarted"));
            var result = await main.Connection.RequestAsync<ExportResult>(Methods.ExportSites,
                new ExportRequest
                {
                    SiteIds = ids, IncludeDatabases = databases, IncludePackages = packages, IncludeSettings = settings,
                    Password = string.IsNullOrEmpty(password) ? null : password
                },
                TimeSpan.FromMinutes(60));
            if (result is null)
            {
                return;
            }

            try
            {
                var target = await main.Host.PickSavePathAsync(Loc.T("ExportTitle"), Path.GetFileName(result.Path), "zip", "ZIP");
                if (target is not null)
                {
                    await Task.Run(() => File.Copy(result.Path, target, overwrite: true));
                    main.ShowToast(Loc.F("ToastExportDone", result.Sites, Core.Diagnostics.LogReader.FormatBytes(result.SizeBytes)));
                }
            }
            finally
            {
                TryDelete(result.Path);
            }

            if (result.Warnings.Count > 0)
            {
                await main.Host.ShowTextAsync(Loc.T("ExportTitle"), string.Join(Environment.NewLine, result.Warnings));
            }
        }
        catch (Exception ex)
        {
            main.ShowError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (main.Status is not { } status)
        {
            return;
        }

        var source = await main.Host.PickFileAsync(Loc.T("ImportTitle"), "ZIP", ["*.zip"]);
        if (source is null)
        {
            return;
        }

        // Encrypted export: ask for the password first.
        string? password = null;
        if (IsEncryptedZip(source))
        {
            password = await main.Host.PromptPasswordAsync(Loc.T("ImportTitle"), Loc.T("ImportPasswordText"), Loc.T("BtnImport"));
            if (string.IsNullOrEmpty(password))
            {
                return;
            }
        }

        IsBusy = true;
        var copy = Path.Combine(status.DataRoot, "imports", $"import-{Guid.NewGuid():N}.zip");
        try
        {
            // The service account cannot read user folders: hand the file over through the data folder.
            await Task.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(source, copy, overwrite: true);
            });

            main.ShowToast(Loc.T("ToastImportStarted"));
            var result = await main.Connection.RequestAsync<ImportResult>(Methods.ImportSites,
                new ImportRequest { Path = copy, ApplySettings = ApplySettings, Password = password }, TimeSpan.FromMinutes(60));
            if (result is not null)
            {
                main.ShowToast(Loc.F("ToastImportDone", result.Sites.Count));
                if (result.Warnings.Count > 0)
                {
                    await main.Host.ShowTextAsync(Loc.T("ImportTitle"), string.Join(Environment.NewLine, result.Warnings));
                }
            }
        }
        catch (Exception ex)
        {
            main.ShowError(ex);
            TryDelete(copy);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>First local file header of the ZIP has the "encrypted" flag (general purpose bit 0).</summary>
    private static bool IsEncryptedZip(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            Span<byte> header = stackalloc byte[8];
            return stream.Read(header) == 8 && header[0] == 0x50 && header[1] == 0x4B && header[2] == 0x03 && header[3] == 0x04
                   && (header[6] & 1) == 1;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception)
        {
            // Removed by the service or left in data\exports.
        }
    }
}
