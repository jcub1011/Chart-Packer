using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChartPacker.Core.Settings;

/// <summary>
/// What Chart Builder reads of Chart Packer's <c>settings.json</c> (charter-spec §10). It's Chart Packer's file, so it's
/// only ever read here, never written.
/// </summary>
public static class ChartPackerSettingsFile
{
    /// <summary>
    /// The folder Chart Packer's library was last opened on, from its <c>LastFolder</c> setting, or null when the file or
    /// the setting is missing, unreadable or empty.
    /// </summary>
    /// <param name="path">The settings file, <see cref="UserConfigPaths.SettingsFile"/>.</param>
    public static string? LibraryRoot(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            return JsonFiles.ReadObject(path, warn: true)?["LastFolder"] is JsonValue value
                && value.GetValueKind() == JsonValueKind.String
                && value.GetValue<string>() is { } folder && !string.IsNullOrWhiteSpace(folder)
                ? folder
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
