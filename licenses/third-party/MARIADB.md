# MariaDB Server

- Project: https://mariadb.org · source: https://github.com/MariaDB/server
- **Not bundled:** WEBit downloads the latest long-term-support release for Windows x64 on request (Modules or Dependencies → Download) from `downloads.mariadb.org` into `data\modules\mariadb\`, verified with the SHA-256 checksum published by the MariaDB Foundation.
- License: **GNU General Public License v2.0** (GPL-2.0) – https://github.com/MariaDB/server/blob/main/COPYING
- WEBit only starts the unmodified server as a separate process and talks to it over TCP. WEBit itself stays MIT-licensed; MariaDB is never redistributed by WEBit.
- The license files of the downloaded version are in `data\modules\mariadb\` (`COPYING`, `THIRDPARTY`).
- Your databases are stored in `data\mariadb\data\` and are kept when the module is updated or removed.
