# Terminal static asset provenance

The runtime files in this directory are vendored, unmodified distribution
assets from the official npm Registry. They are never loaded from a CDN and do
not add an application runtime dependency on npm or Node.js.

| File | npm package | Version | SHA-256 |
|---|---|---:|---|
| `xterm.js` | `@xterm/xterm` | 5.5.0 | `1F991AC3B4B283EBF96E60AE23A00A52765DD3A2E46FA6FDDA9F1AAB032F7495` |
| `xterm.css` | `@xterm/xterm` | 5.5.0 | `BA8E6985669488981CCF40C0CEFE3ABA80722CB6C92DE7AD628B0BD717FAF2B6` |
| `xterm-addon-fit.js` | `@xterm/addon-fit` | 0.10.0 | `BDAEFA370B1BFC42EE88D46FE6072400902A4D4B2D45CD93438DDA9B23C97089` |

Package integrity, shasums, official tarball URLs, tarball SHA-256 values, and
upstream repository details are recorded in `vendor-manifest.json`. The
manifest is an audit record only and is not loaded by the application.

Upstream: https://github.com/xtermjs/xterm.js
License: MIT (`../../../licenses/third-party/xterm-5.5.0-LICENSE.txt` and
`../../../licenses/third-party/xterm-addon-fit-0.10.0-LICENSE.txt` in the
published application root).
