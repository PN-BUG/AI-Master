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
build.ps1                自包含发布与 ZIP 打包
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
| `-NoZip` | 只生成目录，不创建 ZIP |

输出固定在 `release/AIMaster-<runtime>-<mode>`，重复构建只清理这个经过校验的目标目录。

同时生成 Windows x64 完整版与轻量版：

```powershell
.\build.ps1
.\build.ps1 -Lightweight
```

## 数据流

```text
WPF 窗口
  -> AiManagerService
      -> CodexAppServerClient -> codex app-server（额度、模型、在线任务）
      -> 本地 JSONL 扫描       -> %USERPROFILE%\.codex\sessions（任务降级数据）
  -> AiManagerSnapshot / AiFloatingSnapshot
  -> 主面板与悬浮窗渲染
```

App Server 请求使用独立超时；任务、模型或历史用量等可选请求失败时，不应阻止本地任务降级和已有额度信息显示。

任务名称数据分为 `ConversationTitle` 与 `LatestUserMessage`。App Server 提供正式对话标题，本地 rollout JSONL 持续更新首条与最后一条真实用户消息；`taskNameSource` 设置决定 UI 使用哪一个，缺失时自动回退。

## 发布前检查

1. `dotnet build .\AIMaster.sln -c Release` 无警告、无错误。
2. 主面板能够同步额度。
3. 悬浮窗能显示任务、模型、推理强度和额度。
4. 在主屏、副屏分别验证上/左/右靠边收起。
5. 切换中英文，检查窗口尺寸和右键菜单。
6. 分别运行 `build.ps1` 和 `build.ps1 -Lightweight`，确认两个 ZIP 中均含 `AIMaster.exe` 和 `START-HERE.txt`。
