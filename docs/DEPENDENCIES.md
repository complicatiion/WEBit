# Abhängigkeiten & Runtime-Manager

## Grundsatz

So viel wie möglich wird mitgeliefert; was nicht mitgeliefert werden soll oder darf, lädt ein integrierter **Runtime-Manager** aus der offiziellen Quelle – immer mit Prüfsumme. Manuelles Nachinstallieren entfällt.

## Komponenten

| Komponente | Lizenz | Status v0.0.9 | Weg | Quelle / Prüfsumme |
| --- | --- | --- | --- | --- |
| .NET 10 Runtime | MIT | ✔ gebündelt | self-contained Publish | – |
| Caddy | Apache-2.0 | ✔ gebündelt | `build\fetch-thirdparty.ps1` → `bin\caddy` | GitHub Releases + `checksums.txt` (SHA-256-Digest der API, Fallback SHA-512) |
| Go-Module in Caddy | MIT/BSD/Apache-2.0 u. a. | ✔ Teil von Caddy | Liste `licenses\third-party\CADDY-MODULES.md` aus `WEBit-Caddy.exe build-info` (beim Build) | – |
| SharpZipLib | MIT | ✔ gebündelt (NuGet) | Export-ZIP mit Passwort (AES-256) | NuGet |
| PHP (NTS x64) | PHP License 3.01 | ✔ Download in der App | Module → Herunterladen → `data\modules\php` | `releases.json` von windows.php.net (SHA-256) |
| weitere PHP-Versionen | PHP License 3.01 | ✔ Download in der App (ab 0.0.9) | Abhängigkeiten → PHP 8.x → `data\modules\php-8.x`, eigener `php-cgi` je Version | `releases.json` (SHA-256) |
| Xdebug | Xdebug License | ✔ Download in der App (ab 0.0.9) | → `data\modules\xdebug\php_xdebug-<x.y>.dll` | xdebug.org (HTTPS; SHA-256, wenn auf der Seite angegeben; DLL-Header geprüft) |
| VC++ Runtime | Microsoft Redistributable | ✔ Download in der App (mit PHP, nur wenn sie fehlt) | Installer mit Admin-Abfrage | aka.ms (Authenticode-Signatur Microsoft) |
| Node.js LTS | MIT | ✔ Download in der App | Module/Abhängigkeiten → Herunterladen → `data\modules\node` | `nodejs.org/dist/index.json` + `SHASUMS256.txt` (SHA-256) |
| uv | MIT/Apache-2.0 | ✔ Download in der App | → `data\modules\python` | GitHub Releases `astral-sh/uv` (SHA-256-Digest, Fallback `.sha256`) |
| Python | PSF | ✔ über uv | uv installiert beim ersten Start nach `data\runtimes\python` | python-build-standalone (von uv geprüft) |
| Mailpit | MIT | ✔ Download in der App | Module → Herunterladen → `data\modules\mailpit` | GitHub Releases (SHA-256-Digest der API, Fallback `checksums.txt`) |
| MariaDB | GPL-2.0 | ✔ Download in der App (**nie gebündelt**) | → `data\modules\mariadb`, Daten in `data\mariadb\data`, eigenständiger Prozess | REST-API `downloads.mariadb.org` (neueste stabile LTS, SHA-256) |
| Apache httpd | Apache-2.0 | zurückgestellt | Caddy + PHP deckt die Fälle ab | – |
| Go | BSD-3-Clause | nicht nötig | Go-Apps sind fertige EXE-Dateien | – |

## Release-Varianten (ab v0.1)

| Paket | Inhalt |
| --- | --- |
| `webit_v<ver>_portable.zip` | WEBit, Caddy, (ab v0.2) PHP + VC++ Runtime, Mailpit |
| `webit_v<ver>_full.zip` | zusätzlich Apache, Node.js LTS, uv + Python |
| `webit_v<ver>_setup.exe` | Installer mit Komponentenauswahl |

## Runtime-Manager (Ausbaustufe, geplant)

```
Katalog (signiert, aus dem WEBit-Repository)
   │ Runtimes, Provider, erlaubte Versionen, gepinnte Hashes
   ▼
Provider je Runtime ──► offizieller Index (live)
   ▼
Download → Prüfsumme → Entpacken (Staging) → Post-Install → Smoke-Test → Aktivieren
```

- Katalog als Release-Asset, Ed25519-signiert; öffentlicher Schlüssel in WEBit eingebettet.
- Schnittstelle `IRuntimeProvider` (`ListAvailable`, `Resolve`, `Download`, `Verify`, `Install`, `Test`, `Remove`).
- Lizenzdialog vor dem ersten Download, Lizenzdatei wird automatisch nach `licenses\third-party\` geschrieben.
- Proxy-Unterstützung (System-Proxy, Standard-Anmeldedaten), Download-Cache, Fortsetzen nach Abbruch.
- Mehrere Versionen parallel unter `bin\<runtime>\<version>\`; Entfernen gesperrt, solange eine Site sie nutzt.
- Offline-Paket exportieren/importieren für Server ohne Internet.

## Lizenzregeln

- Gebündelt werden nur permissiv lizenzierte Komponenten, jeweils mit vollständigem Lizenztext in `licenses\third-party\`.
- GPL-Software (MariaDB) wird nicht gebündelt, sondern vom Anwender über den Runtime-Manager aus der offiziellen Quelle geladen und als eigenständiger Prozess gestartet.
- `build\thirdparty.json` ist die einzige Quelle für gebündelte Versionen; `publish.ps1` bricht ab, wenn eine Lizenzdatei fehlt.

*Keine Rechtsberatung.*
