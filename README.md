# AIMaster

<p align="center">
  <strong>简体中文</strong> · <a href="README.en.md">English</a>
</p>

<p align="center">
  <img src="Resources/AIMaster.png" width="144" alt="AIMaster icon">
</p>

AIMaster 是一个可独立运行的 Windows Codex 用量与任务监控工具。它集中展示剩余额度、额度重置时间、消耗预测、最近任务、执行状态、当前模型与推理强度，并提供紧凑的桌面悬浮窗。

## 主要功能

![AIMaster 中文主面板](docs/images/dashboard-zh.png)

- 显示 Codex 总额度、7 天窗口、5 小时窗口与重置时间。
- 自动追踪 [codex-reset.com](https://codex-reset.com/) 的重置预告与公告，新事件发送 Windows 通知；用官方账号额度变化单独检测额度恢复。
- 根据近期消耗速度预测额度是否会提前耗尽。
- 设置预警线和暂停线，越线时提醒并暂停当前连接可见的任务。
- 用折线图展示本周一至周日的剩余额度估算与未来预测，并可切换账号总消耗速度或仅按本机消耗速度推演。
- 汇总本周本机 Codex Token 消耗和按 Token 加权的常用模型；结合账号同周期 Token 历史估算当前设备与各项目的周额度占比，并为每个项目显示常用模型，避免把多设备总用量全部归到本机。
- 显示今日的本机 Token、约占账号周总额度的比例，以及 0–23 时每小时消耗折线图。
- 可选启用涡流网共享统计，将多台设备按同步密钥归组并显示各设备 Token 占比、常用模型、会话数和最后同步时间。
- 主窗口按额度概览、任务监控、用量分析、共享统计和设置分页；顶部保留同步与悬浮监控，语言、主题和更新集中在设置页。
- 支持在功能页内拖动卡片上边缘或两侧调整布局、折叠卡片，以及拖动下边缘调整高度；宽度随窗口自适应，布局和高度会自动保存。
- 按任务状态和最近更新时间排序，悬浮窗最多显示 8 个任务，包括进行中、等待、暂停/中断和已完成状态。
- 任务名称可选择显示 Codex 对话标题或该任务最后发送的用户内容。
- 显示任务当前使用的模型与推理强度。
- 悬浮窗可自由调整尺寸并保存位置；任务行可点击直达 Codex，字号可在主窗口设置中调整。
- 支持多显示器靠边收起；收起时停止刷新，悬停展开后立即刷新。
- 支持手动刷新、1/2/5/10/30 秒刷新频率和右键自动收起开关。
- 支持简体中文与英文即时切换。
- 启动时自动检测 GitHub Release，也可在主窗口检查并直接下载安装新版本。
- 点击主窗口的“关于”查看版本、开发者和开源许可，并通过链接加入[涡流工作室 QQ 交流群](https://qm.qq.com/q/2a93fRANJO)。
- 主窗口显示任务栏入口；悬浮窗不占任务栏位置。
- 关闭主窗口后隐藏到 Windows 系统托盘并保持后台监控；托盘菜单可恢复窗口或真正退出。

## 直接运行

预编译的 Windows x64 版本请从 [GitHub Releases](https://github.com/PN-BUG/AI-Master/releases) 下载；本地运行 `build.ps1` 后，发布文件位于 `release`：

- `release/AIMaster-win-x64-standalone.zip`：完整独立版，包含 .NET 运行时。
- `release/AIMaster-win-x64-lightweight.zip`：轻量多文件版，需要 [.NET 8 Desktop Runtime x64](https://dotnet.microsoft.com/download/dotnet/8.0)。

1. 解压 ZIP。
2. 确保 Codex 已安装并完成登录。
3. 双击 `AIMaster.exe`。

完整独立版无需另外安装 .NET Desktop Runtime；轻量版体积更小，适合已经安装 .NET 8 Desktop Runtime 的电脑。两个版本均不包含 Codex 本身。

AIMaster 启动时会静默检查 GitHub Release，也可点击主窗口的“检查更新”。查到 Release 后可点“查看更新内容”阅读发布说明；如果 GitHub 未返回说明，应用会打开该版本的发布页。发现新版后会显示“立即更新”，自动选择与当前架构及轻量/独立模式匹配的 ZIP，校验后退出、替换并重启；失败时会恢复旧版本，本地设置不会被覆盖。

更新以当前运行的 EXE 所在目录为准，原地覆盖并重启，不依赖快捷方式。仅生成发布包不会更新运行副本；开发时可用 `update-running.ps1` 安装最新本地构建，详见 [更新约定](docs/UPDATES.md)。

## 使用要求

- Windows 10 1809 或更新版本（含 Windows 11）。
- 已安装并登录 Codex；`codex` 命令在 PATH 中，或使用标准的 Codex Desktop 安装位置。
- 读取额度和在线任务时，Codex App Server 需要可用。

## 悬浮窗

![AIMaster 中文悬浮窗](docs/images/floating-zh.png)

在主窗口点击“悬浮监控”打开悬浮窗：

- 拖到任意显示器边缘可自动收起。
- 收起时只保留 8 px 感应条并停止后台刷新。
- 鼠标悬停感应条会展开并立即刷新。
- 右键可立即刷新、切换收起状态、选择任务名称来源、开启或关闭靠边自动收起、修改刷新频率。
- 拖动边缘或角落调整尺寸，按高度显示最多 8 个最近任务；位置和尺寸会保存。
- 悬停任务行可高亮，单击可在 Codex 桌面端打开对应任务。
- 浮窗字号可在主窗口“保护策略”中按 100–150% 调整。
- 主窗口右上角关闭按钮只会隐藏窗口。单击托盘图标可恢复，右键托盘图标可打开悬浮监控或退出 AIMaster。

## 数据来源与隐私

AIMaster 启动本机的 `codex app-server --listen stdio://` 并使用 Codex 自己维护的登录状态。工具不会读取、复制或保存登录令牌。

本机 Codex 桌面任务的实时审批状态通过已有的本地 `codex-ipc` 管道只读跟随。命令执行、文件变更和权限请求存在实际待处理审批时显示“等待批准”，答复后立即清除。解析后仅保留请求编号、类型、运行状态与修订号，不持久化收到的对话、命令或审批内容。该内部协议不兼容或连接中断时回退到本地执行记录，不推测审批状态。

共享统计默认关闭。启用后仅向 `https://www.woliux.com/api/v1/aimaster/sync` 上传设备周汇总，不上传 Codex 凭证、任务内容、项目名称或路径；同步密钥使用 Windows DPAPI 加密后保存在本机。

当 App Server 暂时不可用时，任务列表会从 `%USERPROFILE%\.codex\sessions` 中的本地会话记录降级读取。设置保存在 `%LOCALAPPDATA%\AIMaster\settings.json`。第一次启动会自动迁移旧版 `%LOCALAPPDATA%\SoftwareToolkit\ai-manager.json` 设置。

“对话标题”来自 Codex 的任务元数据；“最后发送内容”从对应任务最近一条真实用户消息中提取。附件说明、环境信息等注入文本不会作为任务名。

## 从源码构建

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

```powershell
dotnet build .\AIMaster.sln -c Release
```

生成 Windows x64 自包含包：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

一键生成完整版、轻量版两个发布目录和对应的两个 ZIP：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -All
```

Windows 下也可以直接双击 `package-all.cmd`。它先退出当前运行的 AIMaster 并确认进程结束，再生成两个发布包、更新其原目录并自动打开新版本；没有运行实例时打开新生成的独立版。打包失败时保留旧产物并重新打开旧程序，更新失败时由事务更新器恢复。输出统一位于 `release` 目录。只需生成分发包时使用上面的 `build.ps1 -All`。

生成 Windows x64 轻量版：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Lightweight
```

ARM64：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Runtime win-arm64
```

更多信息：

- [用户手册](docs/USER-GUIDE.md)
- [English user guide](docs/USER-GUIDE.en.md)
- [开发与构建](docs/DEVELOPMENT.md)
- [故障排查](docs/TROUBLESHOOTING.md)

## License

Apache License 2.0，详见 [LICENSE](LICENSE)。
