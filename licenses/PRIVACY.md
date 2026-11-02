# Privacy

WEBit is **local-first**:

- **No telemetry, no account, no cloud.** WEBit does not send any usage data anywhere.
- **Module downloads on request only.** When you click *Download* on a module, the WEBit service connects to the official source (windows.php.net, api.github.com / github.com, aka.ms / download.visualstudio.microsoft.com). Only the HTTP request itself is sent (IP address, user agent `WEBit/<version>`).
- **No external resources in the app.** Fonts and icons are embedded.
- **Network connections** only occur
  - when browsers in your network access the sites you publish,
  - when you use **Check** on the URLs page (DNS lookups and HTTP requests from the server to your own sites, without proxy),
  - when you open a link (browser),
  - during the build, when `build\fetch-thirdparty.ps1` downloads Caddy from its official GitHub release.

## Data stored by WEBit

| Location | Content |
| --- | --- |
| `%ProgramData%\WEBit\config\` (portable: `data\config\`) | settings and one JSON file per site |
| `%ProgramData%\WEBit\logs\` | service log, engine log, setup log |
| `%ProgramData%\WEBit\logs\sites\<id>\access.log` | access log per site: time, client IP, method, host, path, status, size, duration and request headers as written by Caddy |
| `%AppData%\WEBit\ui.json` | interface settings per user (language, sidebar, message template) |

Access logs contain **IP addresses of clients** and may therefore be personal data. Logs are rotated automatically (10 MB, 5 files per site). Check your company policy on log retention; delete old logs in the `logs` folder if required.

The local control channel (named pipe) is restricted to administrators, the group *WEBit Admins* and users added during installation.
