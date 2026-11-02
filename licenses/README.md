# Licenses

## WEBit

WEBit is released under the **MIT License** – see [LICENSE.md](LICENSE.md).
Copyright © 2026 Sven Klocke (*sksdesign | complicatiion*).

| Document | Content |
| --- | --- |
| [LICENSE.md](LICENSE.md) | MIT License of WEBit |
| [COPYRIGHT.md](COPYRIGHT.md) | Copyright and trademark notes |
| [LEGAL-NOTICE.md](LEGAL-NOTICE.md) | Disclaimer and limitation of liability |
| [PRIVACY.md](PRIVACY.md) | Privacy notes (local-first, no telemetry) |

## Third-party components

Components shipped with WEBit keep their own copyrights and licenses. Full license texts are in [third-party/](third-party/).

| Component | Version | License | Shipped as | File |
| --- | --- | --- | --- | --- |
| Caddy | 2.11.4 | Apache-2.0 | `bin\caddy\WEBit-Caddy.exe` | [third-party/CADDY.md](third-party/CADDY.md) |
| Go modules compiled into Caddy | see list | mostly MIT, BSD, Apache-2.0 | part of `WEBit-Caddy.exe` | [third-party/CADDY-MODULES.md](third-party/CADDY-MODULES.md) |
| Mailpit | latest | MIT | download in the app → `data\modules\mailpit\` | [third-party/MAILPIT.md](third-party/MAILPIT.md) |
| PHP (NTS x64) | latest 8.x | PHP License 3.01 | download in the app → `data\modules\php\`, additional versions `data\modules\php-<x.y>\` | [third-party/PHP.md](third-party/PHP.md) |
| Xdebug | latest | Xdebug License | download in the app → `data\modules\xdebug\` | [third-party/XDEBUG.md](third-party/XDEBUG.md) |
| MariaDB Server | latest LTS | GPL-2.0 | download in the app → `data\modules\mariadb\` (never bundled) | [third-party/MARIADB.md](third-party/MARIADB.md) |
| Node.js (incl. npm) | latest LTS | MIT (+ bundled components) | download in the app → `data\modules\node\` | [third-party/NODEJS.md](third-party/NODEJS.md) |
| uv | latest | MIT OR Apache-2.0 | download in the app → `data\modules\python\` | [third-party/UV.md](third-party/UV.md) |
| Python | via uv | PSF License | installed by uv → `data\runtimes\python\` | [third-party/PYTHON.md](third-party/PYTHON.md) |
| Composer | latest | MIT | download in the app → `data\modules\composer\` | [third-party/COMPOSER.md](third-party/COMPOSER.md) |
| WP-CLI | latest | MIT | download in the app → `data\modules\wpcli\` | [third-party/WP-CLI.md](third-party/WP-CLI.md) |
| Adminer | latest | Apache-2.0 / GPL-2.0 | download in the app → `data\modules\adminer\` | [third-party/ADMINER.md](third-party/ADMINER.md) |
| WordPress | latest | GPL-2.0+ | downloaded by WP-CLI into the site folder (never bundled) | [third-party/WORDPRESS.md](third-party/WORDPRESS.md) |
| Laravel | latest | MIT | created by Composer in the site folder | [third-party/LARAVEL.md](third-party/LARAVEL.md) |
| Microsoft Visual C++ Redistributable | 14.x | Microsoft redistributable | download in the app (for PHP), installed by Microsoft's installer | [third-party/VCREDIST.md](third-party/VCREDIST.md) |
| .NET Runtime | 10.0 | MIT | self-contained runtime | [third-party/DOTNET.md](third-party/DOTNET.md) |
| Microsoft.Extensions.Hosting (+ WindowsServices) | 10.0.0 | MIT | library | [third-party/MICROSOFT-EXTENSIONS.md](third-party/MICROSOFT-EXTENSIONS.md) |
| Avalonia UI (incl. Fluent theme, Inter font package) | 11.3.14 | MIT | library | [third-party/AVALONIA.md](third-party/AVALONIA.md) |
| SkiaSharp / HarfBuzzSharp (via Avalonia) | – | MIT (+ Skia BSD-3-Clause, HarfBuzz MIT-style) | native libraries | [third-party/SKIASHARP.md](third-party/SKIASHARP.md) |
| QRCoder | 1.6.0 | MIT | library | [third-party/QRCODER.md](third-party/QRCODER.md) |
| SharpZipLib | 1.4.2 | MIT | library | [third-party/SHARPZIPLIB.md](third-party/SHARPZIPLIB.md) |
| CommunityToolkit.Mvvm | 8.4.0 | MIT | library | [third-party/COMMUNITYTOOLKIT-MVVM.md](third-party/COMMUNITYTOOLKIT-MVVM.md) |
| Material Design Icons (Pictogrammers) | – | Apache-2.0 | vector icons in the UI | [third-party/MATERIAL-DESIGN-ICONS.md](third-party/MATERIAL-DESIGN-ICONS.md) |
| Inter (font) | 4.x | SIL OFL 1.1 | embedded font | [third-party/INTER-FONT.md](third-party/INTER-FONT.md) |

Shared license texts: [Apache License 2.0](third-party/LICENSE-APACHE-2.0.md) · [SIL Open Font License 1.1](third-party/LICENSE-OFL-1.1.md) ·
[MIT](third-party/LICENSE-MIT.md) · [BSD 2-Clause](third-party/LICENSE-BSD-2-CLAUSE.md) · [BSD 3-Clause](third-party/LICENSE-BSD-3-CLAUSE.md) · [ISC](third-party/LICENSE-ISC.md)

Build-time only (not shipped): xUnit (Apache-2.0), Microsoft.NET.Test.Sdk (MIT), Inno Setup (own license, installer only).

Only Caddy is bundled. PHP, Xdebug, Mailpit, MariaDB, Node.js, uv/Python and the VC++ runtime are not part of the package; the app downloads them on request from the official source (checksum verified), their licenses are listed here. GPL-licensed software (MariaDB) is only downloaded on demand and never bundled.
