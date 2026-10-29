using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ChartShared.Core.Settings;

namespace ChartShared.Core.Keybinds;

/// <summary>The shape of <c>keybinds.json</c>.</summary>
public sealed class KeybindsSettings
{
    /// <summary>
    /// Chord text by action id, for the actions whose keys differ from their defaults; an empty list is an action the user
    /// unbound. Ids this build doesn't know are kept as they are, since the other app, or a newer version, may.
    /// </summary>
    public Dictionary<string, string[]> Bindings { get; set; } = new(StringComparer.Ordinal);
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(KeybindsSettings))]
internal sealed partial class KeybindsJsonContext : JsonSerializerContext
{
}

/// <summary>Why a layout file couldn't be read.</summary>
public enum LayoutFailure
{
    None,

    /// <summary>The file is missing, can't be opened or isn't JSON.</summary>
    Unreadable,

    /// <summary>The file is larger than <see cref="KeybindsLayout.MaxBytes"/>.</summary>
    TooLarge,

    /// <summary>It's JSON, but not a keybinds layout.</summary>
    NotKeybinds,
}

/// <summary>A layout file's bindings, or why they couldn't be read.</summary>
public readonly record struct LayoutReadResult(IReadOnlyDictionary<string, string[]>? Bindings, LayoutFailure Failure);

/// <summary>
/// Reading and writing a keybind layout as its own file, to share: the same shape as <c>keybinds.json</c>. A layout read
/// from a file is untrusted, so it only ever goes through <see cref="KeyMap.ApplyLayout"/>, which checks every entry.
/// </summary>
public static class KeybindsLayout
{
    /// <summary>The largest layout file read. A real one is a few kilobytes.</summary>
    public const long MaxBytes = 1 << 20;

    public static LayoutReadResult Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            if (new FileInfo(path).Length > MaxBytes)
                return new LayoutReadResult(null, LayoutFailure.TooLarge);

            using var stream = File.OpenRead(path);
            if (JsonNode.Parse(stream) is not JsonObject { } root || root["Bindings"] is not JsonObject)
                return new LayoutReadResult(null, LayoutFailure.NotKeybinds);

            var settings = root.Deserialize(KeybindsJsonContext.Default.KeybindsSettings);
            return settings is null
                ? new LayoutReadResult(null, LayoutFailure.NotKeybinds)
                : new LayoutReadResult(settings.Bindings, LayoutFailure.None);
        }
        catch (JsonException)
        {
            // Includes a Bindings entry that isn't a list of text.
            return new LayoutReadResult(null, LayoutFailure.NotKeybinds);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LayoutReadResult(null, LayoutFailure.Unreadable);
        }
    }

    /// <summary>Writes the whole layout of <paramref name="map"/> to <paramref name="path"/>.</summary>
    /// <returns>Whether it was written.</returns>
    public static bool Write(string path, KeyMap map)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(map);

        var bindings = new JsonObject();
        foreach (var (id, chords) in map.Export())
            bindings[id] = new JsonArray([.. chords.Select(c => (JsonNode)JsonValue.Create(c)!)]);

        var file = new JsonObject { ["Bindings"] = bindings };
        SettingsSchema.Stamp(file, KeybindsService.SchemaVersion);
        return JsonFiles.WriteAtomically(path, file);
    }
}

/// <summary>
/// Loads and saves <c>keybinds.json</c> through <see cref="JsonSettingsFile{T}"/> and keeps the <see cref="KeyMap"/> it
/// fills. Both apps read and write the file, so a save merges by action id: it changes only the actions this process
/// changed and leaves the other app's. The path is always passed in; tests pass a temp file.
/// </summary>
public sealed class KeybindsService
{
    /// <summary>The schema version this build writes to <c>keybinds.json</c>.</summary>
    public const int SchemaVersion = 1;

    private readonly string _path;
    private readonly JsonSettingsFile<KeybindsSettings> _file;
    private KeybindsSettings _settings;
    private DateTime _loadedWriteTime;
    private long _loadedLength;

    private KeybindsService(string path, KeybindCatalog catalog)
    {
        _path = path;
        Map = new KeyMap(catalog);
        _file = new JsonSettingsFile<KeybindsSettings>(
            path, KeybindsJsonContext.Default.KeybindsSettings, [nameof(KeybindsSettings.Bindings)], SchemaVersion);
        _settings = new KeybindsSettings();
    }

    /// <summary>The keys, as the file has them (see <see cref="Reload"/>) plus whatever has been changed since.</summary>
    public KeyMap Map { get; }

    /// <summary>Loads the keys from <paramref name="filePath"/>, falling back to the defaults when it's missing or unreadable.</summary>
    public static KeybindsService Load(string filePath, KeybindCatalog catalog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(catalog);
        var service = new KeybindsService(filePath, catalog);
        service.Read();
        return service;
    }

    /// <summary>
    /// Writes the actions whose keys this process changed, keeping the other app's changes to the rest. An action put back to
    /// its defaults is taken out of the file.
    /// </summary>
    /// <remarks>
    /// The file's save is what keeps the other app's changes: it writes only the entries that differ from what was last
    /// loaded or saved, onto what the file holds now. So the file may hold a change here that the map hasn't, and the map is
    /// read from it again after a save, or it would show the old key until the next start.
    /// </remarks>
    /// <returns>Whether the file holds this process's keys.</returns>
    public bool Save()
    {
        var bindings = _settings.Bindings;
        foreach (var action in Map.Catalog)
            bindings.Remove(action.Id);
        foreach (var (id, chords) in Map.Overrides)
            bindings[id] = [.. chords.Select(c => c.ToString())];

        bool saved = _file.Save(_settings);
        if (saved)
            Resync();
        return saved;
    }

    /// <summary>
    /// Reads the file again if another process wrote it since it was last read or written, as when a key was rebound in the
    /// other app. Keys changed here and not yet saved are replaced.
    /// </summary>
    /// <returns>Whether the keys were reloaded.</returns>
    public bool ReloadIfChanged()
    {
        try
        {
            var info = new FileInfo(_path);
            if (info.Exists ? info.LastWriteTimeUtc == _loadedWriteTime && info.Length == _loadedLength : _loadedLength == 0)
                return false;
        }
        catch (IOException)
        {
            return false;
        }

        Read();
        return true;
    }

    private void Read()
    {
        _settings = _file.Load();
        _settings.Bindings ??= new Dictionary<string, string[]>(StringComparer.Ordinal);
        var applied = Map.ApplyLayout(_settings.Bindings.Where(b => Map.Catalog.Contains(b.Key)).ToDictionary(b => b.Key, b => b.Value), replace: true);
        if (applied.Ignored > 0)
            Trace.TraceWarning($"Ignored {applied.Ignored} unusable entries in '{_path}'.");
        Remember();
    }

    // Takes the file as a save left it. A file that can't be read just now is left to the next reload, rather than read as
    // empty, which would put every key back to its default.
    private void Resync()
    {
        if (JsonFiles.ReadObject(_path, warn: false) is null)
        {
            Remember();
            return;
        }

        Read();
    }

    private void Remember()
    {
        var info = new FileInfo(_path);
        _loadedWriteTime = info.Exists ? info.LastWriteTimeUtc : default;
        _loadedLength = info.Exists ? info.Length : 0;
    }
}
