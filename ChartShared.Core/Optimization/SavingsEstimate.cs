using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ChartShared.Core.Scanning;

namespace ChartShared.Core.Optimization;

/// <summary>Chooses the charts a library's savings are first measured from.</summary>
public static class SampleSelector
{
    /// <summary>How many files of each <see cref="MediaType"/> the sample measures, where the library has that many.</summary>
    public const int DefaultQuotaPerType = 8;

    /// <summary>
    /// The charts to measure first so that every media type the enabled strategies of <paramref name="options"/> would
    /// touch has <paramref name="quotaPerType"/> files measured, or all of its files when the library has fewer. They're
    /// in the order to measure them: one chart for each type, the rarest type first, then a second for each, and so on, so
    /// that every type has something measured, and every chart can be predicted, as early as can be.
    /// </summary>
    /// <remarks>
    /// <para>Files are grouped by type before any chart is chosen, so a type that few charts hold (a video codec, PNG
    /// backgrounds) is sampled as well as one that every chart holds. A chart is measured whole, so one chosen for one type
    /// counts towards the quota of every other type it holds, which keeps the sample small.</para>
    /// <para>Within a type, the files chosen are those whose chart's relative path and stored name hash lowest (SHA-256,
    /// not <see cref="string.GetHashCode()"/>, which changes between runs), so the same library gives the same sample
    /// however it's ordered and wherever it's kept.</para>
    /// <para>Units whose saving is known without measuring (<see cref="PlanUnit.NeedsMeasuring"/>) aren't sampled.</para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="quotaPerType"/> is below 1.</exception>
    public static IReadOnlyList<ChartAssets> Cover(IReadOnlyList<ChartAssets> library, OptimizeOptions options, int quotaPerType = DefaultQuotaPerType)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(quotaPerType, 1);

        var charts = new List<Candidate>(library.Count);
        var byType = new Dictionary<MediaType, List<(ulong Key, Candidate Chart)>>();
        foreach (var chart in library)
        {
            var types = OptimizationPlanner.UnitsOf(chart, options).Where(u => u.NeedsMeasuring).ToList();
            if (types.Count == 0)
                continue;

            var candidate = new Candidate(chart, NormalizedPath(chart.Entry), [.. types.Select(u => u.Type)]);
            charts.Add(candidate);
            foreach (var unit in types)
            {
                if (!byType.TryGetValue(unit.Type, out var units))
                    byType[unit.Type] = units = [];
                units.Add((KeyOf(candidate.Path, unit.Sources[0].StoredName), candidate));
            }
        }

        var measured = new Dictionary<MediaType, int>();
        var chosen = new List<List<ChartAssets>>();
        // The rarest types choose first: the charts they need are few, and often cover the common types on the way.
        foreach (var (type, units) in byType.OrderBy(t => t.Value.Count).ThenBy(t => t.Key))
        {
            var mine = new List<ChartAssets>();
            chosen.Add(mine);
            foreach (var (_, chart) in units.OrderBy(u => u.Key).ThenBy(u => u.Chart.Path, StringComparer.Ordinal))
            {
                if (measured.GetValueOrDefault(type) >= quotaPerType)
                    break;
                if (chart.Chosen)
                    continue;

                chart.Chosen = true;
                mine.Add(chart.Chart);
                foreach (var held in chart.Types)
                    measured[held] = measured.GetValueOrDefault(held) + 1;
            }
        }

        // Taken a round at a time, so no type waits for another's whole quota: a slow type (video) would otherwise hold
        // back the first figure for every chart that has a file of a type behind it.
        var sample = new List<ChartAssets>();
        for (int round = 0; chosen.Any(c => round < c.Count); round++)
        {
            foreach (var mine in chosen)
            {
                if (round < mine.Count)
                    sample.Add(mine[round]);
            }
        }

        return sample;
    }

    private sealed class Candidate(ChartAssets chart, string path, MediaType[] types)
    {
        public ChartAssets Chart { get; } = chart;

        public string Path { get; } = path;

        public MediaType[] Types { get; } = types;

        public bool Chosen { get; set; }
    }

    private static string NormalizedPath(SongEntry entry) => entry.RelativePath.Replace('\\', '/');

    private static ulong KeyOf(string path, string storedName) =>
        BinaryPrimitives.ReadUInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(path + "\n" + storedName.ToLowerInvariant())));
}

/// <summary>What a chart is expected to save, in all and by strategy.</summary>
public sealed class ChartSavings
{
    private readonly long[] _byStrategy = new long[Enum.GetValues<OptimizeStrategy>().Length];

    /// <summary>What's saved across the strategies; negative when the chart would get larger.</summary>
    public long TotalBytes { get; private set; }

    /// <summary>
    /// Whether every part of it is known: measured, or of a type that's been measured elsewhere. False when the chart has a
    /// file of a type nothing is known about yet, which then counts for nothing in <see cref="TotalBytes"/>.
    /// </summary>
    public bool IsComplete { get; internal set; } = true;

    /// <summary>What <paramref name="strategy"/> saves.</summary>
    public long this[OptimizeStrategy strategy] => _byStrategy[(int)strategy];

    internal void Add(OptimizeStrategy strategy, long bytes)
    {
        _byStrategy[(int)strategy] += bytes;
        TotalBytes += bytes;
    }
}

/// <summary>
/// What's been learned from the charts measured so far about how each <see cref="MediaType"/> shrinks, for predicting the
/// charts that haven't been. Safe to use from several threads.
/// </summary>
/// <remarks>
/// A type's ratio is what its measured files are planned to save over their size, where a file the planner skips (below
/// the margin, would grow, its name taken, already optimal) saves nothing. So a prediction has the planner's rules in it
/// in the proportion they applied to the sample, and a chart's predicted figures add up as its measured ones will.
/// </remarks>
public sealed class SavingsModel
{
    private readonly object _sync = new();
    private readonly Dictionary<MediaType, (int Files, long InputBytes, long SavingBytes)> _types = [];
    private readonly OptimizeOptions _options;
    private int _version;

    /// <param name="options">The options the plans it learns from were made with.</param>
    public SavingsModel(OptimizeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>Rises whenever something's learned, so a prediction made before can be seen to be out of date.</summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>Learns from <paramref name="plan"/>, a chart planned with the model's options. A chart is learned from once.</summary>
    public void Add(ChartPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var units = OptimizationPlanner.UnitsOf(plan.Chart, _options);
        lock (_sync)
        {
            foreach (var unit in units.Where(u => u.NeedsMeasuring))
            {
                // A unit with no action is a stem the mixdown took: as audio, it saved nothing.
                var action = plan.Actions.FirstOrDefault(unit.Matches);
                // One that couldn't be measured says nothing about its type.
                if (action is { Reason: OptimizeSkipReason.EstimateFailed or OptimizeSkipReason.OptimizerUnavailable })
                    continue;

                _types.TryGetValue(unit.Type, out var t);
                _types[unit.Type] = (t.Files + 1, t.InputBytes + unit.InputBytes, t.SavingBytes + (action is { IsSkip: false } ? action.EstimatedSavingBytes : 0));
            }

            _version++;
        }
    }

    /// <summary>How many files of <paramref name="type"/> have been measured.</summary>
    public int MeasuredFiles(MediaType type)
    {
        lock (_sync)
            return _types.TryGetValue(type, out var t) ? t.Files : 0;
    }

    /// <summary>The share of its size a file of <paramref name="type"/> is expected to save; null when none has been measured.</summary>
    public double? RatioOf(MediaType type)
    {
        lock (_sync)
            return _types.TryGetValue(type, out var t) && t.InputBytes > 0 ? (double)t.SavingBytes / t.InputBytes : null;
    }

    /// <summary>What a chart with <paramref name="units"/> (<see cref="OptimizationPlanner.UnitsOf"/>) is expected to save, from what's been measured of their types.</summary>
    public ChartSavings Predict(IReadOnlyList<PlanUnit> units)
    {
        ArgumentNullException.ThrowIfNull(units);
        var savings = new ChartSavings();
        lock (_sync)
        {
            foreach (var unit in units)
            {
                if (unit.KnownSavingBytes is { } known)
                    savings.Add(unit.Strategy, known);
                else if (_types.TryGetValue(unit.Type, out var t) && t.InputBytes > 0)
                    savings.Add(unit.Strategy, (long)Math.Round(unit.InputBytes * ((double)t.SavingBytes / t.InputBytes)));
                else
                    savings.IsComplete = false;
            }
        }

        return savings;
    }

    /// <summary>What <paramref name="plan"/> saves: the planner's own figures, with what it skips saving nothing.</summary>
    public static ChartSavings Measured(ChartPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var savings = new ChartSavings();
        foreach (var action in plan.Work)
            savings.Add(action.Strategy, action.EstimatedSavingBytes);
        return savings;
    }
}
