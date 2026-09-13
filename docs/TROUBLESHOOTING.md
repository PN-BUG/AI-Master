# 故障排查

## 轻量版提示缺少 .NET

轻量版不携带运行时。请安装与程序架构一致的 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)，Windows x64 包应安装 x64 Desktop Runtime。也可以直接改用 `AIMaster-win-x64-standalone.zip` 完整独立版。

## 无法读取 Codex 用量

先在 PowerShell 中执行：

```powershell
codex --version
codex login
```

如果找不到 `codex`，请安装 Codex 并重新打开终端。AIMaster 也会检查标准 Codex Desktop 安装目录、npm 全局目录和 `CODEX_EXECUTABLE` 环境变量。

## 无法连接 Codex App Server

确认没有安全软件阻止 `codex app-server --listen stdio://`。短暂超时可以点击“立即同步”重试。额度依赖 App Server；本地任务列表仍会尝试从 `%USERPROFILE%\.codex\sessions` 读取。

## 任务名称或状态没有显示

1. 确认至少运行过一次 Codex 任务。
2. 检查 `%USERPROFILE%\.codex\sessions` 是否存在 JSONL 会话文件。
3. 点击手动刷新。
4. 如果悬浮窗处于收起状态，先悬停展开；收起期间设计为不刷新。

任务实时状态可能只对创建它的 App Server 连接可见。本地降级数据会显示最近执行记录，但不能保证替代另一 Codex 窗口的实时控制状态。

## 悬浮窗无法在副屏收起

把浮窗完全拖入目标显示器，再靠近该显示器的上、左或右工作区边缘。AIMaster 根据浮窗原生坐标选择显示器；更改 DPI 或显示器排列后，重新拖动一次即可更新停靠区域。

## 重置设置

关闭 AIMaster，将下列文件改名或移走后重新启动：

`%LOCALAPPDATA%\AIMaster\settings.json`

不要删除 Codex 的登录或会话目录。
