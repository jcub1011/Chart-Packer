using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace ChartPacker.Core.Settings;

/// <summary>
/// An app's settings file, as JSON: Chart Packer's <c>settings.json</c> and Chart Builder's
/// <c>builder-settings.json</c> (charter-spec §10).
/// </summary>
/// <remarks>
/// <para>
/// More than one process may use a file at once (two Chart Builders, say), so a save never writes back
/// what this process merely loaded. It re-reads the file and replaces only the top-level properties this
/// process changed since it last loaded or saved, so another process's changes to other settings are
/// kept. Properties this version doesn't know are kept too.
/// </para>
/// <para>
/// A save writes a temp file of its own beside the file and renames it into place, so a reader never sees
/// half a file and two saves don't share a temp file.
/// </para>
/// <para>
/// The path is always passed in, never defaulted, so only a composition root (which passes a
/// <see cref="UserConfigPaths"/> file) touches real settings. Tests must pass a temp file.
/// </para>
/// </remarks>
/// <typeparam name="T">The settings type, with a source-generated <see cref="JsonTypeInfo{T}"/>.</typeparam>
public sealed class JsonSettingsFile<T>
    where T : class, new()
{
    private readonly string _path;
    private readonly JsonTypeInfo<T> _typeInfo;

    // The settings as last loaded or saved: what a save compares with to find what this process changed.
    private JsonObject _baseline = [];

    public JsonSettingsFile(string path, JsonTypeInfo<T> typeInfo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(typeInfo);
        _path = path;
        _typeInfo = typeInfo;
    }

    /// <summary>Reads the settings, or gives defaults when the file is missing or unreadable.</summary>
    public T Load()
    {
        var file = Read(warn: true);
        T? settings = null;
        if (file is not null)
        {
            try
            {
                settings = file.Deserialize(_typeInfo);
            }
            catch (JsonException ex)
            {
                Trace.TraceWarning($"Ignoring unreadable settings file '{_path}': {ex.Message}");
            }
        }

        settings ??= new T();
        _baseline = ToJson(settings);
        return settings;
    }

    /// <summary>
    /// Writes the settings this process changed since it last loaded or saved, onto what the file holds
    /// now. Failures are traced and otherwise ignored; settings are never worth stopping the app for.
    /// </summary>
    public void Save(T settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var current = ToJson(settings);

        // When the file is missing or can't be read now, the settings as last loaded or saved stand in for it.
        var merged = Read(warn: false) ?? (JsonObject)_baseline.DeepClone();
        foreach (var (name, value) in current)
        {
            if (!_baseline.TryGetPropertyValue(name, out var before) || !JsonNode.DeepEquals(before, value))
                merged[name] = value?.DeepClone();
        }

        string tempPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            using (var stream = File.Create(tempPath))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                merged.WriteTo(writer);
            }
            File.Move(tempPath, _path, overwrite: true);
            _baseline = current;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Couldn't save settings to '{_path}': {ex.Message}");
            try { File.Delete(tempPath); } catch (Exception) { }
        }
    }

    private JsonObject ToJson(T settings) => JsonSerializer.SerializeToNode(settings, _typeInfo) as JsonObject ?? [];

    // The file's JSON object, or null when it's missing, unreadable or not an object.
    private JsonObject? Read(bool warn)
    {
        try
        {
            if (!File.Exists(_path))
                return null;

            using var stream = File.OpenRead(_path);
            return JsonNode.Parse(stream) as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            if (warn)
                Trace.TraceWarning($"Ignoring unreadable settings file '{_path}': {ex.Message}");
            return null;
        }
    }
}
