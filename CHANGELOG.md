# 更新日志

本文件记录 PyRunner 各稳定版本中面向用户的变化。每次发布新版本时，必须先更新本文件，再将对应版本内容同步到 GitHub Release。

格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循[语义化版本](https://semver.org/lang/zh-CN/)。

## [Unreleased]

尚无未发布变更。

## [1.1.0] - 2026-09-12

### 新增

- 新增应用内定时任务，支持单次、每天、每周和固定分钟间隔四种执行规则。
- 新增定时任务管理页面，可新建、编辑、启用、禁用、立即运行和删除任务。
- 脚本列表及文件树右键菜单新增“创建定时任务”入口。
- 新增数据库迁移，持久化任务规则、下次执行时间、上次执行时间和执行结果。
- 新增中英文定时任务界面与错误提示。

### 安全与行为

- 定时任务仅在 PyRunner 打开期间触发，不注册 Windows 任务计划，也不会在应用退出后后台运行。
- 系统休眠期间错过的重复任务会在恢复后触发一次，再计算下一次执行时间。
- 脚本登记被删除时，对应定时任务会保留并自动禁用，不会删除磁盘上的脚本文件。

### 验证

- Release x64 构建通过，0 警告、0 错误。
- 自动验证 `123/123` 通过，Vendor 哈希 `3/3` 通过。

### 已知限制

- Windows 安装包暂未进行代码签名，启动安装程序时 Windows 可能显示 SmartScreen 提示。

## [1.0.1] - 2026-09-11

### 修复

- 修复运行记录完整输出顶部出现大块空白的问题，并确保详情打开时定位到输出顶部。
- 将“关于作者”信息统一为仓库作者 `@Ethereal-09`。

## [1.0.0] - 2026-09-11

- PyRunner 首个正式稳定版本。

[Unreleased]: https://github.com/Ethereal-09/PyRunner/compare/v1.1.0...HEAD
[1.1.0]: https://github.com/Ethereal-09/PyRunner/compare/v1.0.1...v1.1.0
[1.0.1]: https://github.com/Ethereal-09/PyRunner/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/Ethereal-09/PyRunner/releases/tag/v1.0.0
