using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace AIMaster.Services;

/// <summary>轻量级运行时本地化；只翻译应用自带文案，不触碰用户输入。</summary>
public static class LocalizationService
{
    public const string Chinese = "zh-CN";
    public const string English = "en-US";

    private static readonly Dictionary<string, string> ZhToEn = new(StringComparer.Ordinal)
    {
        ["SoftwareToolkit · 工具工作台"] = "SoftwareToolkit · Tool Workspace",
        ["工具工作台"] = "Tool Workspace", ["把工具放在顺手的地方"] = "Keep every tool within reach",
        ["拖入文件即可添加"] = "Drop a file to add it", ["设置与偏好"] = "Settings & preferences",
        ["搜索名称、标签、描述…"] = "Search names, tags, descriptions…", ["清空搜索 · Esc"] = "Clear search · Esc",
        ["＋  添加工具"] = "+  Add tool", ["我的工作空间 / 工具库"] = "MY WORKSPACE / TOOL LIBRARY",
        ["全部工具"] = "All tools", ["我的收藏"] = "Favorites", ["最近使用"] = "Recently used",
        ["正在加载工具"] = "Loading tools", ["刷新"] = "Refresh", ["重新扫描工具 · F5"] = "Rescan tools · F5",
        ["排序"] = "Sort", ["排序方式"] = "Sort by", ["常用优先"] = "Most used", ["名称 A → Z"] = "Name A → Z",
        ["收藏优先"] = "Favorites first", ["多选"] = "Select", ["切换列表"] = "List view",
        ["这里还没有工具"] = "No tools here yet", ["添加一个工具，开始整理你的工作空间。"] = "Add a tool to start organizing your workspace.",
        ["清除筛选"] = "Clear filters", ["添加工具"] = "Add tool", ["编辑"] = "Edit", ["删除"] = "Delete",
        ["收藏"] = "Favorite", ["取消收藏"] = "Unfavorite", ["收藏工具"] = "Favorite tool",
        ["启动 ↗"] = "Launch ↗", ["启动  ↗"] = "Launch  ↗", ["启动工具  ↗"] = "Launch tool  ↗",
        ["编辑、打开位置与其他操作"] = "Edit, open location, and more", ["完成"] = "Done", ["全选"] = "Select all",
        ["已选择 0 个"] = "0 selected", ["Ctrl+F 搜索  ·  双击 / Enter 启动"] = "Ctrl+F search  ·  Double-click / Enter to launch",
        ["就绪"] = "Ready", ["搜索工具"] = "Search tools",
        ["开发"] = "Development", ["工具"] = "Tools", ["网络"] = "Network", ["图形"] = "Graphics",
        ["音频"] = "Audio", ["文档"] = "Documents", ["游戏"] = "Games", ["系统"] = "System",
        ["安全"] = "Security", ["数据"] = "Data", ["文本"] = "Text", ["计算"] = "Calculators",
        ["终端"] = "Terminals", ["协作"] = "Collaboration", ["诊断"] = "Diagnostics", ["编辑器"] = "Editors",
        ["娱乐"] = "Entertainment", ["共享"] = "Sharing", ["效率"] = "Productivity", ["示例"] = "Samples",
        ["管理"] = "Management", ["自动化"] = "Automation", ["开发工具"] = "Developer tools",
        ["记事本"] = "Notepad", ["计算器"] = "Calculator", ["命令提示符"] = "Command Prompt",
        ["资源管理器"] = "File Explorer", ["画图"] = "Paint", ["注册表编辑器"] = "Registry Editor",
        ["Ping 谷歌"] = "Ping Google", ["路由追踪"] = "Route Trace", ["IP 配置"] = "IP Configuration",
        ["截图工具"] = "Snipping Tool", ["任务管理器"] = "Task Manager", ["字符映射表"] = "Character Map",
        ["DeepL 翻译"] = "DeepL Translate", ["局域网文件共享"] = "LAN File Share",
        ["示例插件工具"] = "Sample Plugin Tool", ["定时任务管理器"] = "Task Scheduler",

        ["⚙️ 设置"] = "⚙️ Settings", ["设置"] = "Settings", ["自定义 SoftwareToolkit 的行为和外观"] = "Customize SoftwareToolkit behavior and appearance",
        ["🌐 界面语言"] = "🌐 Interface language", ["选择应用显示语言，保存后立即生效"] = "Choose the app display language; changes apply after saving",
        ["⌨️ 全局热键"] = "⌨️ Global hotkey", ["设置显示/隐藏主窗口的快捷键"] = "Set the shortcut to show or hide the main window",
        ["恢复默认"] = "Restore default", ["支持组合: Ctrl, Alt, Shift, Win + A~Z / F1~F12"] = "Supported: Ctrl, Alt, Shift, Win + A–Z / F1–F12",
        ["🎯 行为"] = "🎯 Behavior", ["开机自动启动 SoftwareToolkit"] = "Start SoftwareToolkit with Windows",
        ["启用后，系统启动时自动运行本程序（注册表 Run 项）"] = "Automatically runs the app when Windows starts (registry Run entry)",
        ["关闭窗口时最小化到系统托盘（而非退出）"] = "Minimize to the system tray when closing",
        ["启用后，点击 ❌ 会隐藏到托盘图标，右键托盘图标可选择退出"] = "Closing hides the app in the tray; right-click the tray icon to exit",
        ["ℹ️ 关于"] = "ℹ️ About", ["应用:"] = "App:", ["版本:"] = "Version:", ["配置:"] = "Config:",
        ["🗑️ 卸载 SoftwareToolkit"] = "🗑️ Uninstall SoftwareToolkit", ["删除所有文件、配置和注册表项，彻底移除本程序"] = "Remove all app files, settings, and registry entries",
        ["取消"] = "Cancel", ["保存"] = "Save", ["保存并应用"] = "Save & apply", ["关闭"] = "Close",

        ["AIMaster"] = "AIMaster", ["看清额度跑道，在撞线之前停下来。"] = "See your quota runway and stop before the limit.",
        ["Codex 总额度"] = "Codex total quota", ["7 天窗口"] = "7-day window", ["5 小时窗口"] = "5-hour window",
        ["↻ 立即同步"] = "↻ Sync now", ["▣ 悬浮监控"] = "▣ Floating monitor", ["正在同步"] = "Syncing",
        ["主额度跑道"] = "PRIMARY QUOTA RUNWAY", ["等待额度数据"] = "Waiting for quota data", ["暂停线"] = "Pause limit",
        ["额度窗口"] = "QUOTA WINDOWS", ["账户可能同时返回总额度和特定模型额度；守卫以最高占用为准。"] = "The account may return total and model-specific quotas; the guard uses the highest usage.",
        ["任务闸门"] = "TASK GUARD", ["额度安全，当前无需暂停"] = "Quota safe — no pause needed",
        ["运行态只对当前 App Server 连接可见；其他 Codex 窗口可能显示为历史记录。"] = "Live state is visible only to this App Server connection; other Codex windows may appear as history.",
        ["暂停可见任务"] = "Pause visible tasks", ["解除本地闸门"] = "Release local guard",
        ["消耗预测 / 近 7 日"] = "USAGE FORECAST / LAST 7 DAYS", ["消耗预测 / 本周"] = "USAGE FORECAST / THIS WEEK",
        ["账号"] = "Account", ["本机"] = "This device", ["汇总该账号所有设备的用量"] = "Includes usage from all devices on this account",
        ["本周剩余额度"] = "WEEKLY QUOTA REMAINING", ["估算"] = "Estimated", ["预测"] = "Forecast",
        ["近 7 日 TOKEN"] = "LAST 7 DAYS TOKENS", ["等待数据"] = "Waiting for data", ["日均 TOKEN"] = "DAILY TOKENS",
        ["日均额度"] = "DAILY QUOTA", ["最近任务"] = "RECENT TASKS", ["等待连接"] = "Waiting to connect",
        ["保护策略"] = "GUARD POLICY", ["刻度线使用账户返回的百分比；不同模型可能拥有独立窗口。"] = "Thresholds use account percentages; models may have separate windows.",
        ["预警线"] = "Warning", ["达到暂停线后中断当前连接可见任务"] = "Interrupt visible tasks when the pause limit is reached",
        ["浮窗字号（100–150%）"] = "Floating font size (100–150%)",
        ["超出暂停线时中断可见任务并提示（窗口打开时监控）"] = "Interrupt visible tasks and alert when over the limit (while this window is open)",
        ["数据只保存在本机；认证由 Codex 管理。"] = "Data stays on this device; authentication is managed by Codex.",
        ["AI 任务悬浮监控"] = "AI task floating monitor", ["正在连接"] = "Connecting", ["连接中"] = "Connecting",
        ["读取最近任务…"] = "Reading recent task…", ["剩余额度"] = "QUOTA LEFT", ["当前推理模型"] = "CURRENT MODEL",
        ["取消置顶"] = "Unpin", ["保持置顶"] = "Keep on top", ["关闭悬浮窗"] = "Close floating monitor", ["切换语言"] = "Switch language",
        ["未命名任务"] = "Untitled task", ["暂无最近任务"] = "No recent tasks", ["模型未知"] = "Unknown model",
        ["额度窗口未知"] = "Unknown quota window", ["重置时间未知"] = "Reset time unknown", ["时间未知"] = "Time unknown",
        ["推理中"] = "Running", ["等待批准"] = "Awaiting approval", ["等待输入"] = "Awaiting input",
        ["已暂停"] = "Paused", ["已完成"] = "Completed", ["异常"] = "Error", ["历史记录"] = "History",
        ["立即刷新"] = "Refresh now", ["立即收起"] = "Collapse now", ["展开浮窗"] = "Expand window",
        ["靠边自动收起"] = "Auto-hide at edge", ["刷新频率"] = "Refresh interval", ["悬停展开"] = "Hover to expand",
        ["保护策略已保存并应用"] = "Guard policy saved and applied", ["设置已保存并应用"] = "Settings saved and applied",
        ["折叠卡片"] = "Collapse card", ["展开卡片"] = "Expand card", ["拖动调整布局"] = "Drag to rearrange",
        ["本机消耗"] = "LOCAL USAGE", ["TOKEN / 本周"] = "TOKENS / THIS WEEK",
        ["常用模型"] = "TOP MODEL",
        ["等待本地记录"] = "Waiting for local records", ["等待账户与本机记录"] = "Waiting for account and local records",
        ["本机 Token 来自本地会话日志；账户 Token 来自 Codex 用量历史。"] = "Local tokens come from on-device session logs; account tokens come from Codex usage history.",
        ["额度占比按同周期 Token 比例估算，不等于官方设备账单。"] = "Quota attribution is estimated from same-period token ratios; it is not an official per-device bill.",
        ["项目消耗分布"] = "USAGE BY PROJECT", ["本周暂无本地 Token 记录"] = "No local token records this week",
        ["未归属项目"] = "Unassigned",
        ["当日消耗 / 时段"] = "TODAY / HOURLY USAGE", ["TOKEN / 今日"] = "TOKENS / TODAY",
        ["周总额度占比暂不可用"] = "Weekly quota share unavailable",
        ["按账号本周 Token 历史与周窗口已用比例估算，不是官方逐日账单。"] = "Estimated from this week's account token history and weekly-window usage; this is not an official daily bill.",
        ["今日每小时消耗"] = "TODAY'S HOURLY USAGE", ["00–23 时"] = "00–23 HOURS",
        ["今日暂无本机 Token"] = "No local tokens today",
        ["共享统计"] = "SHARED USAGE", ["启用共享统计"] = "Enable shared usage",
        ["使用相同同步密钥的设备归为一组；不会上传 Codex 凭证、任务内容、项目名称或路径。"] = "Devices using the same sync key form a group; Codex credentials, task content, project names, and paths are never uploaded.",
        ["服务器地址"] = "Server URL", ["设备名称"] = "Device name", ["同步密钥"] = "Sync key",
        ["生成密钥"] = "Generate key", ["复制密钥"] = "Copy key", ["保存并同步"] = "Save & sync",
        ["等待共享设置"] = "Waiting for sharing settings", ["暂无共享设备数据"] = "No shared device data",

        ["软件清单"] = "Software Inventory", ["我的软件清单"] = "My Software Inventory",
        ["扫描本机软件，挑选要保留或分享的应用，并维护可用的下载入口。"] = "Scan installed software, curate a shareable list, and maintain download links.",
        ["↻ 重新扫描"] = "↻ Rescan", ["设备软件"] = "DEVICE SOFTWARE", ["我的清单"] = "MY LIST",
        ["准备扫描"] = "Ready to scan", ["搜索名称、发布者或版本"] = "Search name, publisher, or version",
        ["选择当前结果"] = "Select current results", ["清空清单"] = "Clear list", ["导出分享页"] = "Export share page",
        ["复制分享文本"] = "Copy share text", ["正在扫描注册表、Store 应用并匹配 WinGet…"] = "Scanning registry and Store apps, then matching WinGet…",
        ["清单还是空的"] = "Your list is empty", ["到“设备软件”中勾选要加入的应用"] = "Select apps under Device Software to add them",
        ["软件"] = "Software", ["版本"] = "Version", ["来源"] = "Source", ["安装日期"] = "Installed",
        ["包 ID"] = "Package ID", ["下载链接（可直接修改）"] = "Download link (editable)", ["操作"] = "Actions",
        ["加入"] = "Add", ["移除"] = "Remove", ["下载"] = "Download", ["恢复自动"] = "Restore auto",
        ["加入或移出我的清单"] = "Add to or remove from my list",

        ["✏️ 编辑工具"] = "✏️ Edit tool", ["编辑工具"] = "Edit tool", ["配置工具的启动方式和属性"] = "Configure how the tool launches and its properties",
        ["工具名称 *"] = "Tool name *", ["描述"] = "Description", ["分类（如 开发/编辑器）"] = "Category (e.g. Development/Editors)",
        ["标签（逗号分隔）"] = "Tags (comma-separated)", ["启动类型"] = "Launch type", ["⚡ 可执行文件 (EXE/BAT/PS1)"] = "⚡ Executable (EXE/BAT/PS1)",
        ["⌨️ 命令 (CMD)"] = "⌨️ Command (CMD)", ["🌐 网址/URL"] = "🌐 Website/URL", ["🧩 插件 DLL"] = "🧩 Plugin DLL",
        ["🔨 一键编译 (Build)"] = "🔨 One-click build", ["📋 内置工具"] = "📋 Built-in tool",
        ["路径 / URL / 命令 *"] = "Path / URL / command *", ["浏览..."] = "Browse…", ["命令行参数（可选）"] = "Command-line arguments (optional)",
        ["以管理员身份运行"] = "Run as administrator", ["编译配置 (Release/Debug)"] = "Build configuration (Release/Debug)",
        ["输出目录（留空使用默认 publish 目录）"] = "Output directory (blank uses the default publish directory)",
        ["编译输出"] = "Build output", ["正在编译..."] = "Building…", ["📁 打开目录"] = "📁 Open folder", ["📂 打开 EXE"] = "📂 Open EXE",

        ["检查更新"] = "Check for updates", ["正在检查…"] = "Checking…",
        ["⇩ 检查更新"] = "⇩ Check for updates", ["检查 GitHub 新版本"] = "Check GitHub for a new version",
        ["立即更新"] = "Update now", ["⇩ 立即更新"] = "⇩ Update now", ["下载并安装新版本"] = "Download and install the new version",
        ["显示主窗口"] = "Show main window", ["AI 悬浮监控"] = "AI floating monitor", ["退出"] = "Exit"
        , ["设置已保存。"] = "Settings saved.", ["保存成功"] = "Saved"
    };

    private static readonly Dictionary<string, string> EnToZh = ZhToEn
        .GroupBy(pair => pair.Value, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.First().Key, StringComparer.Ordinal);
    private static string _currentLanguage = Chinese;

    public static event EventHandler? LanguageChanged;
    public static string CurrentLanguage => _currentLanguage;
    public static bool IsEnglish => _currentLanguage == English;

    public static void Initialize(string? language)
    {
        _currentLanguage = Normalize(language);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(_currentLanguage);
    }

    public static void SetLanguage(string? language)
    {
        var normalized = Normalize(language);
        if (_currentLanguage == normalized) return;
        _currentLanguage = normalized;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(normalized);
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    public static string T(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var chinese = EnToZh.TryGetValue(text, out var original) ? original : text;
        return IsEnglish && ZhToEn.TryGetValue(chinese, out var translated) ? translated : chinese;
    }

    public static string F(string chineseFormat, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(chineseFormat), args);

    public static string L(string chinese, string english) => IsEnglish ? english : chinese;

    public static string LocalizeKnownText(string text) => T(text);

    public static string FormatWindowDuration(int minutes)
    {
        if (minutes <= 0) return L("额度窗口未知", "Unknown quota window");
        if (minutes >= 1440 && minutes % 1440 == 0)
            return IsEnglish ? $"{minutes / 1440}-day window" : $"{minutes / 1440} 天窗口";
        if (minutes >= 60 && minutes % 60 == 0)
            return IsEnglish ? $"{minutes / 60}-hour window" : $"{minutes / 60} 小时窗口";
        return IsEnglish ? $"{minutes}-minute window" : $"{minutes} 分钟窗口";
    }

    public static string FormatResetTime(DateTimeOffset? value) => value is null
        ? L("重置时间未知", "Reset time unknown")
        : IsEnglish
            ? $"Resets {value.Value.LocalDateTime.ToString("MMM d HH:mm", CultureInfo.InvariantCulture)}"
            : $"{value.Value.LocalDateTime:M月d日 HH:mm} 重置";

    public static string FormatUpdatedTime(DateTimeOffset? value) => value is null
        ? L("时间未知", "Time unknown")
        : IsEnglish
            ? value.Value.LocalDateTime.ToString("MMM d HH:mm", CultureInfo.InvariantCulture)
            : value.Value.LocalDateTime.ToString("M月d日 HH:mm", CultureInfo.GetCultureInfo(Chinese));

    public static string FormatTaskStatus(string status) => status switch
    {
        "active" or "inProgress" => L("推理中", "Running"),
        "waitingOnApproval" => L("等待批准", "Awaiting approval"),
        "waitingOnUserInput" => L("等待输入", "Awaiting input"),
        "idle" or "completed" => L("已完成", "Completed"),
        "interrupted" => L("已暂停", "Paused"),
        "failed" or "systemError" => L("异常", "Error"),
        "notLoaded" => L("未加载", "Unavailable"),
        _ => L("历史记录", "History")
    };

    public static void Apply(DependencyObject root)
    {
        ApplyOne(root);
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            Apply(child);
    }

    private static void ApplyOne(DependencyObject item)
    {
        if (item is Window window) window.Title = T(window.Title);
        if (item is TextBlock textBlock)
        {
            textBlock.Text = T(textBlock.Text);
            foreach (var run in textBlock.Inlines.OfType<Run>()) run.Text = T(run.Text);
        }
        if (item is ContentControl contentControl && contentControl.Content is string content)
            contentControl.Content = T(content);
        if (item is HeaderedContentControl headered && headered.Header is string header)
            headered.Header = T(header);
        if (item is FrameworkElement element && element.ToolTip is string tooltip)
            element.ToolTip = T(tooltip);
        if (item is DataGrid grid)
            foreach (var column in grid.Columns.Where(column => column.Header is string))
                column.Header = T((string)column.Header);
    }

    private static string Normalize(string? language) =>
        language?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true ? English : Chinese;
}
