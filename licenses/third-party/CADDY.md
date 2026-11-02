# Caddy

| | |
| --- | --- |
| Version | 2.11.4 (see `build/thirdparty.json`) |
| License | Apache License 2.0 |
| Copyright | Copyright 2015 Matthew Holt and The Caddy Authors |
| Source | https://github.com/caddyserver/caddy |
| Website | https://caddyserver.com |
| Shipped as | `bin\caddy\WEBit-Caddy.exe` (unmodified official release binary from `caddy_<version>_windows_amd64.zip`, only renamed) |
| Use in WEBit | Web server engine that serves all sites; controlled through its admin API on 127.0.0.1 |

WEBit does not modify Caddy. The binary is downloaded from the official GitHub release by `build/fetch-thirdparty.ps1` and verified against the published checksums. The original `LICENSE` and `README.md` of the release are shipped next to `WEBit-Caddy.exe`.

## Compiled-in libraries

Like every Go program, the Caddy binary contains third-party Go modules (mostly MIT, BSD and Apache-2.0 licensed),
plus the Go standard library (BSD-3-Clause). They are listed with version and a link to their license and copyright
notice in [CADDY-MODULES.md](CADDY-MODULES.md), generated during the build from:

```
bin\caddy\WEBit-Caddy.exe build-info
```

The command shows the exact modules of the installed binary at any time. Sources: https://github.com/caddyserver/caddy
(`go.mod`, `go.sum`). Common license texts: [LICENSE-MIT.md](LICENSE-MIT.md), [LICENSE-BSD-2-CLAUSE.md](LICENSE-BSD-2-CLAUSE.md),
[LICENSE-BSD-3-CLAUSE.md](LICENSE-BSD-3-CLAUSE.md), [LICENSE-ISC.md](LICENSE-ISC.md), [LICENSE-APACHE-2.0.md](LICENSE-APACHE-2.0.md).

Full license text: [LICENSE-APACHE-2.0.md](LICENSE-APACHE-2.0.md)

```
Copyright 2015 Matthew Holt and The Caddy Authors

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
```
