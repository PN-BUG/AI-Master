namespace AIMaster.Services;

internal static class AppPaths
{
    private const string SettingsFileName = "settings.json";

    public static string GetSettingsFile()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIMaster");
        Directory.CreateDirectory(directory);

        var settingsFile = Path.Combine(directory, SettingsFileName);
        if (File.Exists(settingsFile)) return settingsFile;

        // One-time compatibility migration from the original SoftwareToolkit build.
        var legacyFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SoftwareToolkit",
            "ai-manager.json");
        if (File.Exists(legacyFile))
        {
            try
            {
                File.Copy(legacyFile, settingsFile, overwrite: false);
            }
            catch (IOException) when (File.Exists(settingsFile))
            {
                // Another instance completed migration first.
            }
        }

        return settingsFile;
    }
}
