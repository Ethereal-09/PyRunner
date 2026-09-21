# Offline code editor dependencies

`codemirror.bundle.js` is built only from the exact npm versions and integrity
records in `tools/editor-vendor/package-lock.json`. The application loads this
local bundle and never uses a CDN. Rebuild with `npm ci --ignore-scripts` and
`npm run build`, then update both SHA-256 values in `vendor-manifest.json`.

CodeMirror and its included Lezer packages are MIT-licensed. The applicable
license text is distributed at `licenses/third-party/codemirror-6-LICENSE.txt`.
