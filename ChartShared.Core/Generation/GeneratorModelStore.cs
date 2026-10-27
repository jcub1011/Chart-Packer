using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ChartShared.Core.Settings;

namespace ChartShared.Core.Generation;

/// <summary>What copying the models to a new storage folder did.</summary>
/// <param name="Copied">How many files were copied.</param>
/// <param name="Skipped">How many were left out: already there, without overwriting.</param>
/// <param name="Errors">The files that couldn't be copied, each with why.</param>
public sealed record GeneratorModelMigrationResult(int Copied, int Skipped, IReadOnlyList<(string File, string Reason)> Errors);

/// <summary>
/// Reads and writes the generator models both apps share (issue #154): large model/weight
/// files in one folder, each stored as <c>{GeneratorType}\{ModelName}.{MODEL_EXT}</c>.
/// Files stream, and saves land atomically (a temp file renamed into place), so a crash
/// never leaves half a model where a whole one was.
/// </summary>
/// <remarks>
/// <para>
/// The folder is configurable and shared: <paramref name="folders"/> keeps the override,
/// and <paramref name="defaultFolder"/> (the app's <see cref="UserConfigPaths.GeneratorModelsFolder"/>)
/// is used while none is set. The folder is always resolved afresh, so a change in one app
/// is the other's next time it asks.
/// </para>
/// <para>All paths are passed in; tests pass temp folders.</para>
/// </remarks>
public sealed class GeneratorModelStore(GeneratorModelFolderFile folders, string defaultFolder) : IGeneratorModelStore
{
    private const int CopyBufferSize = 81920;

    private readonly GeneratorModelFolderFile _folders = folders ?? throw new ArgumentNullException(nameof(folders));
    private readonly string _defaultFolder = !string.IsNullOrWhiteSpace(defaultFolder)
        ? defaultFolder
        : throw new ArgumentException("The default folder is blank.", nameof(defaultFolder));

    /// <summary>The stored override folder, or null for the default.</summary>
    public string? CustomFolder => _folders.LoadFolder();

    /// <summary>Where models are stored now: the override, or the default.</summary>
    public string ResolvedFolder => FullOrDefault(CustomFolder);

    /// <summary>Stores the override folder, or back to the default when blank.</summary>
    /// <returns>Whether it was written.</returns>
    public bool SetCustomFolder(string? folder) => _folders.SaveFolder(folder);

    /// <summary>The full path <paramref name="id"/> is stored at in the current folder.</summary>
    public string ResolvePath(GeneratorModelId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return Path.Combine(ResolvedFolder, GeneratorModelNaming.RelativePathFor(id));
    }

    /// <summary>Whether the current folder holds any models (temp files don't count).</summary>
    public bool HasModels() => List().Count > 0;

    /// <summary>Every model in the current folder's type subfolders, by kind then name. None when the folder is missing.</summary>
    public IReadOnlyList<GeneratorModelInfo> List()
    {
        string folder = ResolvedFolder;
        if (!Directory.Exists(folder))
            return [];

        var found = new List<GeneratorModelInfo>();
        foreach (var type in Enum.GetValues<GeneratorType>())
        {
            string dir = Path.Combine(folder, type.ToString());
            if (!Directory.Exists(dir))
                continue;

            foreach (string path in Directory.EnumerateFiles(dir))
            {
                if (IsTemp(path) || !GeneratorModelNaming.TryParseStoredPath(path, out var id) || id is null)
                    continue;

                long size = -1;
                DateTimeOffset modified = DateTimeOffset.MinValue;
                try
                {
                    var info = new FileInfo(path);
                    size = info.Length;
                    modified = info.LastWriteTimeUtc;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Trace.TraceWarning($"Couldn't read model file '{path}': {ex.Message}");
                }

                found.Add(new GeneratorModelInfo(id, path, size, modified));
            }
        }

        return [.. found.OrderBy(m => m.Id.Type).ThenBy(m => m.Id.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Opens <paramref name="id"/> for reading.</summary>
    /// <exception cref="FileNotFoundException">The model isn't stored.</exception>
    public Stream OpenRead(GeneratorModelId id)
    {
        string path = ResolvePath(id);
        if (!File.Exists(path))
            throw new FileNotFoundException($"No stored {id.Type} model is named '{id.Name}'.", path);

        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    /// <summary>Reads all of <paramref name="id"/>'s bytes.</summary>
    /// <exception cref="FileNotFoundException">The model isn't stored.</exception>
    public async Task<byte[]> ReadAllBytesAsync(GeneratorModelId id, CancellationToken cancellationToken = default)
    {
        await using var source = OpenRead(id);
        await using var copy = new MemoryStream();
        await source.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
        return copy.ToArray();
    }

    /// <summary>Reads <paramref name="id"/> as JSON of <typeparamref name="T"/>.</summary>
    /// <exception cref="FileNotFoundException">The model isn't stored.</exception>
    /// <exception cref="JsonException">The model isn't readable JSON of <typeparamref name="T"/>.</exception>
    public async Task<T?> LoadJsonAsync<T>(GeneratorModelId id, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        await using var source = OpenRead(id);
        return await JsonSerializer.DeserializeAsync(source, typeInfo, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stores <paramref name="source"/> as <paramref name="id"/>, landing atomically.
    /// </summary>
    /// <exception cref="IOException">A model is already stored there, without <paramref name="overwrite"/>.</exception>
    public async Task SaveAsync(GeneratorModelId id, Stream source, bool overwrite, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        string path = ResolvePath(id);
        if (!overwrite && File.Exists(path))
            throw new IOException($"A {id.Type} model named '{id.Name}' is already stored.");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = TempPathFor(path);
        try
        {
            await using (var dest = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None,
                           CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(dest, cancellationToken).ConfigureAwait(false);
                await dest.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, path, overwrite);
        }
        finally
        {
            DeleteQuietly(temp);
        }
    }

    /// <summary>
    /// Copies <paramref name="sourcePath"/> into storage as <paramref name="id"/>.
    /// </summary>
    /// <exception cref="FileNotFoundException"><paramref name="sourcePath"/> isn't there.</exception>
    /// <exception cref="IOException">A model is already stored there, without <paramref name="overwrite"/>.</exception>
    public async Task ImportAsync(string sourcePath, GeneratorModelId id, bool overwrite, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException($"No file is at '{sourcePath}'.", sourcePath);

        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await SaveAsync(id, source, overwrite, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Copies <paramref name="id"/> to <paramref name="destinationPath"/>.</summary>
    /// <exception cref="FileNotFoundException">The model isn't stored.</exception>
    /// <exception cref="IOException">Something is already at <paramref name="destinationPath"/>, without <paramref name="overwrite"/>.</exception>
    public async Task ExportAsync(GeneratorModelId id, string destinationPath, bool overwrite, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        string path = ResolvePath(id);
        if (!File.Exists(path))
            throw new FileNotFoundException($"No stored {id.Type} model is named '{id.Name}'.", path);

        if (!overwrite && (File.Exists(destinationPath) || Directory.Exists(destinationPath)))
            throw new IOException($"Something is already at '{destinationPath}'.");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var dest = new FileStream(destinationPath, overwrite ? FileMode.Create : FileMode.CreateNew,
            FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(dest, cancellationToken).ConfigureAwait(false);
        await dest.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes <paramref name="id"/> from storage.</summary>
    /// <returns>Whether a stored model was deleted.</returns>
    public bool Delete(GeneratorModelId id)
    {
        string path = ResolvePath(id);
        if (!File.Exists(path))
            return false;

        File.Delete(path);
        return true;
    }

    /// <summary>Renames <paramref name="id"/> to <paramref name="newName"/>, within its kind.</summary>
    /// <exception cref="FileNotFoundException">The model isn't stored.</exception>
    /// <exception cref="IOException">A model is already stored under <paramref name="newName"/>, without <paramref name="overwrite"/>.</exception>
    public GeneratorModelId Rename(GeneratorModelId id, string newName, bool overwrite)
    {
        ArgumentNullException.ThrowIfNull(id);
        return Move(id, id.Type, newName, overwrite);
    }

    /// <summary>Moves <paramref name="id"/> to <paramref name="newType"/> and <paramref name="newName"/>.</summary>
    /// <exception cref="FileNotFoundException">The model isn't stored.</exception>
    /// <exception cref="IOException">A model is already stored there, without <paramref name="overwrite"/>.</exception>
    public GeneratorModelId Move(GeneratorModelId id, GeneratorType newType, string newName, bool overwrite)
    {
        ArgumentNullException.ThrowIfNull(id);
        string validated = GeneratorModelNaming.ValidateName(newName);
        var moved = new GeneratorModelId(newType, validated);
        if (id == moved)
            return moved;

        string from = ResolvePath(id);
        if (!File.Exists(from))
            throw new FileNotFoundException($"No stored {id.Type} model is named '{id.Name}'.", from);

        string to = ResolvePath(moved);
        if (!overwrite && File.Exists(to))
            throw new IOException($"A {moved.Type} model named '{moved.Name}' is already stored.");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(to))!);
        File.Move(from, to, overwrite);
        return moved;
    }

    /// <summary>
    /// Copies the current folder's models to <paramref name="destinationFolder"/> (issue #154: the
    /// source keeps its files). Temp files aren't copied. Copying onto the same folder does nothing.
    /// </summary>
    public async Task<GeneratorModelMigrationResult> MigrateCopyAsync(
        string destinationFolder, bool overwrite, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFolder);
        string from = Path.GetFullPath(ResolvedFolder);
        string to = Path.GetFullPath(destinationFolder);
        if (string.Equals(from, to, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return new GeneratorModelMigrationResult(0, 0, []);

        int copied = 0;
        int skipped = 0;
        var errors = new List<(string File, string Reason)>();
        if (!Directory.Exists(from))
            return new GeneratorModelMigrationResult(0, 0, []);

        Directory.CreateDirectory(to);
        foreach (var info in List())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string dest = Path.Combine(to, GeneratorModelNaming.RelativePathFor(info.Id));
            if (!overwrite && File.Exists(dest))
            {
                skipped++;
                continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dest))!);
                await using var source = new FileStream(info.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var destStream = new FileStream(dest, overwrite ? FileMode.Create : FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await source.CopyToAsync(destStream, cancellationToken).ConfigureAwait(false);
                await destStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                copied++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add((GeneratorModelNaming.RelativePathFor(info.Id), ex.Message));
            }
        }

        return new GeneratorModelMigrationResult(copied, skipped, errors);
    }

    private string FullOrDefault(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return Path.GetFullPath(_defaultFolder);
        try
        {
            return Path.GetFullPath(folder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Ignoring invalid generator model folder '{folder}': {ex.Message}");
            return Path.GetFullPath(_defaultFolder);
        }
    }

    private static bool IsTemp(string path) =>
        path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

    private static string TempPathFor(string path) => $"{path}.{Guid.NewGuid():N}.tmp";

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Couldn't delete temp file '{path}': {ex.Message}");
        }
    }
}
