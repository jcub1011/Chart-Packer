using System.Text.Json.Nodes;

namespace ChartPacker.Core.Settings;

/// <summary>Where a latency came from: the device's ID, its name, or neither.</summary>
public enum LatencySource
{
    Id,
    Name,
    Fallback,
}

/// <summary>A device's latency in milliseconds, and where it was found.</summary>
public readonly record struct LatencyLookup(int Milliseconds, LatencySource Source);

/// <summary>
/// <c>audio-latency.json</c>: the audio/visual latency measured by Chart Builder's calibration tool, per
/// output device (charter-spec C11, §10). Chart Builder writes it; Chart Packer only reads it.
/// </summary>
/// <remarks>
/// <para>
/// It holds two maps, from device ID and from device name to milliseconds, and a fallback:
/// <c>{ "byId": { … }, "byName": { … }, "fallback": 0 }</c>. A device is looked up by its ID, then its
/// name, then the fallback. A device's ID, where the platform gives one, outlives its name.
/// </para>
/// <para>
/// Several processes may share the file, so a save re-reads it and changes only the device saved,
/// keeping every other entry and any keys it doesn't know. It writes a temp file and renames it into
/// place, as <see cref="JsonSettingsFile{T}"/> does. The path is always passed in; tests pass a temp file.
/// </para>
/// </remarks>
public sealed class AudioLatencyFile
{
    /// <summary>The most a latency can be either way, in milliseconds.</summary>
    public const int MaxMilliseconds = 500;

    private const string ById = "byId";
    private const string ByName = "byName";
    private const string Fallback = "fallback";

    private readonly string _path;

    public AudioLatencyFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>The latency stored for the device with <paramref name="name"/> and <paramref name="id"/>.</summary>
    public LatencyLookup Find(string name, string? id)
    {
        var file = JsonFiles.ReadObject(_path, warn: true);
        if (!string.IsNullOrEmpty(id) && ValueIn(file, ById, id) is { } byId)
            return new(byId, LatencySource.Id);
        if (!string.IsNullOrEmpty(name) && ValueIn(file, ByName, name) is { } byName)
            return new(byName, LatencySource.Name);
        return new(file is not null && Milliseconds(file[Fallback]) is { } fallback ? fallback : 0, LatencySource.Fallback);
    }

    /// <summary>
    /// <see cref="Find"/>, and when the value was found by name, <see cref="RememberUnderId"/>. For Chart Builder,
    /// which owns the file; Chart Packer only finds.
    /// </summary>
    public LatencyLookup FindAndRemember(string name, string? id)
    {
        var found = Find(name, id);
        if (found.Source == LatencySource.Name)
            RememberUnderId(name, id);
        return found;
    }

    /// <summary>
    /// Stores <paramref name="milliseconds"/> for the device, under its name and its ID if it has one.
    /// Gives false when it couldn't be written, or when the device has neither.
    /// </summary>
    public bool Save(string name, string? id, int milliseconds)
    {
        if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(id))
            return false;

        milliseconds = Math.Clamp(milliseconds, -MaxMilliseconds, MaxMilliseconds);
        var file = JsonFiles.ReadObject(_path, warn: false) ?? [];
        if (!string.IsNullOrEmpty(name))
            MapIn(file, ByName)[name] = milliseconds;
        if (!string.IsNullOrEmpty(id))
            MapIn(file, ById)[id] = milliseconds;
        return JsonFiles.WriteAtomically(_path, file);
    }

    /// <summary>
    /// Copies the value found under a device's name to its ID, so that a later rename doesn't lose it.
    /// Does nothing when the device has no ID, already has a value under it, or has none under its name.
    /// </summary>
    public void RememberUnderId(string name, string? id)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
            return;

        var file = JsonFiles.ReadObject(_path, warn: false);
        if (ValueIn(file, ById, id) is not null || ValueIn(file, ByName, name) is not { } byName)
            return;

        MapIn(file!, ById)[id] = byName;
        JsonFiles.WriteAtomically(_path, file!);
    }

    private static int? ValueIn(JsonObject? file, string map, string key) =>
        file?[map] is JsonObject entries && entries.TryGetPropertyValue(key, out var value) ? Milliseconds(value) : null;

    // A stored value, clamped to the range, or null when it isn't a number.
    private static int? Milliseconds(JsonNode? value) =>
        value is JsonValue number && number.TryGetValue<double>(out var ms) && double.IsFinite(ms)
            ? (int)Math.Round(Math.Clamp(ms, -MaxMilliseconds, MaxMilliseconds))
            : null;

    private static JsonObject MapIn(JsonObject file, string map)
    {
        if (file[map] is JsonObject entries)
            return entries;

        var created = new JsonObject();
        file[map] = created;
        return created;
    }
}
