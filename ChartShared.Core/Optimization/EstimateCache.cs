using System.Globalization;
using System.Text.Json.Serialization;
using ChartShared.Core.Dedup;
using ChartShared.Core.Jobs;
using ChartShared.Core.Settings;

namespace ChartShared.Core.Optimization;

/// <summary>
/// What the optimizers estimated for the files of charts, kept so a file is measured once: by the next plan of the same
/// chart, by the run that follows an estimate, and, once <see cref="Save"/>d, by the next session.
/// </summary>
/// <remarks>
/// <para>An estimate is kept under its chart's path and <see cref="EntryStamp"/>, the strategy, the files it read with
/// their sizes, and the settings that shape the output (<see cref="SettingsKey"/>). A chart that's changed has a new
/// stamp, which drops everything kept for it; a setting that's changed misses, and leaves the other strategies' estimates
/// in place. The savings margin isn't part of the key: it judges an estimate, it doesn't change one.</para>
/// <para>A chart that wasn't stamped is never cached. Safe to use from several threads.</para>
/// <para>The path is always passed in, never defaulted; tests pass temp files.</para>
/// </remarks>
public sealed class EstimateCache
{
    /// <summary>The schema version this build writes to the file.</summary>
    public const int SchemaVersion = 1;

    // What a save keeps at most, so a cache fed by many libraries doesn't grow without end. The charts seen this session are
    // kept first.
    private const int MaxChartsSaved = 50_000;

    // Which way of estimating a video the kept estimates were made by. Raised when the way changes, so that estimates made
    // the old way aren't taken for the new: 2 is two windows of 512 frames, with keyframes counted apart.
    private const int VideoMethod = 3;

    private readonly object _sync = new();
    private readonly Dictionary<string, Chart> _charts = new(OutputPaths.PathComparer);
    private readonly JsonSettingsFile<EstimateCacheData>? _file;
    private long _clock;
    private bool _dirty;

    private sealed class Chart(string stamp)
    {
        public string Stamp { get; } = stamp;

        public Dictionary<string, OptimizeEstimate> Estimates { get; } = new(StringComparer.Ordinal);

        public long LastUsed { get; set; }
    }

    /// <summary>A cache that lives as long as this object; nothing is read or written.</summary>
    public EstimateCache()
    {
    }

    /// <summary>A cache kept in the file at <paramref name="path"/>: what it holds is read now, and written by <see cref="Save"/>.</summary>
    public EstimateCache(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _file = new JsonSettingsFile<EstimateCacheData>(path, EstimateCacheJsonContext.Default.EstimateCacheData, schemaVersion: SchemaVersion);
        foreach (var (chartPath, saved) in _file.Load().Charts)
        {
            if (string.IsNullOrEmpty(chartPath) || saved?.Stamp is null || saved.Estimates is null)
                continue;

            var chart = new Chart(saved.Stamp);
            foreach (var (key, estimate) in saved.Estimates)
            {
                if (estimate?.Extension is { } extension && estimate.Bytes >= 0)
                    chart.Estimates[key] = new OptimizeEstimate(estimate.Bytes, extension, estimate.Optimal);
            }

            _charts[chartPath] = chart;
        }
    }

    /// <summary>How many estimates are held.</summary>
    public int Count
    {
        get
        {
            lock (_sync)
                return _charts.Values.Sum(c => c.Estimates.Count);
        }
    }

    /// <summary>The estimate kept for <paramref name="sources"/> of <paramref name="chart"/>, or null when there's none for the chart as it is now.</summary>
    /// <param name="settings">The strategy's settings, from <see cref="SettingsKey"/>.</param>
    public OptimizeEstimate? TryGet(ChartAssets chart, OptimizeStrategy strategy, IReadOnlyList<ChartAsset> sources, string settings)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(sources);
        if (chart.Stamp is not { } stamp)
            return null;

        lock (_sync)
        {
            if (!_charts.TryGetValue(chart.Entry.FullPath, out var held) || held.Stamp != StampKey(stamp))
                return null;

            held.LastUsed = ++_clock;
            return held.Estimates.GetValueOrDefault(UnitKey(strategy, sources, settings));
        }
    }

    /// <summary>Keeps <paramref name="estimate"/> for <paramref name="sources"/> of <paramref name="chart"/>. What was kept for the chart under another stamp goes.</summary>
    public void Set(ChartAssets chart, OptimizeStrategy strategy, IReadOnlyList<ChartAsset> sources, string settings, OptimizeEstimate estimate)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(estimate);
        if (chart.Stamp is not { } stamp)
            return;

        string stampKey = StampKey(stamp);
        lock (_sync)
        {
            if (!_charts.TryGetValue(chart.Entry.FullPath, out var held) || held.Stamp != stampKey)
                _charts[chart.Entry.FullPath] = held = new Chart(stampKey);

            held.LastUsed = ++_clock;
            held.Estimates[UnitKey(strategy, sources, settings)] = estimate;
            _dirty = true;
        }
    }

    /// <summary>Writes the cache to its file, when it has one and something's changed. A failure is traced, not thrown.</summary>
    /// <returns>Whether the file holds the cache.</returns>
    public bool Save()
    {
        if (_file is null)
            return false;

        EstimateCacheData data;
        lock (_sync)
        {
            if (!_dirty)
                return true;

            data = new EstimateCacheData();
            foreach (var (path, chart) in _charts.OrderByDescending(c => c.Value.LastUsed).Take(MaxChartsSaved))
            {
                data.Charts[path] = new EstimateCacheChart
                {
                    Stamp = chart.Stamp,
                    Estimates = chart.Estimates.ToDictionary(
                        e => e.Key,
                        e => new EstimateCacheEntry { Bytes = e.Value.OutputBytes, Extension = e.Value.OutputExtension, Optimal = e.Value.AlreadyOptimal },
                        StringComparer.Ordinal),
                };
            }

            _dirty = false;
        }

        return _file.Replace(data);
    }

    /// <summary>The settings of <paramref name="strategy"/> that shape what its optimizer writes, as a cache key. Whether it's enabled isn't one.</summary>
    public static string SettingsKey(OptimizeStrategy strategy, OptimizeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return strategy switch
        {
            OptimizeStrategy.Audio => Invariant($"{options.Audio.BitrateKbps}"),
            OptimizeStrategy.StemMixdown => Invariant($"{options.StemMixdown.BitrateKbps}"),
            OptimizeStrategy.Video => Invariant($"{VideoMethod}/{options.Video.Target}/{options.Video.Cq}/{options.Video.Speed}"),
            OptimizeStrategy.AlbumArt => ImageKey(options.AlbumArt),
            OptimizeStrategy.BackgroundArt => ImageKey(options.BackgroundArt),
            _ => "",
        };
    }

    private static string ImageKey(ImageOptimizeOptions image) => Invariant($"{image.Quality}/{image.MaxWidth}x{image.MaxHeight}");

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static string StampKey(EntryStamp stamp) =>
        Invariant($"{stamp.TotalBytes}/{stamp.LastWriteTimeUtc.Ticks}/{stamp.FileCount}/{stamp.ListingDigest}");

    private static string UnitKey(OptimizeStrategy strategy, IReadOnlyList<ChartAsset> sources, string settings) =>
        Invariant($"{strategy}|{settings}|{string.Join('|', sources.Select(s => Invariant($"{s.StoredName.ToLowerInvariant()}:{s.Length}")))}");
}

/// <summary>The estimate cache as its file has it.</summary>
internal sealed class EstimateCacheData
{
    public Dictionary<string, EstimateCacheChart> Charts { get; set; } = [];
}

internal sealed class EstimateCacheChart
{
    public string? Stamp { get; set; }

    public Dictionary<string, EstimateCacheEntry>? Estimates { get; set; }
}

internal sealed class EstimateCacheEntry
{
    public long Bytes { get; set; }

    public string? Extension { get; set; }

    public bool Optimal { get; set; }
}

[JsonSerializable(typeof(EstimateCacheData))]
internal sealed partial class EstimateCacheJsonContext : JsonSerializerContext
{
}
