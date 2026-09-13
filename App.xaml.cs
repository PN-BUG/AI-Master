using System.Globalization;
using System.Text.Json;
using System.Windows;
using AIMaster.Services;

namespace AIMaster;

public partial class App : WpfApplication
{
    private Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        LocalizationService.Initialize(LoadSavedLanguage() ?? CultureInfo.CurrentUICulture.Name);
        _instanceMutex = new Mutex(true, "AIMaster_Standalone_v1", out var createdNew);
        if (!createdNew)
        {
            WpfMessageBox.Show(
                LocalizationService.IsEnglish ? "AIMaster is already running." : "AIMaster 已在运行中。",
                LocalizationService.IsEnglish ? "Notice" : "提示",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    private static string? LoadSavedLanguage()
    {
        try
        {
            var path = AppPaths.GetSettingsFile();
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("language", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
