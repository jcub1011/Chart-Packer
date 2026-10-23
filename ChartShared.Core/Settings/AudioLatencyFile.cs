using System.Text.Json.Nodes;

namespace ChartShared.Core.Settings;

/// <summary>Where a latency came from: the device's ID, its name, or neither.</summary>
public enum LatencySource
{
    Id,
    Name,
    Fallback,
}

/// <summary>
/// A device's latency: the calibrated part, where it was found, and the latency detected from the device that's added
/// to it, if any.
/// </summary>
/// <param name="Calibration">The latency calibrated for the device, or the fallback, in milliseconds.</param>
/// <param name="Detected">The latency detected from the device, in milliseconds, when it's known and detecting is on.</param>
public readonly record struct LatencyLookup(int Calibration, LatencySource Source, int? Detected = null)
{
    /// <summary>The whole latency, in milliseconds: <see cref="Calibration"/> plus <see cref="Detected"/>.</summary>
    public int Milliseconds => Calibration + (Detected ?? 0);
}

/// <summary>
/// <c>audio-latency.json</c>: the audio/visual latency measured by Chart Builder's calibration tool, per
/// output device (charter-spec C11, §10). The calibration tool writes it, in whichever app opens it.
/// </summary>
/// <remarks>
/// <para>
/// It holds two maps, from device ID and from device name to milliseconds, a fallback, and whether to
/// add the latency detected from a device: <c>{ "byId": { … }, "byName": { … }, "fallback": 0, "detect": true }</c>.
/// A device is looked up by its ID, then its name, then the fallback. A device's ID, where the
/// platform gives one, outlives its name.
/// </para>
/// <para>
/// The stored values are what the device's latency is beyond what's detected from it (issue #140): while
/// <c>detect</c> is on, which it is unless it's <c>false</c>, the detected latency is added to the value
/// found. Off, the value found is the whole latency.
/// </para>
/// <para>
/// Several processes may share the file, so a save re-reads it and changes only the device saved,
/// keeping every other entry and any keys it doesn't know. It writes a temp file and renames it into
/// place, holding <c>{file}.lock</c> from its read to the rename, as <see cref="JsonSettingsFile{T}"/> does.
/// The path is always passed in; tests pass a temp file.
/// </para>
/// </remarks>
public sealed class AudioLatencyFile
{
    /// <summary>The most a latency can be either way, in milliseconds.</summary>
    public const int MaxMilliseconds = 500;

    private const string ById = "byId";
    private const string ByName = "byName";
    private const string Fallback = "fallback";
    private const string Detect = "detect";

    private readonly string _path;

    public AudioLatencyFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>Whether the latency detected from a device is added to the value stored for it.</summary>
    public bool DetectEnabled => DetectIn(JsonFiles.ReadObject(_path, warn: true));

    /// <summary>
    /// The latency stored for the device with <paramref name="name"/> and <paramref name="id"/>, or the fallback, with
    /// <paramref name="detected"/>, the latency detected from it, added while <see cref="DetectEnabled"/>.
    /// </summary>
    public LatencyLookup Find(string name, string? id, int? detected = null)
    {
        var file = JsonFiles.ReadObject(_path, warn: true);
        int? added = detected is { } ms && DetectIn(file) ? Math.Clamp(ms, 0, MaxMilliseconds) : null;
        if (!string.IsNullOrEmpty(id) && ValueIn(file, ById, id) is { } byId)
            return new(byId, LatencySource.Id, added);
        if (!string.IsNullOrEmpty(name) && ValueIn(file, ByName, name) is { } byName)
            return new(byName, LatencySource.Name, added);
        return new(file is not null && Milliseconds(file[Fallback]) is { } fallback ? fallback : 0, LatencySource.Fallback, added);
    }

    /// <summary>
    /// <see cref="Find"/>, and when the value was found by name, <see cref="RememberUnderId"/>. For the calibration
    /// tool and Chart Builder's playback; Chart Packer's playback only finds.
    /// </summary>
    public LatencyLookup FindAndRemember(string name, string? id, int? detected = null)
    {
        var found = Find(name, id, detected);
        if (found.Source == LatencySource.Name)
            RememberUnderId(name, id);
        return found;
    }

    /// <summary>Turns <see cref="DetectEnabled"/> on or off, keeping everything else. Gives false when it couldn't be written.</summary>
    public bool SetDetect(bool on) => JsonFiles.Update(_path, file =>
    {
        file ??= [];
        file[Detect] = on;
        return file;
    });

    /// <summary>
    /// Stores <paramref name="milliseconds"/> for the device, under its name and its ID if it has one.
    /// Gives false when it couldn't be written, or when the device has neither.
    /// </summary>
    public bool Save(string name, string? id, int milliseconds)
    {
        if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(id))
            return false;

        milliseconds = Math.Clamp(milliseconds, -MaxMilliseconds, MaxMilliseconds);
        return JsonFiles.Update(_path, file =>
        {
            file ??= [];
            if (!string.IsNullOrEmpty(name))
                MapIn(file, ByName)[name] = milliseconds;
            if (!string.IsNullOrEmpty(id))
                MapIn(file, ById)[id] = milliseconds;
            return file;
        });
    }

    /// <summary>
    /// Copies the value found under a device's name to its ID, so that a later rename doesn't lose it.
    /// Does nothing when the device has no ID, already has a value under it, or has none under its name.
    /// </summary>
    public void RememberUnderId(string name, string? id)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
            return;

        // Looked for first without the lock, as it's nearly always there already, then again under it.
        var found = JsonFiles.ReadObject(_path, warn: false);
        if (ValueIn(found, ById, id) is not null || ValueIn(found, ByName, name) is null)
            return;

        JsonFiles.Update(_path, file =>
        {
            if (ValueIn(file, ById, id) is not null || ValueIn(file, ByName, name) is not { } byName)
                return null;

            MapIn(file!, ById)[id] = byName;
            return file;
        });
    }

    // On unless it says false, so a file from before it, or one without it, detects.
    private static bool DetectIn(JsonObject? file) =>
        !(file?[Detect] is JsonValue value && value.TryGetValue<bool>(out var on) && !on);

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
