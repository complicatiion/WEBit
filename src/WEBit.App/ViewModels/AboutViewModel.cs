using CommunityToolkit.Mvvm.Input;
using WEBit.App.Localization;
using WEBit.App.Services;

namespace WEBit.App.ViewModels;

public sealed record ThirdPartyItem(string Name, string Version, string License, string Usage, string Url, string LicenseFile);

/// <summary>About page: version, license, components.</summary>
public sealed partial class AboutViewModel(MainViewModel main) : PageViewModel(main)
{
    public override string TitleKey => "NavAbout";

    public override bool RequiresService => false;

    public string Version => "v" + AppEnvironment.Version;

    public string Copyright => "Copyright © 2026 Sven Klocke · sksdesign | complicatiion";

    public IReadOnlyList<ThirdPartyItem> ThirdParty { get; } =
    [
        new("Caddy", "2.11", "Apache-2.0", "Web server engine", "https://caddyserver.com", "third-party/CADDY.md"),
        new("Go modules in Caddy", "–", "MIT · BSD · Apache-2.0", "Compiled into the web server", "https://github.com/caddyserver/caddy", "third-party/CADDY-MODULES.md"),
        new("Mailpit", "latest", "MIT", "Mail testing module (download)", "https://mailpit.axllent.org", "third-party/MAILPIT.md"),
        new("PHP", "latest 8.x", "PHP License 3.01", "PHP module (download)", "https://www.php.net", "third-party/PHP.md"),
        new("Xdebug", "latest", "Xdebug License", "PHP debugger (download)", "https://xdebug.org", "third-party/XDEBUG.md"),
        new("MariaDB", "latest LTS", "GPL-2.0", "Database module (download)", "https://mariadb.org", "third-party/MARIADB.md"),
        new("Node.js", "latest LTS", "MIT", "Runtime for Node.js sites (download)", "https://nodejs.org", "third-party/NODEJS.md"),
        new("uv", "latest", "MIT · Apache-2.0", "Python manager for Python sites (download)", "https://docs.astral.sh/uv/", "third-party/UV.md"),
        new("Python", "via uv", "PSF License", "Runtime for Python sites (installed by uv)", "https://www.python.org", "third-party/PYTHON.md"),
        new("Composer", "latest", "MIT", "PHP package manager (download)", "https://getcomposer.org", "third-party/COMPOSER.md"),
        new("WP-CLI", "latest", "MIT", "WordPress setup (download)", "https://wp-cli.org", "third-party/WP-CLI.md"),
        new("Adminer", "latest", "Apache-2.0 · GPL-2.0", "Database UI (download)", "https://www.adminer.org", "third-party/ADMINER.md"),
        new("WordPress", "latest", "GPL-2.0+", "Template (downloaded by WP-CLI)", "https://wordpress.org", "third-party/WORDPRESS.md"),
        new("Laravel", "latest", "MIT", "Template (created by Composer)", "https://laravel.com", "third-party/LARAVEL.md"),
        new("QRCoder", "1.6", "MIT", "QR codes", "https://github.com/codebude/QRCoder", "third-party/QRCODER.md"),
        new("SharpZipLib", "1.4", "MIT", "Export ZIP with password", "https://github.com/icsharpcode/SharpZipLib", "third-party/SHARPZIPLIB.md"),
        new("Visual C++ Runtime", "14.x", "Microsoft redistributable", "Required by PHP (download)", "https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist", "third-party/VCREDIST.md"),
        new(".NET", "10.0", "MIT", "Runtime", "https://github.com/dotnet/runtime", "third-party/DOTNET.md"),
        new("Microsoft.Extensions.Hosting", "10.0", "MIT", "Windows service host", "https://github.com/dotnet/runtime", "third-party/MICROSOFT-EXTENSIONS.md"),
        new("Avalonia UI", "11.3", "MIT", "User interface", "https://avaloniaui.net", "third-party/AVALONIA.md"),
        new("SkiaSharp / HarfBuzzSharp", "–", "MIT · BSD-3-Clause", "Rendering", "https://github.com/mono/SkiaSharp", "third-party/SKIASHARP.md"),
        new("CommunityToolkit.Mvvm", "8.4", "MIT", "MVVM", "https://github.com/CommunityToolkit/dotnet", "third-party/COMMUNITYTOOLKIT-MVVM.md"),
        new("Material Design Icons", "–", "Apache-2.0", "Icons", "https://pictogrammers.com/library/mdi/", "third-party/MATERIAL-DESIGN-ICONS.md"),
        new("Inter", "4.x", "SIL OFL 1.1", "Font", "https://rsms.me/inter/", "third-party/INTER-FONT.md")
    ];

    [RelayCommand]
    private void OpenProject() => AppEnvironment.OpenUrl(AppEnvironment.ProjectUrl);

    [RelayCommand]
    private void OpenMitLicense() => AppEnvironment.OpenUrl(AppEnvironment.MitLicenseUrl);

    [RelayCommand]
    private void OpenLicensesFolder() => AppEnvironment.OpenFolder(AppEnvironment.LicensesDirectory);

    [RelayCommand]
    private void OpenLink(string? url) => AppEnvironment.OpenUrl(url);

    [RelayCommand]
    private Task ShowLicenseAsync(string? file) => ShowFileAsync(Loc.T("HdrLicense"), file ?? "LICENSE.md");

    [RelayCommand]
    private Task ShowThirdPartyAsync(ThirdPartyItem? item) =>
        item is null ? Task.CompletedTask : ShowFileAsync(item.Name, item.LicenseFile);

    [RelayCommand]
    private Task ShowDocumentAsync(string? file) => ShowFileAsync(Path.GetFileNameWithoutExtension(file ?? ""), file ?? "README.md");

    private Task ShowFileAsync(string title, string relativePath)
    {
        var path = Path.Combine(AppEnvironment.LicensesDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        string text;
        try
        {
            text = File.Exists(path) ? File.ReadAllText(path) : Loc.F("LicenseFileMissing", path);
        }
        catch (Exception ex)
        {
            text = ex.Message;
        }

        return Host.ShowTextAsync(title, text);
    }
}
