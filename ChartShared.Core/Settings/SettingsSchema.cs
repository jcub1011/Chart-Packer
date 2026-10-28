using System.Text.Json.Nodes;

namespace ChartShared.Core.Settings;

/// <summary>
/// The schema version every settings file records: <c>SchemaVersion</c>, an integer saying which shape the file's
/// settings have (charter-spec §10). A file without one is version 0, a file written before anything recorded a
/// version. It's there so a later build can migrate a drastically changed schema rather than guess from absent
/// properties.
/// </summary>
/// <remarks>
/// <see cref="Stamp"/> only ever raises a file's version. A build that finds a file from a newer schema doesn't write it
/// at all (<see cref="JsonFiles.Update(string, int, Func{JsonObject?, JsonObject?})"/>), so an older build can't leave a
/// file labelled with the newer version holding settings in the older shape.
/// </remarks>
internal static class SettingsSchema
{
    /// <summary>The top-level JSON property holding the version.</summary>
    public const string PropertyName = "SchemaVersion";

    /// <summary>
    /// The version <paramref name="file"/> records: its integer <see cref="PropertyName"/>, or 0 when the file is
    /// missing, doesn't hold one, or holds something that isn't a whole number.
    /// </summary>
    public static int Read(JsonObject? file) =>
        file?[PropertyName] is JsonValue value && value.TryGetValue<int>(out var version) && version > 0 ? version : 0;

    /// <summary>
    /// Records <paramref name="version"/> in <paramref name="file"/> when it has no version or an older one. A newer
    /// version is kept as the file has it.
    /// </summary>
    public static void Stamp(JsonObject file, int version)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (version > Read(file))
            file[PropertyName] = version;
    }
}
