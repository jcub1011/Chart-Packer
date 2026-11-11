using ChartShared.Core.Optimization;
using ChartShared.Core.Packing;
using ChartShared.Core.Scanning;
using ChartShared.Core.Sng;

namespace ChartShared.Core.Tests.Optimization;

/// <summary>Reports synchronously, so a test sees every report as it's made.</summary>
internal sealed class SyncProgress<T> : IProgress<T>
{
    public List<T> Reports { get; } = [];

    public void Report(T value) => Reports.Add(value);
}

/// <summary>
/// One fake for all three optimizers: it estimates a fraction of the sources' size and writes a file of that many
/// bytes, so a test says what it wants by the ratio, or by replacing <see cref="Estimate"/>.
/// </summary>
internal sealed class FakeOptimizer : IAudioOptimizer, IVideoOptimizer, IImageOptimizer
{
    public bool IsAvailable { get; set; } = true;

    /// <summary>What it expects optimizing the sources to give.</summary>
    public Func<IReadOnlyList<AssetFile>, OptimizeEstimate> Estimate { get; set; } = DefaultEstimate(0.5);

    /// <summary>The size of the file it writes; the estimate's by default.</summary>
    public Func<IReadOnlyList<AssetFile>, long>? WrittenLength { get; set; }

    /// <summary>Whether optimizing the sources fails.</summary>
    public Func<IReadOnlyList<AssetFile>, bool>? FailWhen { get; set; }

    /// <summary>Whether estimating the sources fails.</summary>
    public Func<IReadOnlyList<AssetFile>, bool>? FailEstimateWhen { get; set; }

    /// <summary>Called with the sources and destination as optimizing starts.</summary>
    public Action<IReadOnlyList<AssetFile>, string>? OnOptimize { get; set; }

    public List<string> Estimated { get; } = [];

    public List<string> Optimized { get; } = [];

    public static Func<IReadOnlyList<AssetFile>, OptimizeEstimate> DefaultEstimate(double ratio) => files =>
    {
        long length = files.Sum(f => f.Asset.Length);
        string extension = files[0].Asset.Kind switch
        {
            AssetKind.Audio => ".opus",
            AssetKind.Video => ".webm",
            _ => files[0].Asset.Extension,
        };
        return new OptimizeEstimate((long)(length * ratio), extension);
    };

    Task<OptimizeEstimate> IAudioOptimizer.EstimateAsync(IReadOnlyList<AssetFile> sources, AudioOptimizeOptions options, CancellationToken cancellationToken) =>
        EstimateCore(sources);

    Task<OptimizeEstimate> IVideoOptimizer.EstimateAsync(AssetFile source, VideoOptimizeOptions options, CancellationToken cancellationToken) =>
        EstimateCore([source]);

    Task<OptimizeEstimate> IImageOptimizer.EstimateAsync(AssetFile source, ImageOptimizeOptions options, CancellationToken cancellationToken) =>
        EstimateCore([source]);

    async Task IAudioOptimizer.OptimizeAsync(IReadOnlyList<AssetFile> sources, string destinationPath, AudioOptimizeOptions options, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        await OptimizeCore(sources, destinationPath, cancellationToken).ConfigureAwait(false);
        progress?.Report(1);
    }

    async Task IVideoOptimizer.OptimizeAsync(AssetFile source, string destinationPath, VideoOptimizeOptions options, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        await OptimizeCore([source], destinationPath, cancellationToken).ConfigureAwait(false);
        progress?.Report(1);
    }

    Task IImageOptimizer.OptimizeAsync(AssetFile source, string destinationPath, ImageOptimizeOptions options, CancellationToken cancellationToken) =>
        OptimizeCore([source], destinationPath, cancellationToken);

    private Task<OptimizeEstimate> EstimateCore(IReadOnlyList<AssetFile> sources)
    {
        Estimated.Add(string.Join("+", sources.Select(s => s.Asset.StoredName)));
        if (FailEstimateWhen?.Invoke(sources) == true)
            throw new IOException("Can't read the file to estimate it.");
        foreach (var source in sources)
            Assert.IsTrue(File.Exists(source.Path), $"'{source.Path}' is readable when it's estimated.");
        return Task.FromResult(Estimate(sources));
    }

    private async Task OptimizeCore(IReadOnlyList<AssetFile> sources, string destination, CancellationToken cancellationToken)
    {
        Optimized.Add(string.Join("+", sources.Select(s => s.Asset.StoredName)));
        foreach (var source in sources)
            Assert.IsTrue(File.Exists(source.Path), $"'{source.Path}' is readable when it's optimized.");

        OnOptimize?.Invoke(sources, destination);
        cancellationToken.ThrowIfCancellationRequested();
        long length = WrittenLength?.Invoke(sources) ?? Estimate(sources).OutputBytes;
        // The partial file is left behind before a failure, as a real encoder's would be.
        await File.WriteAllBytesAsync(destination, OptimizationTestSupport.Bytes(length, 7), cancellationToken).ConfigureAwait(false);
        if (FailWhen?.Invoke(sources) == true)
            throw new InvalidDataException("The optimizer failed.");
    }
}

/// <summary>Probes by file name, from a table.</summary>
internal sealed class FakeProbe : IAudioProbe, IVideoProbe, IImageProbe
{
    public Dictionary<string, ProbeResult> Results { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Probed { get; } = [];

    public Task<ProbeResult?> ProbeAsync(string name, Stream stream, CancellationToken cancellationToken)
    {
        Probed.Add(name);
        // The stream is the file's content, readable and seekable.
        Assert.IsTrue(stream.CanRead && stream.CanSeek);
        return Task.FromResult(Results.GetValueOrDefault(name));
    }
}

/// <summary>Synthetic songs on disk, and checks of what's left of them. No real chart is used.</summary>
internal static class OptimizationTestSupport
{
    /// <summary>Deterministic bytes that aren't all alike.</summary>
    public static byte[] Bytes(long length, int seed)
    {
        var bytes = new byte[length];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)((i * 31) + seed);
        return bytes;
    }

    /// <summary>A song folder with a <c>song.ini</c> and <paramref name="files"/>.</summary>
    public static SongEntry Folder(string root, string name, string? songIni = null, params (string Name, byte[] Data)[] files)
    {
        string folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "song.ini"), songIni ?? $"[song]\nname = {name}\n");
        foreach (var (file, data) in files)
            File.WriteAllBytes(Path.Combine(folder, file), data);
        return new SongEntry(SongEntryKind.SongFolder, folder, name, name);
    }

    /// <summary>A <c>.sng</c> with <paramref name="metadata"/> and <paramref name="files"/>.</summary>
    public static async Task<SongEntry> SngAsync(string root, string name, IReadOnlyList<KeyValuePair<string, string>>? metadata = null, params (string Name, byte[] Data)[] files)
    {
        string source = Path.Combine(Path.GetTempPath(), "chartpacker-optsrc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        try
        {
            var sources = new List<SngSourceFile>();
            foreach (var (file, data) in files)
            {
                string path = Path.Combine(source, file);
                await File.WriteAllBytesAsync(path, data);
                sources.Add(new SngSourceFile(file, path, data.Length));
            }

            string sng = Path.Combine(root, name + ".sng");
            await SngWriter.WriteAsync(sng, SngMask.Generate(), metadata ?? [KeyValuePair.Create("name", name)], sources, overwrite: false);
            return new SongEntry(SongEntryKind.SngFile, sng, name + ".sng", name);
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }
    }

    /// <summary>The stored file <paramref name="name"/> of <paramref name="entry"/>, or null.</summary>
    public static Task<byte[]?> ReadAsync(SongEntry entry, string name) => SongFile.ReadAsync(entry, name, long.MaxValue);

    /// <summary>The names of the files directly in <paramref name="entry"/>.</summary>
    public static IReadOnlyList<string> Names(SongEntry entry) =>
        [.. SongFile.ListTopLevel(entry).Select(f => f.Name).Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Every temp file or folder an optimizer run could leave under <paramref name="root"/>.</summary>
    public static IReadOnlyList<string> Leftovers(string root) =>
    [
        .. Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(StagingPath.Suffix, StringComparison.Ordinal) || p.EndsWith(SngWriter.TempSuffix, StringComparison.Ordinal)
                || p.Contains(StagingPath.Suffix + Path.DirectorySeparatorChar, StringComparison.Ordinal)),
    ];

    /// <summary>All options on, in place, with <paramref name="margin"/>.</summary>
    public static OptimizeOptions AllOn(double margin = 0.1, string? outputFolder = null) => new(
        new MetadataCleanupOptions(Enabled: true),
        new AudioOptimizeOptions(Enabled: true),
        new AudioOptimizeOptions(Enabled: false),
        new VideoOptimizeOptions(Enabled: true),
        new ImageOptimizeOptions(Enabled: true),
        new ImageOptimizeOptions(Enabled: true),
        margin,
        outputFolder);

    /// <summary>Only <paramref name="strategy"/> on.</summary>
    public static OptimizeOptions Only(OptimizeStrategy strategy, double margin = 0.1, string? outputFolder = null)
    {
        var all = AllOn(margin, outputFolder);
        return all with
        {
            Metadata = all.Metadata with { Enabled = strategy == OptimizeStrategy.Metadata },
            Audio = all.Audio with { Enabled = strategy == OptimizeStrategy.Audio },
            StemMixdown = all.StemMixdown with { Enabled = strategy == OptimizeStrategy.StemMixdown },
            Video = all.Video with { Enabled = strategy == OptimizeStrategy.Video },
            AlbumArt = all.AlbumArt with { Enabled = strategy == OptimizeStrategy.AlbumArt },
            BackgroundArt = all.BackgroundArt with { Enabled = strategy == OptimizeStrategy.BackgroundArt },
        };
    }

    /// <summary>The planner's actions for one strategy, by the first source name.</summary>
    public static PlannedAction ActionFor(ChartPlan plan, OptimizeStrategy strategy, string? source = null) =>
        plan.Actions.Single(a => a.Strategy == strategy && (source is null || a.SourceNames.Contains(source, StringComparer.OrdinalIgnoreCase)));

    /// <summary>An already-inspected chart of the given assets, for planner tests that don't need a song on disk.</summary>
    public static ChartAssets Assets(SongEntry entry, params ChartAsset[] assets) => new(entry, assets);

    public static ChartAsset Asset(string name, long length, ProbeResult? probe = null)
    {
        var kind = AssetInspector.Classify(name);
        return new ChartAsset(name, Path.GetExtension(name).ToLowerInvariant(), length, kind, kind == AssetKind.Audio ? AudioStems.StemOf(name) : null, probe);
    }
}
