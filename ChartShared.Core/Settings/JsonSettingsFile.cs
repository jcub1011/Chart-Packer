using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace ChartShared.Core.Settings;

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
/// half a file and two saves don't share a temp file. It holds <c>{file}.lock</c> from its read to the rename,
/// so two processes' saves can't interleave and lose one's changes.
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
    /// now. Failures are traced; settings are never worth stopping the app for.
    /// </summary>
    /// <returns>Whether the file was written.</returns>
    public bool Save(T settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var current = ToJson(settings);

        bool saved = JsonFiles.Update(_path, file =>
        {
            // When the file is missing or can't be read now, the settings as last loaded or saved stand in for it.
            var merged = file ?? (JsonObject)_baseline.DeepClone();
            foreach (var (name, value) in current)
            {
                if (!_baseline.TryGetPropertyValue(name, out var before) || !JsonNode.DeepEquals(before, value))
                    merged[name] = value?.DeepClone();
            }

            return merged;
        });

        if (saved)
            _baseline = current;
        return saved;
    }

    /// <summary>
    /// Writes every setting of <paramref name="settings"/> onto what the file holds now, those another process changed
    /// meanwhile too, as a reset to defaults must. Properties this version doesn't know are kept. Failures are traced.
    /// </summary>
    /// <returns>Whether the file was written.</returns>
    public bool Replace(T settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var current = ToJson(settings);

        bool saved = JsonFiles.Update(_path, file =>
        {
            var merged = file ?? [];
            foreach (var (name, value) in current)
                merged[name] = value?.DeepClone();
            return merged;
        });

        if (saved)
            _baseline = current;
        return saved;
    }

    private JsonObject ToJson(T settings) => JsonSerializer.SerializeToNode(settings, _typeInfo) as JsonObject ?? [];

    private JsonObject? Read(bool warn) => JsonFiles.ReadObject(_path, warn);
}
