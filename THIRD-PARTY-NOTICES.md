# Third-Party Notices

PyRunner includes or depends on the components below. Versions for NuGet
dependencies come from `PyRunner/packages.lock.json`. Static terminal asset
versions are pinned to official npm Registry packages and verified by package
integrity plus byte-for-byte SHA-256 comparison; see
`PyRunner/Terminal/wwwroot/DEPENDENCIES.md` and
`PyRunner/Terminal/wwwroot/vendor-manifest.json`.

| Component | Version | Upstream | License | License information |
|---|---:|---|---|---|
| CommunityToolkit.Mvvm | 8.2.2 | https://github.com/CommunityToolkit/dotnet | MIT | https://licenses.nuget.org/MIT |
| Dapper | 2.1.79 | https://github.com/DapperLib/Dapper | Apache-2.0 | https://licenses.nuget.org/Apache-2.0 |
| Microsoft.Data.Sqlite / Microsoft.Data.Sqlite.Core | 8.0.30 | https://github.com/dotnet/efcore | MIT | https://licenses.nuget.org/MIT |
| Microsoft.Extensions.DependencyInjection | 8.0.1 | https://github.com/dotnet/runtime | MIT | https://licenses.nuget.org/MIT |
| Microsoft.Extensions.DependencyInjection.Abstractions | 8.0.2 | https://github.com/dotnet/runtime | MIT | https://licenses.nuget.org/MIT |
| SQLitePCLRaw.bundle_e_sqlite3 and related SQLitePCLRaw packages | 2.1.12 | https://github.com/ericsink/SQLitePCL.raw | Apache-2.0 | https://licenses.nuget.org/Apache-2.0 |
| System.Memory | 4.5.3 | https://github.com/dotnet/runtime | MIT | https://licenses.nuget.org/MIT |
| Microsoft.WindowsAppSDK | 1.5.250108004 | https://github.com/microsoft/WindowsAppSDK | Microsoft Software License Terms | `licenses/third-party/Microsoft.WindowsAppSDK-LICENSE.txt` |
| Microsoft.Windows.SDK.BuildTools (build only) | 10.0.22621.3233 | https://aka.ms/WinSDKProjectURL | Microsoft Windows SDK License | https://aka.ms/WinSDKLicenseURL |
| Microsoft.Web.WebView2 SDK, bundled by Windows App SDK | 1.0.2210.55 | https://developer.microsoft.com/microsoft-edge/webview2/ | Microsoft Software License Terms | `licenses/third-party/Microsoft.Web.WebView2-LICENSE.txt` |
| @xterm/xterm (`xterm.js`, `xterm.css`) | 5.5.0 | https://github.com/xtermjs/xterm.js | MIT | `licenses/third-party/xterm-5.5.0-LICENSE.txt` |
| @xterm/addon-fit (`xterm-addon-fit.js`) | 0.10.0 | https://github.com/xtermjs/xterm.js/tree/master/addons/addon-fit | MIT | `licenses/third-party/xterm-addon-fit-0.10.0-LICENSE.txt` |

Both xterm packages are sourced from `https://registry.npmjs.org/`. Their exact
tarball URLs, npm integrity values, npm shasums, tarball SHA-256 values, and
vendored file SHA-256 values are recorded in
`PyRunner/Terminal/wwwroot/vendor-manifest.json`.

Full applicable license and notice texts are stored under
`licenses/third-party/`. Those files are distributed with published builds and
the installer.
