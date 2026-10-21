using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ChartShared.Core.Charting.Authoring;

namespace ChartShared.Core.Settings;

/// <summary>
/// <c>reduction-rules.json</c>: the default rules Reduce starts from, per family and target difficulty (charter-spec C9
/// item 2, §10). Both apps read and write it: Chart Builder's Reduce dialog and Settings, and Chart Packer's quick fix for
/// missing difficulties.
/// </summary>
/// <remarks>
/// <para>It's read afresh by each <see cref="Load"/>, so rules one app saves are the other's next time it asks. A save
/// writes only the sets of rules this process changed since it last loaded or saved (<see cref="JsonSettingsFile{T}"/>).</para>
/// <para>The rules were Chart Builder's own setting before. Until the file is first saved, they're read from
/// <c>builder-settings.json</c>'s <c>ReductionRules</c>, which is never written here.</para>
/// <para>The paths are always passed in; tests pass temp files.</para>
/// </remarks>
public sealed class ReductionRulesFile
{
    private const string LegacyProperty = "ReductionRules";

    private readonly string _path;
    private readonly string? _legacyPath;
    private readonly JsonSettingsFile<ReductionDefaults> _file;

    /// <param name="path">The rules file, <see cref="UserConfigPaths.ReductionRulesFile"/>.</param>
    /// <param name="legacyPath">Chart Builder's settings file, <see cref="UserConfigPaths.BuilderSettingsFile"/>, to read rules saved there; null for none.</param>
    public ReductionRulesFile(string path, string? legacyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _legacyPath = legacyPath;
        _file = new JsonSettingsFile<ReductionDefaults>(path, ReductionRulesJsonContext.Default.ReductionDefaults);
    }

    /// <summary>
    /// The rules as the file has them now, every value kept to what it can be (<see cref="ReductionDefaults.Normalized"/>);
    /// before the file's first save, as Chart Builder's settings had them; otherwise the shipped rules.
    /// </summary>
    public ReductionDefaults Load()
    {
        var rules = _file.Load();
        if (!File.Exists(_path) && _legacyPath is not null && Legacy(_legacyPath) is { } legacy)
            rules = legacy;
        return rules.Normalized();
    }

    /// <summary>Writes <paramref name="rules"/>, keeping another process's changes to the sets this one didn't change.</summary>
    /// <returns>Whether the file was written.</returns>
    public bool Save(ReductionDefaults rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return _file.Save(rules.Normalized());
    }

    /// <summary>Writes the shipped rules for every family and target, those another process changed meanwhile too.</summary>
    /// <returns>Whether the file was written.</returns>
    public bool Reset() => _file.Replace(ReductionDefaults.Shipped.Normalized());

    // The rules Chart Builder's settings file holds, or null when it holds none it can read.
    private static ReductionDefaults? Legacy(string path)
    {
        try
        {
            return JsonFiles.ReadObject(path, warn: true)?[LegacyProperty] is JsonObject rules
                ? rules.Deserialize(ReductionRulesJsonContext.Default.ReductionDefaults)
                : null;
        }
        catch (JsonException ex)
        {
            Trace.TraceWarning($"Ignoring unreadable reduction rules in '{path}': {ex.Message}");
            return null;
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ReductionDefaults))]
internal sealed partial class ReductionRulesJsonContext : JsonSerializerContext
{
}
