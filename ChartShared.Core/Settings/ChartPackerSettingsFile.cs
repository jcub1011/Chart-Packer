using System.Text.Json;
using System.Text.Json.Nodes;
using ChartShared.Core.Jobs;
using ChartShared.Core.Scanning;

namespace ChartShared.Core.Settings;

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

    /// <summary>
    /// Chart Packer's packing settings, for Chart Builder's Save and pack (charter-spec C17 item 3): its output folder,
    /// overwrite policy and flat layout, and its library folder. A setting that's missing or unreadable has Chart
    /// Packer's default.
    /// </summary>
    /// <param name="path">The settings file, <see cref="UserConfigPaths.SettingsFile"/>.</param>
    public static ChartPackerPackSettings PackSettings(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        JsonObject? settings;
        try
        {
            settings = JsonFiles.ReadObject(path, warn: true);
        }
        catch (JsonException)
        {
            settings = null;
        }

        return new ChartPackerPackSettings(
            StringOf(settings?["OutputFolder"]),
            StringOf(settings?["OverwritePolicy"]) is { } policy && Enum.TryParse<OverwritePolicy>(policy, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
                ? parsed
                : OverwritePolicy.Skip,
            settings?["FlatPackOutput"] is JsonValue flat && flat.GetValueKind() == JsonValueKind.True,
            StringOf(settings?["LastFolder"]));
    }

    private static string? StringOf(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>() is { } text && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
}

/// <summary>Chart Packer's packing settings, as Chart Builder reads them (<see cref="ChartPackerSettingsFile.PackSettings"/>).</summary>
/// <param name="OutputFolder">Where packed songs go; null for beside the songs, under the library folder.</param>
/// <param name="Policy">What to do when the <c>.sng</c> is already there.</param>
/// <param name="Flat">Whether every <c>.sng</c> goes directly in the output folder, rather than mirroring the library's folders.</param>
/// <param name="LibraryRoot">The folder Chart Packer's library was last opened on, or null.</param>
public sealed record ChartPackerPackSettings(string? OutputFolder, OverwritePolicy Policy, bool Flat, string? LibraryRoot)
{
    /// <summary>
    /// How Chart Packer would pack <paramref name="entry"/> from its library: the folder it counts as scanned from (the
    /// library folder when the song is in it, otherwise the song's own parent), where the <c>.sng</c> goes, and the layout.
    /// </summary>
    public (string ScanRoot, string Destination, OutputLayout Layout) PackOf(SongEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string song = Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry.FullPath));
        string parent = Path.GetDirectoryName(song) ?? song;
        string scanRoot = parent;
        if (LibraryRoot is { } library)
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(library));
            if (song.StartsWith(root + Path.DirectorySeparatorChar, OutputPaths.PathComparer == StringComparer.Ordinal ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                scanRoot = root;
        }

        return (scanRoot, OutputFolder ?? scanRoot, Flat ? OutputLayout.Flat : OutputLayout.Mirror);
    }
}
