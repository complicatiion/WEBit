# Funktionen im Detail

Stand: v0.0.1

## Dienst

| Funktion | Beschreibung |
| --- | --- |
| Betriebsarten | **Windows-Dienst** (`WEBit Webserver`, Konto `NT SERVICE\WEBit`, verzögerter Autostart, Neustart nach Fehlern) oder **Desktop-Modus** (`WEBit.Service.exe --console`, läuft solange angemeldet). |
| Engine | Caddy als Kindprozess, gebündelt in einem Job Object. Konfiguration wird vollständig per `POST /load` geladen – Caddy übernimmt sie ohne Unterbrechung oder behält bei Fehlern die alte. |
| Fehlerisolierung | Schlägt das gemeinsame Laden fehl, werden Sites einzeln hinzugefügt. Nur die fehlerhafte Site bekommt Status „Fehler“ mit Ursache. |
| Neustart | Unerwartetes Beenden der Engine → automatischer Neustart nach 2/5/10/30 s. |
| Datenhaltung | `config\settings.json`, `config\sites\<id>.json` (eine Datei pro Site, atomar geschrieben), `logs\…`, `runtime\caddy\…`. |
| Steuerkanal | Named Pipe `\\.\pipe\WEBit.Control`, eine JSON-Nachricht pro Zeile, Push-Ereignis `statusChanged` an alle Oberflächen. |

## Sites

| Einstellung | Wirkung in Caddy |
| --- | --- |
| Bindung IP + Port | Server mit `listen`. Gleiche IP+Port mehrerer Sites → ein Server, Unterscheidung per Hostname. |
| Hostname | `host`-Matcher; Zugriffslog per `logger_names`. |
| Site-Ordner | `file_server.root` |
| Standarddokumente | `file_server.index_names` |
| Verzeichnisliste | `file_server.browse` |
| Ausgeblendete Pfade | `file_server.hide` (gilt in jeder Ordnertiefe) |
| Komprimierung | `encode` mit zstd und gzip |
| Zeichensatz | `headers` (deferred) setzt `Content-Type` nur für HTML-Antworten: *Auto* = `text/html` ohne Charset (Meta-Tag gilt), *Windows-1252*, *ISO-8859-1*; *UTF-8* = Caddy-Standard |
| Erlaubte Netze | `remote_ip`-Matcher (negiert) → 403 |
| Weiterleitung | `static_response` 301/302 mit `Location` |
| Sicherheits-Header | `X-Content-Type-Options: nosniff`, `Referrer-Policy: same-origin`, `Server`-Header entfernt |

Neue Sites im www-Ordner erhalten beim ersten Speichern einen Ordner mit Platzhalterseite.

## Prüfungen

| Prüfung | Quelle |
| --- | --- |
| Name, Ordner, Bindungen, Hostname, Netze, URLs | Formatprüfung (verhindert Speichern) |
| Konflikte mit anderen Sites | gleiche IP+Port ohne/mit gleichem Hostnamen, Wildcard überschneidet konkrete IP |
| Gemiedene Ports | Liste in `PortPolicy` (Standard-, Entwicklungs- und Datenbank-Ports, dynamischer Bereich) |
| Port belegt | `GetExtendedTcpTable` mit Prozessname und PID; Listener der eigenen Engine werden ignoriert |
| Windows-Reservierung | `netsh int ipv4 show excludedportrange protocol=tcp` |
| IP am Host | Netzwerkadapter des Servers |
| Ordner erreichbar | Existenz und Leserecht aus Sicht des Dienstkontos |

## URL-Verwaltung

- Primäre URL: öffentliche URL › erste Bindung mit Hostname › erste Bindung. „localhost“ wird nie erzeugt, Port 80 wird weggelassen.
- Weitere URLs: jede Bindung, FQDN mit DNS-Suffix (bei Kurznamen), IP-Varianten bei Wildcard-Bindung, Deep-Links.
- Prüfen: DNS-Auflösung und HTTP-Status mit Antwortzeit – vom Server aus, ohne Proxy.
- Verteilen: URL, Nachricht (Vorlage mit `{SiteName}`, `{Url}`, `{Description}`, `{Links}`), HTML- und Markdown-Link, `.url`-Verknüpfung, Browser-Lesezeichen (Netscape-HTML), Edge/Chrome-Gruppenrichtlinie (`ManagedFavorites`/`ManagedBookmarks`), CSV.
- Ohne Auswahl gelten Aktionen für alle angezeigten Zeilen.

## Oberfläche

- Seitenleiste mit Module, Sites, URLs, Übersicht, Netzwerk, Logs, Einstellungen, Über – per Klick aufs Logo einklappbar, startet ausgeklappt; Fenster startet maximiert.
- Module und Sites mit Ein/Aus-Schalter.
- Jeder Button mit Icon links vom Text; Hauptaktion rechts, Abbrechen links daneben.
- Auswahl, Häkchen und Schalter in Akzentfarbe (#118AB2) über die Fluent-Palette.
- Hinweise (Toasts) unten rechts; Fehler bleiben länger sichtbar.
- Sprache Englisch (Standard) / Deutsch mit Flaggen, Umschaltung ohne Neustart. Meldungen des Dienstes werden als Code übertragen und in der Oberfläche übersetzt.

## System-Tray

| Aktion | Ergebnis |
| --- | --- |
| Icon | Status-Ampel: grün (alles läuft), gelb (Sites gestoppt), rot (Fehler/Engine), grau (Dienst nicht erreichbar) |
| Linksklick | Schnellpanel: Status, alle Sites mit URL kopieren / öffnen / Start-Stop, „Manager öffnen“ |
| Rechtsklick | Menü mit Icons: Status, Manager öffnen, Übersicht, Alle starten/stoppen, Sites ▸ (pro Site: öffnen, URL/Nachricht kopieren, Start/Stop, bearbeiten, Ordner, Logs), Module ▸ (ein/aus, neu starten, öffnen, Log), Neue Site, www-Ordner, URLs, Logs, Einstellungen, Sprache ▸, Dienst ▸ (installieren, neu starten, stoppen, starten, Desktop-Modus, neu verbinden), Über, Beenden |

Das Tray-Icon zeigt das WEBit-Logo mit Status-Punkt. Das Tray-Menü wird unter Windows von Avalonia selbst gezeichnet und folgt damit dem Dark Mode der Anwendung.

## Schnelltest

- Ordner aufs Fenster ziehen (auch eine Datei – dann zählt ihr Ordner): Site mit Live-Reload, Typ automatisch (PHP bei `.php`-Dateien), freier Port, Browser öffnet die Live-Ansicht.
- Neue Ordner im `www`-Ordner werden automatisch zu Sites (Einstellungen → Pfade → Schnelltest).
- Live-Reload: Neuladen bei jeder gespeicherten Änderung, CSS ohne Neuladen, Badge „Live“ auf der Card, „Öffnen“ startet die Live-Ansicht. Für eigene Seiten: `<script src="/__webit/livereload.js"></script>`.

## Abhängigkeiten

- Eigene Seite mit allen Komponenten: Caddy (enthalten), PHP und Mailpit (Download, entfernbar), Visual-C++-Laufzeit (Windows-Komponente, Installation mit UAC).
- Nach Updates suchen vergleicht die installierte mit der neuesten Version der offiziellen Quelle; „Alle aktualisieren“ lädt alle neueren Versionen.
- Prozesse: `WEBit-Caddy.exe`, `WEBit-PHP.exe`, `WEBit-Mailpit.exe`; Windows-Dienst „WEBit Web Server“.

## Module

- Nur Caddy ist enthalten. PHP, Mailpit, MariaDB, Node.js und Python (uv): **Herunterladen** auf der Card (der erste Download schaltet das Modul ein) bzw. **Fehlende Module herunterladen** – offizielle Quelle, Prüfsumme, Fortschritt, automatischer Start; **Aktualisieren** holt die neueste Version.

- **Webserver (Caddy)** – Schalter an/aus, Version, Pfad, Admin-Endpunkt, Log, Neustart.
- **Mailpit** – fängt Mails ab: SMTP `127.0.0.1:47201`, Web-UI `http://127.0.0.1:47202/`, Ports in den Einstellungen änderbar; eigenes Log `mailpit.log`, Neustart mit Backoff.
- **PHP** – PHP 8.5 über FastCGI (`127.0.0.1:47203`), Erweiterungen mbstring, intl, gd, curl, openssl, pdo_mysql, pdo_sqlite, zip u. a.; `php.ini` mit Entwicklungs-Einstellungen, eigene Ergänzungen in `runtime\php\php.user.ini`; Fehlerlog `php-error.log`.
- **MariaDB** – neueste LTS, Port `47204`, standardmäßig nur `127.0.0.1`; **Einstellungen → Netzwerk & Ports → MariaDB aus dem Netzwerk erreichbar** bindet an alle Adressen (Firewall-Port selbst freigeben). Datenordner `data\mariadb\data` bleibt bei Updates/Entfernen erhalten; Root-Passwort wird erzeugt und verschlüsselt (DPAPI, rechnergebunden) gespeichert. Card: **Root-Passwort** kopieren, **Neue Datenbank** (Datenbank + gleichnamiger Benutzer, Passwort erzeugt), **Backup** (`mariadb-dump` aller Datenbanken nach `data\backups`). Sauberes Herunterfahren über `mariadb-admin shutdown`, nach Versionswechsel automatisch `mariadb-upgrade`.
- **Node.js** – LTS von nodejs.org; Laufzeit für Node.js-Sites (Status „Bereit“, Anzahl laufender Apps).
- **Python (uv)** – uv von GitHub; uv installiert Python-Versionen und Pakete selbst (Cache in `data\cache\uv`).
- Apache httpd ist zurückgestellt – Caddy mit PHP reicht für die bisherigen Fälle.

## Vorlagen, Datenbanken, HTTPS (0.0.8)

- **WordPress** per WP-CLI vollautomatisch (Datenbank, `wp-config.php`, Admin-Konto), **Laravel** per Composer. Benötigt PHP, MariaDB (WordPress), WP-CLI bzw. Composer – fehlende Module bietet WEBit zum Download an.
- **Datenbanken**-Seite mit Adminer, Backup, Löschen; Adminer intern auf `127.0.0.1:47205`.
- **HTTPS** über die lokale CA von Caddy; Stammzertifikat einmal pro Gerät vertrauen (PC: Button, Handy: Export installieren). Eine Adresse/ein Port ist entweder HTTPS oder HTTP.
- **PHP-Einstellungen** und Erweiterungen in der Oberfläche; eigene Ergänzungen weiterhin in `runtime\php\php.user.ini`.
- **Export/Import** als ZIP (`webit-export.json`, `sites/<id>/files/`, `sites/<id>/database.sql`, optional `settings.json`); der Dienst schreibt/liest in `data\exports` bzw. `data\imports`, die App kopiert von/zu dem gewählten Ort.
- **QR-Code** der Site-Adresse für Tests auf dem Handy.
- **Auto-Reload** mit Intervall, Verzögerung und Pause.

## Node.js-, Python- und Proxy-Sites

- **Node.js-App**: Startbefehl wird erkannt – `npm run dev`, sonst `start`/`serve`, sonst `main` aus `package.json` bzw. `server.js`/`app.js`/`index.js`. Port kommt als `PORT` (und `HOST=127.0.0.1`); Vite, Next.js, Astro, Nuxt, Angular und webpack-dev-server bekommen `--port`/`--host` zusätzlich. Fehlt `node_modules`, läuft vorher `npm install`. HMR/WebSockets laufen durch Caddy.
- **Python-App** (über `uv run`): Django (`manage.py runserver`), FastAPI/Starlette (`uvicorn --reload`), Flask (`flask run --debug`), sonst `python app.py|main.py`. `pyproject.toml` → uv-Projekt, `requirements.txt` → `--with-requirements`; ohne beides lädt uv Flask/uvicorn/Django selbst. Python-Version pro Site wählbar.
- Eigener **Startbefehl** möglich (Node.js über `cmd`, Python über `uv run`), **Umgebungsvariablen** (KEY=VALUE).
- **Dev-Server** (Standard): nach einem Absturz bleibt die App aus (Fehler auf der Card). **Dauerbetrieb**: automatischer Neustart mit Backoff. Optional **Neustart bei Dateiänderungen** (`node_modules`, `.venv`, `dist` … ausgenommen).
- Interner Port pro App aus `47210–47289` (nur `127.0.0.1`), Caddy leitet die Site-Adresse dorthin weiter. Status „Läuft“ erst, wenn der Port antwortet.
- Card-Menü: **App neu starten**, **Pakete installieren** (`npm install` / `uv sync`); Log-Button zeigt die Ausgabe der App (`logs\sites\<id>\app.log`).
- **Reverse Proxy**: beliebige Zieladresse `http(s)://host:port/`; fremde Hosts erhalten ihren eigenen Host-Header.
- Apps erhalten `Host: 127.0.0.1:<port>` (Vite-`allowedHosts`, Django-`ALLOWED_HOSTS` im DEBUG passen ohne Einrichtung); die aufgerufene Adresse steht in `X-Forwarded-Host` (für absolute URLs: Django `USE_X_FORWARDED_HOST = True`, Flask `ProxyFix`). Flask läuft im Dev-Modus mit Reloader, aber ohne interaktive Debugger-Konsole; im Dauerbetrieb ohne Reloader.
- Antwortet eine App 5 Minuten lang nicht auf ihrem Port, zeigt die Card einen Fehler (App muss `PORT` nutzen und auf `127.0.0.1` lauschen). Apps, die `HOST` ignorieren und auf allen Adressen lauschen, sind am Caddy-IP-Filter vorbei direkt erreichbar – nur die Windows-Firewall schützt dann.
- Datenordner (`%ProgramData%\WEBit`) nur für SYSTEM, Administratoren, den Dienst und „WEBit Admins“ bzw. im Desktop-Modus den installierenden Benutzer; aus dem Datenordner geladene Module werden nie zur Versionsabfrage ausgeführt.

## Deinstallation

- Setup: Windows-Apps oder **Einstellungen → WEBit deinstallieren**; Abfrage, ob Daten gelöscht werden.
- Portable: **Einstellungen → WEBit deinstallieren** – Dienst, Gruppe, Firewall-Regel, Autostart; optional Daten und Programmordner.

## Grenzen von v0.0.3

- Statische Sites, PHP, Node.js, Python, Reverse Proxy und Weiterleitungen; kein Apache.
- Im Windows-Dienst laufen Apps als `NT SERVICE\WEBit` (Ordnerrechte setzt die App beim Speichern); für Dev-Server ist der Desktop-Modus bequemer.
- Kein HTTPS (geplant v0.3), keine Basic-Auth.
- Firewall-Regeln werden noch nicht automatisch angelegt.
- Port-Belegung nur für IPv4.
- Installer (Inno Setup) ohne Komponentenauswahl.

## PHP-Versionen, Xdebug, Backups, Build (0.0.9)

| Funktion | Umsetzung |
| --- | --- |
| Mehrere PHP-Versionen | Abhängigkeiten zeigt nach einer Update-Prüfung alle Zweige aus `releases.json`; Installation nach `data\modules\php-<x.y>`. Site-Editor → PHP-Version. Je Version ein `php-cgi` auf `47206–47209`, nur gestartet, solange eine laufende Site die Version nutzt; Ports bleiben stabil, solange der Prozess läuft. |
| Xdebug | Modul `xdebug` (eine DLL je PHP-Version). Schalter je Site: eigener `php-cgi` mit `xdebug.mode=debug,develop`, `start_with_request=yes`, IDE `127.0.0.1:<Port>` (Standard 9003). |
| MariaDB-Backups | Manuell (Datenbank oder alle) nach `data\backups`, automatisch je Datenbank nach `data\backups\auto` (Intervall, Anzahl; standardmäßig aus). Liste mit Wiederherstellen (Datenbank wird geleert und als root im Sandbox-Modus geladen, Benutzer/Rechte bleiben) und Löschen. Backups aller Datenbanken werden nicht in der App wiederhergestellt. |
| Export mit Passwort | SharpZipLib, AES-256 je Eintrag; Import erkennt verschlüsselte ZIPs und fragt nach dem Passwort. |
| Adminer im Netzwerk | Schalter, Standard aus: Adminer nur auf `127.0.0.1`, unabhängig vom MariaDB-Netzwerkzugriff. |
| WEBit-Update | Update-Prüfung fragt `releases/latest` von `complicatiion/WEBit` ab; Card „WEBit“ unter Abhängigkeiten. |
| CI | `.github/workflows/build.yml`: Build + Tests (Windows), bei Tag `v*` zusätzlich `build\verify.ps1` (portabel + Inno Setup) und Release. |
| Lizenzen Caddy | `CADDY-MODULES.md` aus `build-info` beim Build, Lizenztexte MIT/BSD/ISC. |

