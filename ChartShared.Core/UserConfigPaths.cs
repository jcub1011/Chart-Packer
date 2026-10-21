namespace ChartShared.Core;

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

    /// <summary>Chart Packer's settings file.</summary>
    public static string SettingsFile => Path.Combine(ConfigFolder, "settings.json");

    /// <summary>Chart Builder's settings file. Each app writes only its own (charter-spec §10).</summary>
    public static string BuilderSettingsFile => Path.Combine(ConfigFolder, "builder-settings.json");

    /// <summary>
    /// The default reduction rules (charter-spec C9 item 2, §10), read and written by both apps: Chart Builder's Reduce,
    /// and Chart Packer's Generate Lower Difficulties.
    /// </summary>
    public static string ReductionRulesFile => Path.Combine(ConfigFolder, "reduction-rules.json");

    /// <summary>
    /// The audio/visual latency per output device, written by Chart Builder's calibration tool and read
    /// by both apps (charter-spec C11, §10).
    /// </summary>
    public static string AudioLatencyFile => Path.Combine(ConfigFolder, "audio-latency.json");

    /// <summary>
    /// Chart Builder's instrument bindings: each profile's, and each device's offset and MIDI settings (charter-spec C19,
    /// §10).
    /// </summary>
    public static string InputBindingsFile => Path.Combine(ConfigFolder, "input-bindings.json");

    /// <summary>
    /// Where Chart Builder autosaves the chart open, for recovery after a crash (charter-spec C15 item 6,
    /// <c>RecoveryStore</c>). Never inside a song.
    /// </summary>
    public static string RecoveryFolder => Path.Combine(ConfigFolder, "Recovery");

    /// <summary>
    /// Where both apps write their logs: <c>%LOCALAPPDATA%\ChartPacker\Logs</c>,
    /// <c>~/Library/Logs/ChartPacker</c>, or <c>$XDG_STATE_HOME/ChartPacker/logs</c>
    /// (<c>~/.local/state</c> when unset). It is outside <see cref="ConfigFolder"/>, so a running app
    /// never changes the config folder while the tests' guard is watching it.
    /// </summary>
    public static string LogFolder
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChartPacker", "Logs");
            }

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (OperatingSystem.IsMacOS())
            {
                return Path.Combine(home, "Library", "Logs", "ChartPacker");
            }

            string? state = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            string root = string.IsNullOrEmpty(state) ? Path.Combine(home, ".local", "state") : state;
            return Path.Combine(root, "ChartPacker", "logs");
        }
    }
}
