using System.Text.Json.Serialization.Metadata;

namespace ChartShared.Core.Generation;

/// <summary>
/// The generator models both apps share (issue #154): large model/weight files in one
/// configurable folder, each stored as <c>{GeneratorType}\{ModelName}.{MODEL_EXT}</c>.
/// </summary>
/// <remarks>
/// Generators read through this (never the folder directly), so storage can move without
/// them changing: <see cref="OpenRead"/> streams a model, <see cref="ResolvePath"/> gives
/// its path to runtimes that need a file, and the typed helpers cover JSON weights.
/// </remarks>
public interface IGeneratorModelStore
{
    /// <summary>The stored override folder, or null for the default.</summary>
    string? CustomFolder { get; }

    /// <summary>Where models are stored now: the override, or the default.</summary>
    string ResolvedFolder { get; }

    /// <summary>Stores the override folder, or back to the default when blank.</summary>
    /// <returns>Whether it was written.</returns>
    bool SetCustomFolder(string? folder);

    /// <summary>The full path <paramref name="id"/> is stored at in the current folder.</summary>
    string ResolvePath(GeneratorModelId id);

    /// <summary>Whether the current folder holds any models (temp files don't count).</summary>
    bool HasModels();

    /// <summary>Every model in the current folder, by kind then name. None when the folder is missing.</summary>
    IReadOnlyList<GeneratorModelInfo> List();

    /// <summary>Opens <paramref name="id"/> for reading.</summary>
    /// <exception cref="FileNotFoundException">The model isn't stored.</exception>
    Stream OpenRead(GeneratorModelId id);

    /// <summary>Reads all of <paramref name="id"/>'s bytes.</summary>
    /// <exception cref="FileNotFoundException">The model isn't stored.</exception>
    Task<byte[]> ReadAllBytesAsync(GeneratorModelId id, CancellationToken cancellationToken = default);

    /// <summary>Reads <paramref name="id"/> as JSON of <typeparamref name="T"/>.</summary>
    /// <exception cref="FileNotFoundException">The model isn't stored.</exception>
    /// <exception cref="System.Text.Json.JsonException">The model isn't readable JSON of <typeparamref name="T"/>.</exception>
    Task<T?> LoadJsonAsync<T>(GeneratorModelId id, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default);

    /// <summary>Stores <paramref name="source"/> as <paramref name="id"/>, landing atomically.</summary>
    /// <exception cref="IOException">A model is already stored there, without <paramref name="overwrite"/>.</exception>
    Task SaveAsync(GeneratorModelId id, Stream source, bool overwrite, CancellationToken cancellationToken = default);

    /// <summary>Copies <paramref name="sourcePath"/> into storage as <paramref name="id"/>.</summary>
    /// <exception cref="FileNotFoundException"><paramref name="sourcePath"/> isn't there.</exception>
    /// <exception cref="IOException">A model is already stored there, without <paramref name="overwrite"/>.</exception>
    Task ImportAsync(string sourcePath, GeneratorModelId id, bool overwrite, CancellationToken cancellationToken = default);

    /// <summary>Copies <paramref name="id"/> to <paramref name="destinationPath"/>.</summary>
    /// <exception cref="FileNotFoundException">The model isn't stored.</exception>
    /// <exception cref="IOException">Something is already at <paramref name="destinationPath"/>, without <paramref name="overwrite"/>.</exception>
    Task ExportAsync(GeneratorModelId id, string destinationPath, bool overwrite, CancellationToken cancellationToken = default);

    /// <summary>Deletes <paramref name="id"/> from storage.</summary>
    /// <returns>Whether a stored model was deleted.</returns>
    bool Delete(GeneratorModelId id);

    /// <summary>Renames <paramref name="id"/> to <paramref name="newName"/>, within its kind.</summary>
    /// <exception cref="FileNotFoundException">The model isn't stored.</exception>
    /// <exception cref="IOException">A model is already stored under <paramref name="newName"/>, without <paramref name="overwrite"/>.</exception>
    GeneratorModelId Rename(GeneratorModelId id, string newName, bool overwrite);

    /// <summary>Moves <paramref name="id"/> to <paramref name="newType"/> and <paramref name="newName"/>.</summary>
    /// <exception cref="FileNotFoundException">The model isn't stored.</exception>
    /// <exception cref="IOException">A model is already stored there, without <paramref name="overwrite"/>.</exception>
    GeneratorModelId Move(GeneratorModelId id, GeneratorType newType, string newName, bool overwrite);

    /// <summary>
    /// Copies the current folder's models to <paramref name="destinationFolder"/> (the source
    /// keeps its files). Copying onto the same folder does nothing.
    /// </summary>
    Task<GeneratorModelMigrationResult> MigrateCopyAsync(
        string destinationFolder, bool overwrite, CancellationToken cancellationToken = default);
}
