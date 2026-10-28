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
/// kept. Properties this version doesn't know are kept too. A property merged by entry (a JSON object
/// keyed by name, as each window's placement is) has only the entries this process changed replaced, so
/// two processes each changing a different one keep both. A save with nothing changed writes nothing.
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
    private readonly HashSet<string> _mergedByEntry;

    // The settings as last loaded or saved: what a save compares with to find what this process changed.
    private JsonObject _baseline = [];

    /// <param name="mergedByEntry">
    /// The top-level properties, by JSON name, that a save merges entry by entry rather than replacing whole: each a
    /// JSON object keyed by name. Null for none.
    /// </param>
    public JsonSettingsFile(string path, JsonTypeInfo<T> typeInfo, IReadOnlyCollection<string>? mergedByEntry = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(typeInfo);
        _path = path;
        _typeInfo = typeInfo;
        _mergedByEntry = new HashSet<string>(mergedByEntry ?? [], StringComparer.Ordinal);
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
    /// <returns>Whether the file holds this process's settings: true too when there was nothing to write.</returns>
    public bool Save(T settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var current = ToJson(settings);

        var changed = current.Where(p => !_baseline.TryGetPropertyValue(p.Key, out var before) || !JsonNode.DeepEquals(before, p.Value)).ToList();
        // A missing file is still written, so a save makes one.
        if (changed.Count == 0 && File.Exists(_path))
            return true;

        bool saved = JsonFiles.Update(_path, file =>
        {
            // When the file is missing or can't be read now, the settings as last loaded or saved stand in for it.
            var merged = file ?? (JsonObject)_baseline.DeepClone();
            foreach (var (name, value) in changed)
            {
                if (_mergedByEntry.Contains(name) && merged[name] is JsonObject entries)
                    MergeEntries(entries, _baseline[name] as JsonObject, value as JsonObject ?? []);
                else
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

    /// <summary>
    /// Puts into <paramref name="entries"/>, as the file has them, the entries <paramref name="current"/> added or changed
    /// since <paramref name="before"/>, and takes out those it no longer has. Another process's entries are left be.
    /// </summary>
    private static void MergeEntries(JsonObject entries, JsonObject? before, JsonObject current)
    {
        foreach (var (key, value) in current)
        {
            if (before is null || !before.TryGetPropertyValue(key, out var was) || !JsonNode.DeepEquals(was, value))
                entries[key] = value?.DeepClone();
        }

        if (before is null)
            return;
        foreach (var (key, _) in before)
        {
            if (!current.ContainsKey(key))
                entries.Remove(key);
        }
    }

    private JsonObject ToJson(T settings) => JsonSerializer.SerializeToNode(settings, _typeInfo) as JsonObject ?? [];

    private JsonObject? Read(bool warn) => JsonFiles.ReadObject(_path, warn);
}
