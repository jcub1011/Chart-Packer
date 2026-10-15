namespace ChartShared.Core.Settings;

/// <summary>Search keywords for a setting, as its catalog writes them.</summary>
public static class SettingKeywords
{
    /// <summary>
    /// Splits a space-separated keyword string (a resource holding, say, "charter author name") into the
    /// words <see cref="SettingDefinition.Keywords"/> takes.
    /// </summary>
    public static string[] Split(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}
