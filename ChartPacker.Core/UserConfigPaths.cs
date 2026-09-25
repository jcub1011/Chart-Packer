namespace ChartPacker.Core;

/// <summary>
/// Locations of the real per-user config files (spec §8). Only the app's composition root should
/// use these; tests must use temp files and never read or write here.
/// </summary>
public static class UserConfigPaths
{
    /// <summary>
    /// <c>%APPDATA%\ChartPacker</c>, <c>~/Library/Application Support/ChartPacker</c>, or
    /// <c>$XDG_CONFIG_HOME/ChartPacker</c>.
    /// </summary>
    public static string ConfigFolder
    {
        get
        {
            // On macOS, ApplicationData maps to ~/.config; LocalApplicationData is ~/Library/Application Support.
            var root = Environment.GetFolderPath(OperatingSystem.IsMacOS()
                ? Environment.SpecialFolder.LocalApplicationData
                : Environment.SpecialFolder.ApplicationData);
            return Path.Combine(root, "ChartPacker");
        }
    }

    /// <summary>The app's settings file.</summary>
    public static string SettingsFile => Path.Combine(ConfigFolder, "settings.json");
}
