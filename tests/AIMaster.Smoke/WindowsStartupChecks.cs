using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIMaster;
using AIMaster.Services;
using Microsoft.Win32;

internal static partial class Program
{
    private static void VerifyStartupUi(string[] args)
    {
        var output = Path.GetFullPath(args.SkipWhile(arg => arg != "--output").Skip(1).FirstOrDefault()
            ?? Path.Combine("artifacts", "startup"));
        Directory.CreateDirectory(output);
        var keyPath = @"Software\AIMaster.Tests\StartupUi-" + Guid.NewGuid().ToString("N");
        var executable = Path.Combine(output, "startup-fixture.exe");
        File.WriteAllText(executable, "test fixture; never executed");
        var startup = new WindowsStartupService(executable, keyPath);
        var service = new AiManagerService(Path.Combine(output, "isolated", "settings.json"));
        var window = new AiManagerWindow(service, windowsStartup: startup);
        var checkbox = (CheckBox)window.FindName("StartWithWindowsCheck");
        try
        {
            Check(checkbox.IsChecked == false && !startup.IsEnabled(),
                "opening settings leaves Windows startup disabled");
            checkbox.IsChecked = true;
            checkbox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(startup.IsEnabled(), "the startup checkbox immediately registers Windows logon startup");
            checkbox.IsChecked = false;
            checkbox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(!startup.IsEnabled(), "the startup checkbox immediately removes Windows logon startup");
            File.Delete(executable);
            checkbox.IsChecked = true;
            checkbox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(checkbox.IsChecked == false && !startup.IsEnabled() &&
                  !string.IsNullOrWhiteSpace(((TextBlock)window.FindName("StartupStatusText")).Text),
                "a startup failure restores the checkbox and shows an error");
            File.WriteAllText(executable, "test fixture; never executed");
            foreach (var language in new[] { LocalizationService.Chinese, LocalizationService.English })
            {
                LocalizationService.SetLanguage(language);
                ((RadioButton)window.FindName("SettingsPageButton")).IsChecked = true;
                Check(checkbox.Content is TextBlock label && label.Text == (LocalizationService.IsEnglish
                    ? "Start AIMaster with Windows" : "开机自动启动 AIMaster"),
                    $"{language} localizes the Windows startup option");
                checkbox.IsChecked = true;
                checkbox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Check(startup.IsEnabled() && string.IsNullOrEmpty(((TextBlock)window.FindName("StartupStatusText")).Text),
                    $"{language} can retry successfully after a startup failure");
                foreach (var width in new[] { 960, 1280 })
                {
                    var root = (FrameworkElement)window.Content;
                    ArrangeRedesign(root, width, 720);
                    var position = checkbox.TranslatePoint(new Point(), root);
                    Check(checkbox.ActualWidth > 0 && checkbox.ActualHeight > 0 &&
                          position.X >= 0 && position.X + checkbox.ActualWidth <= width &&
                          position.Y >= 0 && position.Y + checkbox.ActualHeight <= 720,
                        $"{language}/{width} renders the startup option inside the settings viewport");
                    var bitmap = new RenderTargetBitmap(width, 720, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(output, $"startup-{language}-{width}.png"));
                    encoder.Save(stream);
                }
            }
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
            File.Delete(executable);
            window.Close();
        }
    }

    private static void VerifyWindowsStartup()
    {
        var keyPath = @"Software\AIMaster.Tests\Startup-" + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetTempPath(), "AIMaster startup 中文 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, "AIMaster.exe");
        File.WriteAllText(executable, "startup test fixture; never executed");
        try
        {
            var startup = new WindowsStartupService(executable, keyPath);
            Check(!startup.IsEnabled(), "startup is off when no Windows registration exists");
            using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
                key.SetValue("AnotherApp", "leave this alone");
            startup.SetEnabled(true);
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
                Check(startup.IsEnabled() && Equals(key!.GetValue("AIMaster"), $"\"{executable}\""),
                    "startup registers the quoted executable, including spaces and Unicode");
            startup.SetEnabled(true);
            Check(startup.IsEnabled(), "enabling startup repeatedly is safe");
            startup.SetEnabled(false);
            startup.SetEnabled(false);
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
                Check(!startup.IsEnabled() && Equals(key!.GetValue("AnotherApp"), "leave this alone"),
                    "disabling startup removes only AIMaster and is safe to repeat");
            File.Delete(executable);
            var rejected = false;
            try { startup.SetEnabled(true); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected && !startup.IsEnabled(), "a missing executable cannot create a broken startup entry");
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
            Directory.Delete(directory, recursive: true);
        }
    }
}
