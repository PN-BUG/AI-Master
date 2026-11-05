using Microsoft.Win32;

namespace AIMaster.Services;

/// <summary>Manages this user's opt-in Windows logon registration.</summary>
internal sealed class WindowsStartupService
{
    private const string ValueName = "AIMaster";
    private readonly string? _executablePath;
    private readonly string _runKeyPath;

    internal WindowsStartupService(string? executablePath = null, string? runKeyPath = null)
    {
        _executablePath = executablePath ?? Environment.ProcessPath;
        _runKeyPath = runKeyPath ?? @"Software\Microsoft\Windows\CurrentVersion\Run";
    }

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath);
        return key?.GetValue(ValueName) is string command && !string.IsNullOrWhiteSpace(command);
    }

    public void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        if (string.IsNullOrWhiteSpace(_executablePath) || !Path.IsPathFullyQualified(_executablePath) ||
            !string.Equals(Path.GetExtension(_executablePath), ".exe", StringComparison.OrdinalIgnoreCase) ||
            _executablePath.Contains('"') || !File.Exists(_executablePath))
            throw new InvalidOperationException(LocalizationService.L(
                "无法找到 AIMaster 程序，请从解压后的 AIMaster.exe 启动并重试。",
                "Cannot find the AIMaster executable. Run the extracted AIMaster.exe and try again."));

        using var runKey = Registry.CurrentUser.CreateSubKey(_runKeyPath, writable: true);
        runKey.SetValue(ValueName, $"\"{_executablePath}\"", RegistryValueKind.String);
    }
}
