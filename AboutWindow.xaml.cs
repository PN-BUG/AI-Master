using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using AIMaster.Services;
using SharedUpdates;

namespace AIMaster;

public partial class AboutWindow : Window
{
    public AboutWindow(Window owner)
    {
        Owner = owner;
        Icon = owner.Icon;
        FontFamily = owner.FontFamily;
        Resources.MergedDictionaries.Add(owner.Resources);
        InitializeComponent();

        var version = GitHubUpdateChecker.GetCurrentVersion(typeof(App).Assembly);
        DataContext = new
        {
            TitleText = LocalizationService.L("关于 AIMaster", "About AIMaster"),
            VersionText = LocalizationService.L($"版本 {version}", $"Version {version}"),
            Description = LocalizationService.L(
                "Codex 额度与任务监控工具，集中查看额度、重置时间、用量统计与任务状态，并提供桌面悬浮监控。",
                "A Codex quota and task monitor with quota remaining, reset times, usage statistics, task activity, and a desktop overlay."),
            StudioText = LocalizationService.L("开发：涡流工作室", "Developed by Woliu Studio"),
            LicenseText = LocalizationService.L("开源许可：Apache License 2.0", "License: Apache License 2.0"),
            LinksHeading = LocalizationService.L("了解更多", "Learn more"),
            WebsiteText = LocalizationService.L("AIMaster 官网 ↗", "AIMaster website ↗"),
            RepositoryText = LocalizationService.L("GitHub 源码 ↗", "Source on GitHub ↗"),
            CommunityHeading = LocalizationService.L("QQ 交流群", "QQ community"),
            CommunityText = LocalizationService.L("加入群聊【涡流工作室交流群】 ↗", "Join the Woliu Studio QQ group ↗"),
            CloseText = LocalizationService.L("关闭", "Close")
        };
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(this,
                LocalizationService.L($"无法打开链接：{ex.Message}", $"Unable to open the link: {ex.Message}"),
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
