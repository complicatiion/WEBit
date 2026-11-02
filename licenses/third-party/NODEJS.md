# Node.js

- Project: https://nodejs.org · source: https://github.com/nodejs/node
- **Not bundled:** WEBit downloads the latest LTS release (`node-vX-win-x64.zip`) on request from `nodejs.org/dist` into `data\modules\node\`, verified with the SHA-256 checksum from `SHASUMS256.txt` of that release.
- License: **MIT** – https://github.com/nodejs/node/blob/main/LICENSE
- Node.js includes npm and further third-party components (V8, libuv, OpenSSL, ICU …) under their own licenses; they are listed in the `LICENSE` file of the downloaded version in `data\modules\node\`.
- Packages that your projects install (`node_modules`) are governed by their own licenses.
