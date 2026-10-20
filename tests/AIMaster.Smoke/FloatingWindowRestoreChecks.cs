using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using AIMaster;
using AIMaster.Models;
using AIMaster.Services;

internal static partial class Program
{
    private static void VerifyFloatingWindowRestore(App app)
    {
        var directory = Path.Combine(Path.GetTempPath(), "AIMaster-floating-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        try
        {
            Check(!new AiManagerSettings().ShouldRestoreFloatingWindow,
                "a new installation leaves the floating monitor closed");
            var placement = new AiFloatingWindowPlacement { Left = 30, Top = 40, Width = 230, Height = 150 };
            var legacy = JsonSerializer.Deserialize<AiManagerSettings>(
                "{\"floatingWindowPlacement\":{\"left\":30,\"top\":40,\"width\":230,\"height\":150}}")!;
            Check(legacy.ShouldRestoreFloatingWindow && legacy.FloatingWindowEnabled is null,
                "legacy settings with saved placement recover the floating monitor after updating");
            Check(!new AiManagerSettings
                { FloatingWindowPlacement = new AiFloatingWindowPlacement() }.ShouldRestoreFloatingWindow,
                "invalid legacy placement does not enable the floating monitor");
            var settings = new AiManagerSettings
            {
                FloatingWindowEnabled = false, FloatingWindowPlacement = placement,
                FloatingFontScale = 1.4, FloatingRefreshSeconds = 5, FloatingAutoCollapse = false
            };
            File.WriteAllText(path, JsonSerializer.Serialize(settings));
            Check(!ReadSettings().ShouldRestoreFloatingWindow,
                "an explicit close overrides a valid saved placement after restart");
            Check(new AiManagerSettings { FloatingWindowEnabled = true }.ShouldRestoreFloatingWindow,
                "an enabled monitor reopens even before its first placement is saved");

            var floating = CreateOfflineWindow();
            floating.Show();
            Check(ReadSettings().FloatingWindowEnabled == true,
                "showing the monitor persists its enabled state immediately");
            var changed = ReadSettings();
            changed.WarningPercent = 65;
            File.WriteAllText(path, JsonSerializer.Serialize(changed));
            floating.Close();
            var closed = ReadSettings();
            Check(closed.FloatingWindowEnabled == false && !closed.ShouldRestoreFloatingWindow &&
                  closed.FloatingWindowPlacement is { IsValid: true } && closed.WarningPercent == 65,
                "manual close disables reopening while keeping placement and other windows' changes");

            floating = CreateOfflineWindow();
            floating.Show();
            app.ExitApplication();
            var restarted = ReadSettings();
            Check(restarted.FloatingWindowEnabled == true && restarted.ShouldRestoreFloatingWindow,
                "application/update shutdown keeps the monitor enabled for the next launch");
            Check(restarted.FloatingFontScale == 1.4 && restarted.FloatingRefreshSeconds == 5 &&
                  !restarted.FloatingAutoCollapse && restarted.WarningPercent == 65,
                "the restart state retains font size, refresh interval, dock preference, and guard settings");
        }
        finally
        {
            if (!app.IsExiting) app.ExitApplication();
            Directory.Delete(directory, recursive: true);
        }

        AiManagerSettings ReadSettings() =>
            JsonSerializer.Deserialize<AiManagerSettings>(File.ReadAllText(path))!;

        AiFloatingWindow CreateOfflineWindow()
        {
            var window = new AiFloatingWindow(new AiManagerService(settingsPath: path));
            // Exercise WPF visibility/close events without starting Codex or network polling.
            var loaded = typeof(AiFloatingWindow).GetMethod("Window_Loaded", BindingFlags.Instance | BindingFlags.NonPublic)!;
            window.Loaded -= loaded.CreateDelegate<RoutedEventHandler>(window);
            return window;
        }
    }
}
