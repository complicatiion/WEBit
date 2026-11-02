# Changelog

Alle Änderungen an WEBit in einer Datei, neueste Version oben.
Format nach [Keep a Changelog](https://keepachangelog.com/de/1.1.0/), Versionierung nach [SemVer](https://semver.org/lang/de/).

## [0.0.9] – 2026-10-28

Mehrere PHP-Versionen, Xdebug, MariaDB-Backups mit Wiederherstellung, Export mit Passwort, Update-Prüfung für WEBit, automatischer Build.

### Hinzugefügt

- **Mehrere PHP-Versionen**: unter Abhängigkeiten zusätzliche Versionen (z. B. PHP 8.2, 8.3) neben der Standardversion installieren (`data\modules\php-8.2`, windows.php.net mit SHA-256). Site-Editor → **PHP-Version** je Site; jede Version läuft als eigener `php-cgi` (Ports `47206–47209`, nur `127.0.0.1`) und startet nur, wenn eine laufende Site sie braucht. Composer (`Pakete installieren`) nutzt die PHP-Version der Site.
- **Xdebug** als Download-Modul (DLLs der aktuellen Version von xdebug.org für jede PHP-Version). Site-Editor → **Xdebug** je Site: die Site läuft über einen eigenen `php-cgi` mit Xdebug (`start_with_request=yes`), andere Sites bleiben ohne Debugger. IDE-Port in Einstellungen → PHP (Standard `9003`).
- **MariaDB-Backups**: Datenbanken-Seite zeigt alle Backups (manuell und automatisch) mit **Wiederherstellen** (ersetzt die Datenbank, Benutzer und Rechte bleiben) und **Löschen**. Einstellungen → **Datenbank-Backups**: automatische Backups je Datenbank (**standardmäßig aus**), Intervall in Stunden, Anzahl aufbewahrter Backups; Ablage `data\backups\auto`.
- **Export mit Passwort**: Export-ZIP optional AES-256-verschlüsselt (öffnet mit 7-Zip, WinRAR, WinZip); beim Import wird das Passwort abgefragt.
- **Adminer im Netzwerk** (Einstellungen → Netzwerk & Ports): standardmäßig nur `127.0.0.1`, auch wenn MariaDB aus dem Netzwerk erreichbar ist.
- **Update-Prüfung für WEBit**: Abhängigkeiten → Card **WEBit** mit installierter und neuester Version (GitHub-Releases); **Aktualisieren** öffnet die Release-Seite.
- **GitHub Actions** (`.github/workflows/build.yml`): Build und Tests bei jedem Push und Pull Request; bei einem Versions-Tag zusätzlich portables ZIP, Inno-Setup-Installer und Release.
- Tests für HTTPS-Konfiguration, PHP-Versionen, Export/Import (mit Passwort), `wp-config.php`-Anpassung, Projektordner, Backup-Namen und php.ini (neues Testprojekt `WEBit.Service.Tests`).
- Lizenzen: Liste der in Caddy einkompilierten Go-Module (`licenses\third-party\CADDY-MODULES.md`, beim Build aus `WEBit-Caddy.exe build-info` erzeugt) mit Link zu Lizenz und Copyright je Modul; Lizenztexte MIT, BSD-2/3-Clause, ISC; Xdebug, SharpZipLib.
- Steuerprotokoll Version 9 (`getBackups`, `restoreBackup`, `deleteBackup`).

### Geändert

- Abhängigkeiten: Untertitel in den Cards entfernt.
- Datenbanken: Hinweis „unter Module installieren und einschalten“ entfernt.
- Über: Button zur Website entfernt.
- Drag & Drop nutzt die neue `DataTransfer`-API von Avalonia (keine veralteten Aufrufe mehr).
- Backup aller Datenbanken heißt `all-databases-<Zeit>.sql` (bisher `mariadb-…`, wird weiter erkannt).

### Behoben

- Build-Fehler aus 0.0.8: `??` zwischen `List<string>` und `string[]` (`WebitSettings`), zwischen `IOrderedEnumerable` und Sammlungsausdruck (`TransferViewModel`), doppelter Variablenname in `PhpIni`.

## [0.0.8] – 2026-10-28

WordPress und Laravel per Vorlage, lokales HTTPS, Datenbanken-Seite mit Adminer, PHP-Einstellungen, Export/Import, QR-Code, Auto-Reload.

### Hinzugefügt

- **Vorlage WordPress** (Sites → Vorlage): WP-CLI lädt WordPress, WEBit legt Datenbank und Benutzer in MariaDB an, schreibt `wp-config.php` (Adresse folgt dem Aufruf: IP, Hostname, Port, HTTPS) und installiert mit Admin-Konto; Zugangsdaten im Dialog, danach öffnet WP-Admin. Fehlende Module werden zum Download angeboten; bei Fehlern wird alles wieder entfernt.
- **Vorlage Laravel**: `composer create-project laravel/laravel` (SQLite), Site auf `public`, `APP_URL` gesetzt.
- **Module Composer, WP-CLI, Adminer** (Download mit Prüfsumme). Adminer läuft intern auf `127.0.0.1:47205` über PHP; **Adminer** auf der MariaDB-Card und auf der neuen Seite öffnet mit Server und Benutzer, das Root-Passwort liegt in der Zwischenablage.
- **Seite „Datenbanken“**: alle MariaDB-Datenbanken mit Größe und zugehörigen Sites; Adminer, Backup einzeln oder alle, Löschen (mit Benutzer), neue Datenbank, Root-Passwort, Backup-Ordner.
- **HTTPS** (Einstellungen → HTTPS): Caddys lokale Zertifizierungsstelle „WEBit Local CA“, HTTPS je Bindung (Site-Editor → Adresse). Stammzertifikat **auf diesem PC vertrauen** (UAC), **Vertrauen entfernen**, **Exportieren** für andere Geräte. URLs mit `https://`, Port 443 ohne Angabe.
- **PHP-Einstellungen** (Einstellungen → PHP): memory_limit, upload_max_filesize, post_max_size, Laufzeit, Fehleranzeige, OPcache, Erweiterungen einzeln (aus `ext\` der installierten Version), **Standard**.
- **Export & Import** (Einstellungen): ausgewählte Sites als ein ZIP mit Dateien, Datenbanken (Dump), optional Paketen und Einstellungen; Import legt Ordner, freie Ports und neue Datenbanken an und passt `wp-config.php`/Laravel-`.env` an. Site-Card: **Exportieren …**.
- **QR-Code** der Site-Adresse (Site-Card → QR-Code), Adresse wählbar.
- **Auto-Reload**: zusätzlich alle N Sekunden neu laden, Verzögerung nach Änderungen, **Pause** in der Live-Ansicht; Live-Ansicht auch für Node.js-, Python- und Proxy-Sites.
- **Ordner folgt dem Site-Namen** bei neuen Sites (`www\<Name>`); existiert er, Dialog: vorhandenen nutzen oder neuer Ordner (Vorschlag „Name-2“).
- **Farben**: 16 Vorgaben, erste Auswahl **Zufall** (Standard, möglichst unbenutzte Farbe – auch für Schnelltest- und automatische Sites); gewählte Farbe mit Ring in Akzentfarbe.
- Site-Card: **WP-Admin öffnen**, **Datenbank in Adminer**, **Pakete installieren** auch für Laravel (`composer install`). Site-Editor: Feld **Datenbank**.
- Steuerprotokoll Version 8.

### Geändert

- Netzwerk: Symbole und Texte auf einer Linie, kleinerer Aktualisieren-Button, Spaltenköpfe in Akzentfarbe (auch Datenbanken).
- Abhängigkeiten: Status als farbige Pill ohne Punkt, Art und Status als gleich breite Pills rechts; Hinweis „noch nicht nach Updates gesucht“ entfällt.
- „Alle stoppen“-Dialog: roter Button mit Stopp-Symbol.
- Tray-Fenster (Linksklick) mit abgerundeten Ecken.
- Sprache heißt überall **German**.
- Einstellungen: Card heißt wieder **Windows Service** / **Windows-Dienst**.
- Live-Reload-Optionen im Reiter Allgemein (Abschnitt Live-Ansicht).

### Sicherheit

- Automatische Sites überspringen Ordner, die eine Site enthalten (Laravel `…\public`) oder gerade von Vorlage/Import befüllt werden; `.env`, `.env.*` und `.htpasswd` sind bei neuen Sites standardmäßig ausgeblendet.
- Import: Datenbank-Dump läuft als Benutzer der neuen Datenbank im Sandbox-Modus (keine Shell-Befehle, keine anderen Datenbanken; Dumps mit `DEFINER`-Triggern anderer Konten schlagen dann fehl); Node.js-/Python-Sites werden ausgeschaltet importiert; Pfade bleiben im Zielordner.
- Vorlagen und Import räumen bei Fehlern oder Abbruch Ordner und Datenbanken wieder auf und überschreiben nie eine vorhandene Site.
- Schlüssel der lokalen CA nur für SYSTEM, Administratoren und das Dienstkonto; vor „Vertrauen“ zeigt ein Dialog den Fingerabdruck.
- Ports von WEBit selbst (Module, Adminer, Admin-API) sind für Sites gesperrt.

## [0.0.7] – 2026-10-28

MariaDB, Node.js und Python (uv) als Download-Module, neue Site-Typen Node.js, Python und Reverse Proxy.

### Hinzugefügt

- **MariaDB-Modul**: Download der neuesten stabilen LTS von `downloads.mariadb.org` (SHA-256, GPL-2.0 – nie gebündelt). Port `47204`, standardmäßig nur `127.0.0.1`; **Einstellungen → Netzwerk & Ports → MariaDB aus dem Netzwerk erreichbar** bindet an alle Adressen. Datenordner `data\mariadb\data` wird beim ersten Start angelegt und bleibt bei Updates und Entfernen erhalten; Root-Passwort wird erzeugt und mit DPAPI (rechnergebunden) gespeichert. Card-Aktionen **Root-Passwort** (kopieren), **Neue Datenbank** (Datenbank + gleichnamiger Benutzer mit erzeugtem Passwort), **Backup** (`mariadb-dump` nach `data\backups`). Sauberes Herunterfahren, nach Versionswechsel automatisch `mariadb-upgrade`. Log `logs\mariadb.log`, Prozess `WEBit-MariaDB.exe`.
- **Node.js-Modul**: neueste LTS von nodejs.org (`SHASUMS256.txt`).
- **Python-Modul (uv)**: uv von GitHub (SHA-256); Python-Versionen installiert uv nach `data\runtimes\python`, Paket-Cache in `data\cache\uv`.
- **Site-Typ Node.js-App**: Startbefehl automatisch (`npm run dev`/`start`/`serve`, `main`, `server.js` …), Port als `PORT`, Vite/Next.js/Astro/Nuxt/Angular/webpack zusätzlich mit `--port`/`--host`; fehlt `node_modules`, läuft vorher `npm install`. WebSockets/HMR durch Caddy.
- **Site-Typ Python-App** über `uv run`: Django, FastAPI/Starlette (uvicorn), Flask, einfache Skripte; `pyproject.toml` und `requirements.txt` werden genutzt, Python-Version pro Site.
- **Dev-Server oder Dauerbetrieb** pro App-Site (Neustart nach Absturz mit Backoff), optional **Neustart bei Dateiänderungen**, eigener Startbefehl und Umgebungsvariablen. Interner Port pro App aus `47210–47289`; „Läuft“ erst, wenn die App antwortet.
- **Site-Typ Reverse Proxy** auf eine beliebige Adresse (`http(s)://host:port/`).
- Site-Card: Badge Dev-Server/Dauerbetrieb, **App neu starten**, **Pakete installieren**; Logs zeigen die App-Ausgabe (`logs\sites\<id>\app.log`). Tray: **App neu starten**.
- Ordner per Drag & Drop erkennt Node.js- und Python-Projekte.
- Abhängigkeiten und Über: MariaDB, Node.js, uv, Python mit Lizenz und Quelle; Lizenzhinweise in `licenses\third-party\`.
- Steuerprotokoll Version 7 (`restartApp`, `installApp`, `getDbCredentials`, `createDatabase`, `backupDatabase`).

### Geändert

- Der erste Download eines Moduls schaltet es ein (Updates lassen den Schalter unverändert).
- „Fehlende herunterladen“ lädt nur benötigte Module (PHP, Mailpit); MariaDB, Node.js und Python werden einzeln geladen. Nicht installierte optionale Module erscheinen nicht in Übersicht und Tray.
- Im Windows-Dienst erhält `NT SERVICE\WEBit` für App-Ordner Änderungsrechte (Pakete werden in den Projektordner installiert).
- Ordneranalyse überspringt `node_modules`, `.venv` und `.git`.
- Apache httpd entfällt aus der Planung – Caddy mit PHP deckt die Fälle ab.

### Sicherheit

- Datenordner `%ProgramData%\WEBit` ohne geerbte Benutzerrechte: Zugriff nur SYSTEM, Administratoren, `NT SERVICE\WEBit`, „WEBit Admins“ und der installierende Benutzer (Desktop-Modus: nur dieser Benutzer statt aller Benutzer). Schützt MariaDB-Passwort, Backups und Site-Einstellungen und verhindert untergeschobene Modul-Programme.
- Versionen heruntergeladener Module nur aus `.version`, ohne die Programme auszuführen.
- MariaDB: Systemnamen (`mysql`, `sys`, `root` …) als Datenbank-/Benutzername gesperrt, vorhandene Benutzer werden nicht überschrieben; Client-Werkzeuge mit `--no-defaults`.

## [0.0.6] – 2026-10-28

Feinschliff der Oberfläche, Log- und Benachrichtigungseinstellungen, Caddy wird automatisch bereitgestellt.

### Hinzugefügt

- **Caddy automatisch**: Fehlt `runtime\caddy\caddy.exe`, lädt der erste Build unter Windows Caddy selbst (offizielles Release, SHA-256) – „Web server engine not found“ nach einem Quellcode-Build entfällt. Ohne Internet: **Abhängigkeiten → Caddy → Installieren** (Download nach `data\modules\caddy`).
- **Einstellungen → Logs**: Dienst-Log an/aus, Log-Stufe (Debug/Information/Warning/Error), Zugriffslogs pro Site, PHP-/Mailpit-Logs, Rotation (Größe, Anzahl), Aktualisierungsintervall der Logs-Seite, **Logs leeren**, **Log-Ordner öffnen**. Änderungen wirken ohne Neustart.
- **Einstellungen → Benachrichtigungen**: an/aus, nur Fehler, Anzeigedauer und Anzeigedauer bei Fehlern, Test-Button.
- **Abhängigkeiten**: Installieren/Deinstallieren pro Komponente – auch die Visual-C++-Laufzeit (Microsoft-Installer, UAC) und ein heruntergeladenes Caddy.
- **Übersicht**: Hinweise einzeln oder alle ausblenden (kehren zurück, wenn sich etwas ändert).
- Sanfte Animationen: Seitenwechsel, Reiter im Site-Editor, Benachrichtigungen.
- Steuerprotokoll Version 6 (`clearLogs`, Log-Einstellungen).

### Geändert

- Einstellungen: einheitliches Raster – Beschriftung links, Steuerelemente rechts in fester Breite; breitere Sprachauswahl; Schalter rechtsbündig.
- „Windows-Dienst“ heißt jetzt **Verwaltung**: Status als farbige Pill, **Starten/Stoppen** (Windows-Dienst oder Desktop-Modus), **Deinstallieren**, **Neu starten**, **Einstellungsordner öffnen**, **Dienst installieren**.
- Übersicht: **Alle starten** grün, **Alle stoppen** rot, **Neue Site** in Akzentfarbe (auch auf der Sites-Seite); Sites-Zähler in Akzentfarbe, Gestoppt rot, Fehler orange; Modus, Laufzeit, Host und www-Ordner als Pills.
- Tray-Menü: sichtbare Hover-Markierung; Alle starten (grün), Alle stoppen (rot) und Neue Site (Akzent) mit farbigen Symbolen.
- Module und Abhängigkeiten: zwei rechtsbündige Button-Zeilen (Download/Update/Installieren oben, Log/Ordner/Neustart bzw. Quelle/Ordner unten), einheitliche Button-Breiten.
- Netzwerk: feste Spalte für Adressen (Adapternamen bündig), Port-Prüfung mit Buttons unten rechts.
- Bestätigungsdialoge (z. B. „Alle stoppen“) mit zentriertem Symbol, Text und Buttons.
- Benachrichtigung: Symbol, Text und Schließen-Button auf einer Linie, Breite begrenzt.
- Über: „Hinweis:“ vor dem Markenhinweis.

## [0.0.5] – 2026-10-27

Eigene Seite „Abhängigkeiten“, deutlich schnellerer Seitenwechsel, Versionskonflikt mit altem Dienst lösbar.

### Hinzugefügt

- **Seite „Abhängigkeiten“**: Caddy (enthalten), PHP, Mailpit (Download) und die Visual-C++-Laufzeit (Windows-Komponente) mit Zustand, installierter und neuester Version, Pfad, Lizenz und Quelle. Aktionen: **Installieren**, **Aktualisieren**, **Entfernen** (heruntergeladene Dateien), **Nach Updates suchen**, **Alle aktualisieren**, **Fehlende Module herunterladen**, Ordner und Quelle öffnen. Die VC++-Laufzeit wird geladen und danach mit Administratorrechten installiert.
- **Versionskonflikt** („unterschiedliche Protokollversionen“): Meldung zeigt beide Versionen; **Durch diese Version ersetzen** registriert den Windows-Dienst neu (UAC) bzw. beendet einen alten Desktop-Modus und startet den aktuellen.
- **Desktop-Modus-Schalter** in den Einstellungen (Dienst-Card).
- Seitensymbol in Akzentfarbe links neben jeder Überschrift.
- Prozesse eindeutig benannt: `WEBit-Caddy.exe`, `WEBit-PHP.exe`, `WEBit-Mailpit.exe`; Dienst „WEBit Web Server“, Prozessbeschreibung „WEBit Service“.
- Steuerprotokoll Version 5 (`removeModule`, `checkUpdates`, Komponentenstatus).

### Geändert

- Site-Editor und Textanzeige öffnen maximiert; alle WEBit-Fenster mit dunkelblauer Titelleiste. Bestätigungsdialoge bleiben kompakt.
- Status-Pill komplett in Statusfarbe (ohne Punkt), Aktualisieren-Button kompakter.
- Eingeklappte Seitenleiste: quadratische Auswahlmarkierung.
- Module: Buttons immer am unteren Rand der Card.
- URLs und Logs: Suchfeld bzw. Quellenauswahl schrumpfen, Checkbox-Texte umbrechen – auf kompakten Displays wird nichts mehr abgeschnitten oder überlagert.
- Karte „Noch keine Sites“ ohne eigenen „Neue Site“-Button (oben rechts reicht).

### Behoben

- Langsamer Seitenwechsel: Seiten bleiben nach dem ersten Besuch im Fenster und werden nur ein-/ausgeblendet (kein Neuaufbau, kein erneutes Styling).
- Statusmeldungen werden gebündelt: der Dienst sendet höchstens alle 200 ms, die Oberfläche wendet nur den neuesten Stand an – UI-Thread und Tray-Menü reagieren sofort.

## [0.0.4] – 2026-10-27

Abhängigkeiten per Klick statt im Paket: nur Caddy wird mitgeliefert, PHP und Mailpit lädt WEBit bei Bedarf.

### Hinzugefügt

- **Download-Funktion für Module**: Button **Herunterladen** auf der Modul-Card (Modulseite, Übersicht, Tray-Menü) und **Fehlende Module herunterladen** für alle auf einmal. **Aktualisieren** lädt die neueste Version erneut.
  - PHP: neueste Version mit NTS-x64-Build aus `releases.json` von windows.php.net, SHA-256 geprüft.
  - Mailpit: neuestes GitHub-Release, SHA-256 aus dem Asset-Digest (Fallback `checksums.txt`).
  - Visual-C++-Laufzeit für PHP: nur wenn sie fehlt, von aka.ms, Microsoft-Authenticode-Signatur geprüft; die App fragt und startet den Installer mit Administratorrechten.
- Fortschrittsanzeige auf der Card (Prozent, Prüfen, Installieren), Meldung bei Erfolg oder Fehler.
- Ablage unter `data\modules\<modul>\` (Setup: `C:\ProgramData\WEBit\modules`); manuell abgelegte Dateien in diesen Ordnern werden ebenso erkannt. Downloads laufen im Dienst (Systemproxy wird verwendet), alte Version wird erst nach erfolgreicher Prüfung ersetzt.
- Steuerprotokoll Version 4 (`downloadModule`, Download-Status im Modulstatus).

### Geändert

- Paket und Setup enthalten nur noch Caddy (deutlich kleiner); `fetch-thirdparty.ps1` lädt nur Caddy, das Setup installiert keine VC++-Laufzeit mehr.
- Deinstallation beendet auch Prozesse aus `data\modules`.
- Datenschutz-Hinweise ergänzt: Verbindungen ins Internet nur beim Klick auf *Herunterladen*.

### Sicherheit

- Der VC++-Installer wird vor dem Start mit Administratorrechten in einen privaten Temp-Ordner kopiert und dort auf eine gültige Microsoft-Signatur geprüft.
- Module und Caddy werden nur noch im Programmordner, im Datenordner und (Entwicklung) im Repository mit `WEBit.slnx` gesucht – nicht mehr in beliebigen übergeordneten `runtime\`-Ordnern.
- Setup entfernt beim Update die früher mitgelieferten Ordner `bin\php`, `bin\mailpit`, `bin\vcredist`.

## [0.0.3] – 2026-10-27

Schnelltest von Webprojekten: HTML/Bootstrap- und PHP-Sites ablegen und lokal live testen, mit automatischem Neuladen.

### Hinzugefügt

- **PHP-Modul** (PHP 8.5 NTS x64, `bin\php\`): `php-cgi` als FastCGI auf `127.0.0.1:47203`, von Caddy wie `php_fastcgi` angebunden (Front-Controller `index.php`, Verzeichnis-Umleitung). `php.ini` wird bei jedem Start erzeugt (Fehler im Browser, OPcache ohne Verzögerung, `mail()` an Mailpit, eigene Ergänzungen in `php.user.ini`). Ohne PHP-Modul wird kein PHP-Quelltext ausgeliefert (503).
- **Site-Typ PHP**; die Ordneranalyse erkennt `.php`-Dateien und wählt den Typ automatisch.
- **Live-Reload** pro Site: Dateiänderungen (FileSystemWatcher, entprellt) erhöhen `state.json`; die Live-Ansicht `/__webit/live.html` lädt die Seite neu, CSS-Änderungen ohne Neuladen. Alternativ `<script src="/__webit/livereload.js">`. Live-Sites senden `Cache-Control: no-store`.
- **Schnelltest**: Ordner aufs Fenster ziehen → Site mit Live-Reload wird angelegt und im Browser geöffnet. Neue Ordner im `www`-Ordner werden automatisch zu Sites (abschaltbar; gelöschte automatische Sites werden nicht neu angelegt).
- Für Ordner außerhalb des Datenordners erhält das Dienstkonto `NT SERVICE\WEBit` automatisch Leserechte (eigene Ordner, ohne Administratorrechte).
- **Installer**: Auswahl **Windows-Dienst** oder **Desktop-Modus** (Desktop-Modus startet automatisch mit dem Tray, Datenordner für Benutzer beschreibbar, vorhandener Dienst wird entfernt); Tray-Autostart optional; Visual-C++-Laufzeit wird bei Bedarf installiert.
- Einstellung „Desktop-Modus automatisch starten“; Schalter „Schnelltest“ und PHP-FastCGI-Port in den Einstellungen.
- `fetch-thirdparty.ps1`: Anbieter `php` (windows.php.net `releases.json`, SHA-256) und `download` (VC++-Laufzeit, Authenticode-Prüfung).
- Titelleiste zeigt „WEBit vX.Y.Z“ und ist dunkelblau (Windows 11; Dark-Mode-Titelleiste ab Windows 10 20H1).

### Geändert

- Über-Seite: Buttons in Produkt- und Lizenz-Card zentriert, Copyright in Akzentfarbe, Texte gekürzt, Komponententabelle mit festen, linksbündigen Spalten; PHP und VC++-Laufzeit ergänzt.
- Karte „Dienst nicht erreichbar“: alle Buttons gleich breit und zentriert.
- Einstellungen: Beschriftungen „SMTP“ und „UI“ über den Mailpit-Port-Feldern.
- Seitenansichten werden einmal erzeugt und wiederverwendet (schnellerer Seitenwechsel).
- Steuerprotokoll Version 3. Projektpaket enthält den Ordner `webit_vX.Y.Z`.

### Behoben

- Zäher Seitenwechsel, solange der Dienst nicht erreichbar ist: `NamedPipeClientStream.ConnectAsync` belegte bei fehlendem Dienst dauerhaft CPU; der Verbindungsaufbau prüft jetzt zuerst, ob die Pipe existiert.
- Maximierter Start: Fenster wird erst normal angezeigt und danach maximiert – die Titelleiste liegt nicht mehr teilweise oberhalb des Bildschirms.
- Fehlerzähler der Module wird nach stabiler Laufzeit zurückgesetzt.
- Start aus Visual Studio: „WEBit.Service.exe wurde nicht gefunden“ beim Desktop-Modus. WEBit.App baut den Dienst jetzt immer mit (Projektverweis ohne Assembly) und kopiert ihn samt `bin\*`-Modulen neben `WEBit.exe`.
- Statische Sites liefern nie PHP-Quelltext aus (`*.php` ausgeblendet); automatische Sites werden zu PHP-Sites, sobald `.php`-Dateien auftauchen.
- Live-Reload ignoriert Schreibordner und -dateien von Anwendungen (cache, logs, storage, sessions, uploads, `.log`, `.sqlite` …) – keine Neulade-Schleife.
- PHP-Sites behalten ihren Zeichensatz (UTF-8) bei Zeichensatz „Automatisch“.
- Caddy leitet `*.php` nur an PHP weiter, wenn das Modul wirklich läuft; sonst 503 mit Hinweis auf der Card.
- „Neuer Ordner“ im Explorer: automatische Site entsteht erst mit Inhalt und folgt Umbenennungen.
- Desktop-Modus startet nach „Desktop-Modus beenden“ oder Dienst-Installation nicht ungewollt neu.
- Installer: Wechsel vom Dienst zum Desktop-Modus entfernt den alten Dienst vollständig.
- Bestehende Installationen: automatische Sites bleiben nach dem Update aus, bis sie eingeschaltet werden.

## [0.0.2] – 2026-10-27

Überarbeitung nach Laragon-Vorbild: Module mit Schaltern, Mailpit, volle Tray-Steuerung, saubere Deinstallation, Englisch als Standard.

### Hinzugefügt

- **Module mit Ein/Aus-Schalter**: Webserver (Caddy) und Mailpit lassen sich einzeln aktivieren; Zustand wird in `settings.json` (`modules`) gespeichert. Webserver aus → alle Sites gestoppt mit Hinweis.
- **Mailpit 1.32.0** gebündelt (`bin\mailpit\`): SMTP `127.0.0.1:47201`, Web-UI `127.0.0.1:47202`, Datenbank `data\mailpit\`, eigenes Log, automatischer Neustart mit Backoff. Ports in den Einstellungen änderbar.
- **Gemeinsames Job Object** für alle Kindprozesse (Caddy, Mailpit) – nichts bleibt nach dem Dienst-Ende zurück.
- **Steuerprotokoll v2**: `setModuleEnabled`, `restartModule`, Modulstatus im Dienst-Status.
- **Tray-Menü** steuert die gesamte App: Sites (öffnen, URL/Nachricht kopieren, starten/stoppen, bearbeiten, Ordner, Logs), Module (ein/aus, neu starten, öffnen, Log), Alle starten/stoppen, Neue Site, www-Ordner, URLs, Logs, Einstellungen, Sprache, Dienst, Über, Beenden – jeweils mit Icon.
- **Saubere Deinstallation**: `WEBit.Service.exe --uninstall [--purge]` entfernt Dienst, Gruppe „WEBit Admins“, Firewall-Regel, Autostart (alle Benutzer), laufende WEBit-Prozesse und optional alle Daten. In der App: **Einstellungen → WEBit deinstallieren** (Setup-Deinstaller oder Portable inkl. optionalem Löschen des Programmordners).
- **Installer** `build\installer\webit.iss` (Inno Setup 6) → `webit_v0.0.2_setup.exe`; richtet den Dienst ein und fragt bei der Deinstallation nach dem Löschen der Daten.
- **Übersicht** im Laragon-Stil: Alle starten/stoppen, www, URLs, Logs, Neue Site, Dienste mit Schaltern, Hinweise.
- **Über-Seite**: MIT-Lizenz erklärt (Erlaubt / Bedingungen / Einschränkungen), Lizenztext-Anzeige, Tabelle aller Komponenten mit Version, Lizenz und Link.
- Flaggen (2D, flach) in der Sprachauswahl; Text-Viewer-Dialog; Tests für Modul-Standardwerte und Site-IDs.

### Geändert

- **Englisch ist Standardsprache** (Fallback ebenfalls Englisch); alle Quellcode-Kommentare auf Englisch.
- App startet immer **maximiert**; Seitenleiste per Klick aufs Logo ein-/ausklappen (Standard ausgeklappt), unterer Einklapp-Button und Versionstext unter dem Logo entfernt.
- Untertitel unter den Seitenüberschriften und erklärende Hinweistexte entfernt.
- Karte „Dienst nicht erreichbar“: Logo, Text und Buttons zentriert, „Als Windows-Dienst installieren“ mittig darunter.
- Einstellungen: Dienst-Aktionen gebündelt, „Als Windows-Dienst installieren“ in einer Zeile mit „Benutzer-Einstellungen öffnen“.
- Sites und Module werden per Schalter statt Play/Stop-Button gesteuert.
- Tray-Icons verwenden das WEBit-Logo (mit Status-Punkt).
- Formulierungen ohne Intranet-Bezug; Platzhalter `company.local`.
- `fetch-thirdparty.ps1` prüft Downloads über den SHA-256-Digest der GitHub-API; `publish.ps1` auf Englisch, prüft Caddy und Mailpit und baut optional den Installer.

### Behoben

- Modul-/Site-Schalter springen bei Fehlern zuverlässig zurück.
- Doppelte Modul-Schlüssel mit abweichender Schreibweise in `settings.json` führen nicht mehr zu einem Fehler.
- Modul, das direkt nach dem Start endet, wird korrekt als Fehler erkannt und neu gestartet; Meldung zeigt den Exit-Code.
- Nicht installiertes Mailpit lässt sich nicht einschalten; Modul-Schalter werden thread-sicher gespeichert.
- Fehler-Hinweise (Toasts) werden rot mit Warn-Icon dargestellt.

## [0.0.1] – 2026-10-22

Erste Version: lauffähiges Grundgerüst mit Dienst, Oberfläche, Tray und statischen Sites.

### Hinzugefügt

- **Solution** `WEBit.slnx` mit den Projekten Core, Contracts, Platform.Windows, Service, App und Core.Tests; zentrale Versionierung (`Directory.Build.props`) und zentrale Paketverwaltung (`Directory.Packages.props`).
- **Dienst** `WEBit.Service.exe`
  - läuft als Windows-Dienst (`NT SERVICE\WEBit`, verzögerter Autostart, Neustart bei Fehlern) oder im Desktop-Modus
  - Einrichtung per `--install [--user DOMÄNE\Name]` / `--uninstall` inkl. Gruppe „WEBit Admins“, Dateirechten und Protokoll `logs\setup.log`
  - steuert Caddy als Kindprozess im Job Object, lädt die Konfiguration unterbrechungsfrei über die Admin-API (`127.0.0.1:47299`), Neustart mit Backoff
  - Site-Verwaltung: Anlegen, Bearbeiten, Löschen, Start/Stop einzeln und gesamt; bei Fehlern wird die betroffene Site isoliert, alle anderen laufen weiter
  - Prüfungen: Ordner, IP-Adresse am Host, belegte Ports mit Prozess (GetExtendedTcpTable), Windows-Portreservierungen (netsh), Konflikte zwischen Sites, gemiedene Ports
  - Portvorschlag im eigenen Bereich 47100–47199
  - Ordneranalyse (Startseite, FrontPage-Erkennung, Zeichensatz, Umfang)
  - URL-Prüfung (DNS + HTTP aus Sicht des Servers)
  - Logs: Dienst, Engine, Zugriffslog pro Site (lesbar formatiert)
  - Steuerkanal über Named Pipe `\\.\pipe\WEBit.Control` mit ACL, Push-Ereignisse an alle verbundenen Oberflächen
- **Oberfläche** `WEBit.exe` (Avalonia)
  - Dark Mode nach Palette 464B71 · 118AB2 · 7CD5C7 · F2F2ED, Akzentfarbe #118AB2 für Auswahl, Häkchen und Schalter
  - linke Seitenleiste (einklappbar): Übersicht, Sites, URLs, Module, Netzwerk, Logs, Einstellungen, Über
  - Site-Cards mit Status, URL, Ordner, Fehlerursache und Aktionen; Site-Editor mit Analyse, Live-Prüfung der Adresse und Deep-Links
  - URL-Verwaltung: Prüfen, Kopieren, Nachricht für Anwender, Export als Browser-Lesezeichen (HTML), Edge/Chrome-Gruppenrichtlinie (JSON), CSV, `.url`
  - System-Tray mit Status-Ampel, Schnellpanel (Linksklick) und Menü mit Icons (Rechtsklick)
  - Deutsch und Englisch, Umschaltung ohne Neustart
  - Dienst installieren/deinstallieren/starten/stoppen per UAC, Desktop-Modus starten/beenden
  - eine Instanz pro Benutzer-Session; Schließen minimiert in den Tray
- **Sites (statisch)**: Standarddokumente, Verzeichnisliste, Komprimierung (zstd/gzip), ausgeblendete Pfade (FrontPage-Systemordner), Zeichensatz-Modus (Auto/UTF-8/Windows-1252/ISO-8859-1), IP-Filter, Weiterleitungen (301/302), öffentliche URL
- **Build**: `build\fetch-thirdparty.ps1` (Caddy aus dem offiziellen Release mit Prüfsummen-Prüfung), `build\publish.ps1` (portables Release `webit_v0.0.1_portable.zip` + SHA-256)
- **Dokumentation** in `docs\`, **Lizenzen** in `licenses\` inkl. Drittanbieter
- **Tests** für Port-Regeln, Validierung, Caddy-Konfiguration und URL-Erzeugung

### Hinweise

- Avalonia 11.3 statt der im Konzept genannten Version 12 – bewährte, stabile API für die erste Version; Umstieg ist für v0.2 vorgesehen.
- Steuerprotokoll als schlankes JSON-Zeilenprotokoll über die Named Pipe statt StreamJsonRpc – keine zusätzliche Abhängigkeit.
- Release-Benennung: Projektpaket `webit_v0.0.1.zip`, portables Release `webit_v0.0.1_portable.zip`.
