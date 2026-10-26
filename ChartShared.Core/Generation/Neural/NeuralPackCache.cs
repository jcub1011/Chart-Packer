namespace ChartShared.Core.Generation.Neural;

/// <summary>
/// The neural packs loaded so far, validated, with their model, shared by everything that reads them (the Reduce dialog's
/// options and the generator), so each pack is read and checked once. A pack replaced on disk (another size or write
/// time) is read again. Safe to use from any thread.
/// </summary>
public sealed class NeuralPackCache
{
    /// <summary>The cache the apps share.</summary>
    public static NeuralPackCache Shared { get; } = new();

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _loaded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The pack <paramref name="name"/> in <paramref name="store"/>, loading it when it isn't loaded as it is now.</summary>
    /// <exception cref="PackValidationException">The pack is missing, unreadable, damaged or unsupported.</exception>
    public (NeuralPack Pack, BiGruCrf Model) Load(IGeneratorModelStore store, string name)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var id = new GeneratorModelId(GeneratorType.NeuralDifficulty, name);
        string path;
        long length;
        DateTime written;
        try
        {
            path = store.ResolvePath(id);
            var info = new FileInfo(path);
            if (!info.Exists)
                throw new PackValidationException($"Neural pack '{name}' is not installed.");
            length = info.Length;
            written = info.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new PackValidationException($"Neural pack '{name}' can't be read ({ex.Message}).", ex);
        }

        lock (_gate)
        {
            if (_loaded.TryGetValue(path, out var entry) && entry.Length == length && entry.Written == written)
                return (entry.Pack, entry.Model);
        }

        var pack = NeuralPack.Load(store, id);
        var model = new BiGruCrf(pack);
        lock (_gate)
            _loaded[path] = new Entry(length, written, pack, model);
        return (pack, model);
    }

    private sealed record Entry(long Length, DateTime Written, NeuralPack Pack, BiGruCrf Model);
}
