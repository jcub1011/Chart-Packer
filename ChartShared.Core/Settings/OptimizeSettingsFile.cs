using System.Text.Json.Serialization;

namespace ChartShared.Core.Settings;

/// <summary>
/// <c>optimizer-settings.json</c>: the defaults the Library Optimization Tool starts from (<see cref="OptimizeSettings"/>).
/// Only Chart Packer reads and writes it.
/// </summary>
/// <remarks>The path is always passed in; tests pass temp files.</remarks>
public sealed class OptimizeSettingsFile
{
    /// <summary>The schema version this build writes to <c>optimizer-settings.json</c>.</summary>
    public const int SchemaVersion = 1;

    private readonly JsonSettingsFile<OptimizeSettings> _file;

    /// <param name="path">The settings file, <see cref="UserConfigPaths.OptimizerSettingsFile"/>.</param>
    public OptimizeSettingsFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _file = new JsonSettingsFile<OptimizeSettings>(path, OptimizeSettingsJsonContext.Default.OptimizeSettings, schemaVersion: SchemaVersion);
    }

    /// <summary>The defaults as the file has them now, every value kept to what it can be; the shipped defaults when there's no readable file.</summary>
    public OptimizeSettings Load() => _file.Load().Normalized();

    /// <summary>Writes <paramref name="settings"/>.</summary>
    /// <returns>Whether the file was written.</returns>
    public bool Save(OptimizeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return _file.Save(settings.Normalized());
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(OptimizeSettings))]
internal sealed partial class OptimizeSettingsJsonContext : JsonSerializerContext
{
}
