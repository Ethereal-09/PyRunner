# PyRunner

PyRunner 是一个本地 Windows Python 脚本管理器。它使用 WinUI 3 提供脚本目录自动导入、分类、搜索、收藏、解释器管理、运行历史、应用内定时任务、中英文界面和深浅主题，并通过 WebView2 + xterm.js + ConPTY 在应用内运行交互式脚本。

> **发布状态：** 当前稳定版为 v1.1.0，安装包与校验文件通过 GitHub Releases 提供。
> 规划中的应用更新清单地址为 `https://ethereal-09.github.io/PyRunner/update.json`；
> 在仓库管理员启用并完成 GitHub Pages 验收前，该地址不代表已经上线可用。

脚本从「设置」中配置的目录自动扫描，无需逐个新增。每次运行都有独立终端标签；切换标签会同步更新上方脚本信息和运行/停止状态。终端支持 `Ctrl+Shift+C` 复制、`Ctrl+Shift+V` 粘贴、鼠标右键粘贴以及普通 `Ctrl+C` 中断脚本。右上角按钮可在深色与浅色主题之间即时切换，语言仍在设置中选择。

## 系统要求

- Windows 10 1809（内部版本 17763）或更高版本，推荐 Windows 11
- x64 处理器
- Microsoft Edge WebView2 Runtime
- 至少一个 `python.exe`；可在首次引导或设置中添加

发布目录自带 .NET 8 和 Windows App SDK 运行时，不要求目标机器预装对应 .NET Desktop Runtime。

## 构建与验证

技术栈为 Windows App SDK / WinUI 3、.NET 8、C#、MVVM、WebView2、xterm.js 与 ConPTY。建议使用：

- Windows 10/11 x64
- .NET 8 SDK（仓库 `global.json` 指定兼容版本）
- Visual Studio 2022 Build Tools，包含 Windows SDK 和桌面 C++/Appx 构建工具

在仓库的 `PyRunner` 目录执行：

```powershell
dotnet build .\PyRunner.sln -c Debug -p:Platform=x64
dotnet build .\PyRunner.sln -c Release -p:Platform=x64
```

验证程序覆盖命令行转义与参数顺序、ConPTY 的 ANSI/UTF-8/交互输入/退出码、Job Object 进程树清理，以及 SQLite 完整性检查、备份和重建：

```powershell
dotnet run --project .\PyRunner.Verification\PyRunner.Verification.csproj -c Debug
```

## 发布

生成免安装、自包含的 x64 发布目录：

```powershell
dotnet publish .\PyRunner\PyRunner.csproj -p:PublishProfile=win-x64 -p:Platform=x64
```

产物位于 `artifacts\publish\win-x64`。请保持整个目录一起分发，入口为 `PyRunner.exe`。

生成 Windows x64 安装程序（需要 Inno Setup 6）：

```powershell
.\Installer\build-installer.ps1
```

安装包输出到 `artifacts\installer\PyRunner-Setup-<Version>-x64.exe`。构建脚本从
`PyRunner\PyRunner.csproj` 的 `<Version>` 读取版本；版本缺失或非法时会中止。请自行安装
受支持的 Inno Setup 6，仓库不分发其编译器。安装程序按当前用户安装，
不需要管理员权限；卸载或覆盖升级不会删除 `%LOCALAPPDATA%\PyRunner` 中的用户数据库和设置。

各版本的功能变化、修复和已知限制见 [CHANGELOG.md](CHANGELOG.md)。发布新版本时必须先更新
`CHANGELOG.md`，并将对应版本内容同步到 GitHub Release 说明。

更新清单格式见 [docs/update-manifest-v1.md](docs/update-manifest-v1.md)。稳定版通过独立的
`.github/workflows/release.yml` 手动触发发布；它会验证版本和更新日志、构建安装包、创建草稿
Release、上传并回下载核对资产，然后生成并部署 Pages 清单。普通 `.github/workflows/ci.yml`
始终保持只读权限。该流程不依赖 `release: published` 二次触发；Pages 失败时 Release 可能已经
公开，此时必须将状态记录为“Release 已发布、Pages 清单仍是上一稳定版”，修复 Pages 后才能
宣布应用内更新可用。

## 本地数据

- 数据库与设置：`%LOCALAPPDATA%\PyRunner`
- 数据库损坏备份：`%LOCALAPPDATA%\PyRunner\Backups`
- 开机启动：设置开启后写入当前用户的 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`

删除脚本登记不会删除磁盘上的 `.py` 文件。应用退出或停止任务时，Job Object 会清理该任务派生的进程树。

定时任务仅在 PyRunner 处于打开状态时触发；当前版本不会注册 Windows 任务计划，也不会在应用退出后后台运行。系统休眠期间错过的重复任务会在恢复后触发一次，然后继续计算下一次执行时间。

脚本内容、脚本路径、Python 解释器路径、设置和运行历史默认只保存在本机。PyRunner 不上传这些数据。
更新检测匿名读取本项目 GitHub Pages 上固定的 `update.json` 静态清单，不调用 GitHub REST API，
并最多每 24 小时自动检查一次；手动检查会忽略本地时间缓存。仅在用户点击下载后，应用才从
`Ethereal-09/PyRunner` 的 GitHub Release 下载清单指定的安装包。下载后会校验大小和 SHA-256，
再次联网复核清单，并在用户确认前重新校验本地文件；应用不会把 SHA-256 当作代码签名，也不会
静默安装或绕过 Windows 安全提示。

## 开源与安全

PyRunner 使用 [MIT License](LICENSE)。第三方组件及其许可证见
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 和 `licenses/third-party/`。

安全问题请按 [SECURITY.md](SECURITY.md) 使用 GitHub 私密漏洞报告，不要在公开 Issue 中提交漏洞细节、真实脚本、数据库或本机路径。
