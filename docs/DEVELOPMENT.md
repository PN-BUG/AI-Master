# 开发与构建

## 技术栈

- .NET 8
- WPF
- Windows Forms `Screen` API（多显示器工作区定位）
- Codex App Server JSONL 协议

项目不依赖 SoftwareToolkit 源码，也不包含第三方 NuGet 包。

## 目录结构

```text
AIMaster.csproj          WPF 项目
App.xaml(.cs)            应用入口、语言初始化、单实例控制
AiManagerWindow.*        主面板
AiFloatingWindow.*       紧凑悬浮窗、刷新与多屏靠边逻辑
Models/                  设置、额度、任务和快照模型
Services/                App Server 客户端、数据聚合、本地化与路径迁移
Resources/               PNG 原图与多尺寸 ICO
docs/                    用户、开发和排错文档
tests/AIMaster.Smoke/    任务栏、图标与英文完整性回归测试
build.ps1                单版本或双版本发布与 ZIP 打包
package-all.cmd          可双击的一键双版本打包工具
release/                 完整独立版和轻量版发布包
```

## 调试构建

```powershell
dotnet restore .\AIMaster.csproj
dotnet build .\AIMaster.csproj -c Debug
dotnet run --project .\AIMaster.csproj
```

运行回归测试：

```powershell
dotnet run --project .\tests\AIMaster.Smoke\AIMaster.Smoke.csproj -c Release
```

重新生成 README 和用户手册使用的中英文匿名演示截图：

```powershell
dotnet run --project .\tests\AIMaster.Smoke\AIMaster.Smoke.csproj -c Release -- --capture-docs --output .\docs\images
```

截图使用固定的演示额度、任务和项目路径，不读取或公开真实账户数据。生成后应检查四张 PNG 的文字、图表和底部控件是否完整。

## 发布

默认生成 Windows x64 自包含单文件应用和 ZIP：

```powershell
.\build.ps1
```

可选参数：

| 参数 | 说明 |
|---|---|
| `-Configuration Debug` | 使用 Debug 配置 |
| `-Runtime win-arm64` | 发布 ARM64 版本 |
| `-Lightweight` | 生成不携带 .NET 运行时的轻量多文件版 |
| `-FrameworkDependent` | `-Lightweight` 的兼容别名 |
| `-All` | 一次生成完整版、轻量版及各自的 ZIP |
| `-NoZip` | 只生成目录，不创建 ZIP |

输出固定在 `release/AIMaster-<runtime>-<mode>`，重复构建只清理这个经过校验的目标目录。

`build.ps1` 只生成发布包；`package-all.cmd` 同时生成两个发布包，并原地更新、重启当前运行副本。只更新当前副本时使用 `.\update-running.ps1`。两种更新入口都以运行进程的 EXE 所在目录为准，复用事务更新器。更新约定、回滚和测试见 [UPDATES.md](UPDATES.md)。发布时同时上传生成的 `.zip.sha256` 校验附件。

一键同时生成 Windows x64 完整版、轻量版及两个 ZIP：

```powershell
.\build.ps1 -All
```

也可以直接双击仓库根目录的 `package-all.cmd`。它先退出旧程序并确认进程结束，再生成两个包、更新原目录并打开新版本；失败时恢复旧产物并重新打开旧程序。没有运行实例时打开新生成的独立版。完成后会保留窗口供检查结果。

## 数据流

```text
WPF 窗口
  -> AiManagerService
      -> CodexAppServerClient -> codex app-server（额度、模型、在线任务）
      -> 本地 JSONL 扫描       -> %USERPROFILE%\.codex\sessions（任务降级数据）
      -> SharedUsageClient     -> https://www.woliux.com/api/v1/aimaster/sync（可选设备周汇总）
  -> AiManagerSnapshot / AiFloatingSnapshot
  -> 主面板与悬浮窗渲染
```

App Server 请求使用独立超时；任务、模型或历史用量等可选请求失败时，不应阻止本地任务降级和已有额度信息显示。

重置信号由 `CodexResetClient` 读取 `https://codex-reset.com/api/feed` 和 `/api/forecast`，使用独立的两分钟计时器，不依赖 App Server 连接。只读取公开数据并发送标识项目的 User-Agent，不携带 Codex 登录信息。遵守 `Retry-After`；数据过期或请求失败时保留上次显示并停止信号通知。`CodexResetTracker` 将已见事件原子保存到设置目录的 `reset-signal-state.json`，首次成功读取静默建立基线。`AccountResetTracker` 单独比较官方额度快照，公共公告不得直接改动额度。

重置信号的定向回归测试（含离线中英文卡片渲染）：

```powershell
dotnet run --project .\tests\AIMaster.Smoke\AIMaster.Smoke.csproj -- --reset-tests --output .\artifacts\reset-signals
```

共享统计默认关闭。客户端只上传设备级周汇总，`X-AIMaster-Sync-Key` 使用 Windows DPAPI 加密保存在本机设置中；项目、任务与 Codex 登录数据不得进入共享请求。公网服务接口和 Supabase 持久化由 `woliu` 项目维护。

`SharedUsageCounter` 独立保存当前设备的时间戳用量账本及按模式划分的手动清零、恢复记录（设置目录的 `shared-usage-<deviceId>.json`）。按匿名会话标识、模型和 UTC 时间戳保留最高已采集计数，清零不修改 JSONL。每周模式筛选本机周一 00:00 至今，重置模式筛选官方 `codex` 周窗口的 `ResetsAt - WindowDurationMinutes` 至今，不清零模式累计全部历史；模式切换本身不会建立清零基线。自然周期变化保留检测前已产生的周期内用量。官方窗口信息持久化；切换到重置方式时使用最新官方读数，未知或过期的窗口及无法区分重置前后的无时间戳记录会阻止共享上传。旧按日账本首次扫描时迁移为时间戳记录，保留手动清零和恢复数据，无法重读的历史保留为日残余。接口仍使用公共日历周封装，以兼容同组同周期查询。共享扫描包含 `sessions` 和 `archived_sessions`，同名会话不会因归档重复计数。操作、自动刷新和网络同步串行化，防止旧计数覆盖清零结果；本机状态保存失败时不应用清零。

定向验证（离线，不发送共享请求）：`dotnet run --project .\tests\AIMaster.Smoke\AIMaster.Smoke.csproj -- --shared-usage-tests`。

应用使用 `OnExplicitShutdown`：主窗口的关闭事件只隐藏窗口，`App` 持有系统托盘图标并维持后台进程；只有托盘“退出”会关闭窗口、释放 App Server 与图标资源并调用 `Shutdown()`。

任务名称数据分为 `ConversationTitle` 与 `LatestUserMessage`。App Server 提供正式对话标题，本地 rollout JSONL 持续更新首条与最后一条真实用户消息；`taskNameSource` 设置决定 UI 使用哪一个，缺失时自动回退。

## 发布前检查

1. `dotnet build .\AIMaster.sln -c Release` 无警告、无错误。
2. 主面板能够同步额度。
3. 悬浮窗能显示任务、模型、推理强度和额度。
4. 在主屏、副屏分别验证上/左/右靠边收起。
5. 切换中英文，检查窗口尺寸和右键菜单。
6. 运行 `build.ps1 -All`，确认两个发布目录和两个 ZIP 均已生成，且 ZIP 中包含 `AIMaster.exe` 和 `START-HERE.txt`。
7. 运行文档截图命令，确认 `dashboard-zh/en.png` 和 `floating-zh/en.png` 与当前界面一致。
8. 启用共享统计后，用同一同步密钥在两台测试设备上同步，确认设备数、Token 总计与占比正确；检查设置文件中不存在明文同步密钥。
