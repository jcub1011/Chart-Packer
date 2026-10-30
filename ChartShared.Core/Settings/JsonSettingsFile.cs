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
/// two processes each changing a different one keep both. A save with nothing changed writes nothing,
/// unless the file's schema version is older, which the save stamps.
/// </para>
/// <para>
/// The file records a schema version (<see cref="SettingsSchema.PropertyName"/>, an integer) so a later build can
/// migrate a drastically changed schema. A save stamps this build's <see cref="SchemaVersion"/> into a file that has an
/// older one, or none (0), even when no setting changed, unless the file can't be read then. A file from a newer build is
/// left as it is: this build would write its settings in a shape the newer version doesn't have, so a save or replace of
/// it is refused (and traced). Reading never writes, so an older file is stamped by the next save.
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
    private readonly int _schemaVersion;

    // The settings as last loaded or saved: what a save compares with to find what this process changed.
    private JsonObject _baseline = [];

    // The schema version the file recorded when it was last loaded or saved: what a save compares with to stamp it.
    private int _loadedVersion;

    /// <param name="mergedByEntry">
    /// The top-level properties, by JSON name, that a save merges entry by entry rather than replacing whole: each a
    /// JSON object keyed by name. Null for none.
    /// </param>
    /// <param name="schemaVersion">
    /// The schema version this build writes to the file, an integer (see <see cref="SettingsSchema"/>). A save stamps it
    /// into a file that has an older one or none; a file from a newer build isn't written. 0 records no version.
    /// </param>
    public JsonSettingsFile(string path, JsonTypeInfo<T> typeInfo, IReadOnlyCollection<string>? mergedByEntry = null, int schemaVersion = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(typeInfo);
        _path = path;
        _typeInfo = typeInfo;
        _mergedByEntry = new HashSet<string>(mergedByEntry ?? [], StringComparer.Ordinal);
        _schemaVersion = Math.Max(0, schemaVersion);
    }

    /// <summary>The schema version this file's saves record; 0 when it records none.</summary>
    public int SchemaVersion => _schemaVersion;

    /// <summary>
    /// The schema version the file recorded when it was last loaded or saved: 0 for a file written before anything
    /// recorded a version. A migration after <see cref="Load"/> reads this to know what it's migrating from.
    /// </summary>
    public int LoadedVersion => _loadedVersion;

    /// <summary>Reads the settings, or gives defaults when the file is missing or unreadable.</summary>
    public T Load()
    {
        var file = Read(warn: true);
        _loadedVersion = SettingsSchema.Read(file);
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
    /// <returns>
    /// Whether the file holds this process's settings: true too when there was nothing to write, even if a version stamp
    /// couldn't be. False when the file is from a newer build.
    /// </returns>
    public bool Save(T settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var current = ToJson(settings);

        var changed = current.Where(p => !_baseline.TryGetPropertyValue(p.Key, out var before) || !JsonNode.DeepEquals(before, p.Value)).ToList();
        // A file without this build's version is stamped by the next save, even when no setting changed.
        bool stamp = _loadedVersion < _schemaVersion;
        // A missing file is still written, so a save makes one.
        bool settingsPending = changed.Count > 0 || !File.Exists(_path);
        if (!settingsPending && !stamp)
            return true;

        bool saved = Write(current, file =>
        {
            // A stamp alone is never worth rebuilding a file that can't be read now (locked, say) from the baseline.
            if (file is null && !settingsPending)
                return null;

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

        // A stamp that wasn't written leaves nothing unsaved: the settings had nothing to write.
        return saved || !settingsPending;
    }

    /// <summary>
    /// Writes every setting of <paramref name="settings"/> onto what the file holds now, those another process changed
    /// meanwhile too, as a reset to defaults must. Properties this version doesn't know are kept. Failures are traced.
    /// </summary>
    /// <returns>Whether the file was written: false when it's from a newer build.</returns>
    public bool Replace(T settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var current = ToJson(settings);

        return Write(current, file =>
        {
            var merged = file ?? [];
            foreach (var (name, value) in current)
                merged[name] = value?.DeepClone();
            return merged;
        });
    }

    // Has merge change the file under its lock and writes it, stamped with this build's version, unless the file is from a
    // newer build. When written, current is what a later save compares with.
    private bool Write(JsonObject current, Func<JsonObject?, JsonObject?> merge)
    {
        bool saved = JsonFiles.Update(_path, _schemaVersion, merge);
        if (saved)
        {
            _baseline = current;
            _loadedVersion = Math.Max(_loadedVersion, _schemaVersion);
        }
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
