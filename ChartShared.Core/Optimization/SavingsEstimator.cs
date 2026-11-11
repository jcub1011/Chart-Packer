using ChartShared.Core.Scanning;

namespace ChartShared.Core.Optimization;

/// <summary>
/// Estimates what a strategy would save across a library, by asking its optimizer about the files of a sample of
/// charts and grouping the answers by codec: a codec that gets larger when "compressed" shows as a negative saving. The
/// sampled charts are estimated <see cref="OptimizeOptions.Parallelism"/> at a time; the answer is the same either way.
/// </summary>
public static class SavingsEstimator
{
    /// <summary>What a strategy does to one file, or to the several stems of a mixdown.</summary>
    private sealed record Unit(IReadOnlyList<ChartAsset> Sources, long InputBytes, string CodecKey);

    /// <summary>
    /// Estimates <paramref name="strategy"/> from the charts <paramref name="mode"/> picks out of <paramref name="library"/>.
    /// The strategy's settings in <paramref name="options"/> are used whether or not it's enabled. A sample's files are
    /// extracted to a temp folder, never into the library, and the folder is deleted.
    /// </summary>
    /// <param name="selected">The chart for <see cref="SamplingMode.SingleChart"/>.</param>
    /// <param name="progress">Reports charts done and charts to do.</param>
    /// <exception cref="ArgumentException">
    /// The strategy is <see cref="OptimizeStrategy.Metadata"/>, which has no estimate; or an image strategy is asked for
    /// <see cref="SamplingMode.SingleChart"/>; or the single chart isn't in the library.
    /// </exception>
    /// <exception cref="InvalidOperationException">The strategy's optimizer is missing or not available.</exception>
    /// <exception cref="IOException">A chart can't be read.</exception>
    /// <exception cref="OperationCanceledException">The estimate was cancelled.</exception>
    public static async Task<SavingsEstimate> EstimateAsync(
        OptimizeStrategy strategy,
        IReadOnlyList<ChartAssets> library,
        OptimizeOptions options,
        OptimizerSet optimizers,
        SamplingMode mode,
        SongEntry? selected = null,
        int sampleSize = SampleSelector.DefaultSampleSize,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(optimizers);

        if (strategy == OptimizeStrategy.Metadata)
            throw new ArgumentException("Metadata cleanup has no size estimate.", nameof(strategy));
        if (mode == SamplingMode.SingleChart && strategy is OptimizeStrategy.AlbumArt or OptimizeStrategy.BackgroundArt)
            throw new ArgumentException("Images are estimated from a sample or all charts, not a single chart.", nameof(mode));

        // A removal leaves nothing, so there's nothing to ask an optimizer, or to extract for it.
        bool remove = RemovesFiles(strategy, options);
        var estimate = remove ? null : EstimatorFor(strategy, options, optimizers);
        var sample = SampleSelector.Select(library, mode, selected, sampleSize);

        var totals = new SortedDictionary<string, (int Count, long Bytes)>(StringComparer.Ordinal);
        foreach (var chart in library)
        {
            foreach (var unit in UnitsOf(strategy, chart))
            {
                totals.TryGetValue(unit.CodecKey, out var t);
                totals[unit.CodecKey] = (t.Count + 1, t.Bytes + unit.InputBytes);
            }
        }

        var sampled = new Dictionary<string, (int Count, long Input, long Output)>(StringComparer.Ordinal);
        var sync = new object();
        int done = 0;

        async ValueTask EstimateChartAsync(ChartAssets chart, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using var stager = AssetStager.InTemp(chart.Entry);
            foreach (var unit in UnitsOf(strategy, chart))
            {
                var (ok, output) = estimate is null ? (true, 0L) : await TryEstimateAsync(unit, stager, estimate, token).ConfigureAwait(false);
                if (!ok)
                    continue;

                lock (sync)
                {
                    sampled.TryGetValue(unit.CodecKey, out var s);
                    sampled[unit.CodecKey] = (s.Count + 1, s.Input + unit.InputBytes, s.Output + output);
                }
            }

            lock (sync)
                progress?.Report((++done, sample.Count));
        }

        int parallelism = Math.Max(1, options.Parallelism);
        if (parallelism == 1)
        {
            foreach (var chart in sample)
                await EstimateChartAsync(chart, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await Parallel.ForEachAsync(sample, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken }, EstimateChartAsync).ConfigureAwait(false);
        }

        var codecs = totals.Select(t =>
        {
            sampled.TryGetValue(t.Key, out var s);
            return new CodecSavings(t.Key, s.Count, t.Value.Count, s.Input, s.Output, t.Value.Bytes);
        }).ToList();
        return new SavingsEstimate(strategy, mode, sample.Count, library.Count, codecs);
    }

    private static async Task<(bool Ok, long Output)> TryEstimateAsync(
        Unit unit, AssetStager stager, Func<IReadOnlyList<AssetFile>, CancellationToken, Task<OptimizeEstimate>> estimate, CancellationToken cancellationToken)
    {
        try
        {
            var files = new List<AssetFile>(unit.Sources.Count);
            foreach (var source in unit.Sources)
                files.Add(await stager.GetAsync(source, cancellationToken).ConfigureAwait(false));

            var result = await estimate(files, cancellationToken).ConfigureAwait(false);
            return (true, result.AlreadyOptimal ? unit.InputBytes : result.OutputBytes);
        }
        catch (Exception ex) when (OptimizationPlanner.IsEstimateFailure(ex))
        {
            // A file that can't be read, or that its optimizer fails on, isn't counted, so it doesn't skew its codec.
            return (false, 0);
        }
    }

    private static bool RemovesFiles(OptimizeStrategy strategy, OptimizeOptions options) => strategy switch
    {
        OptimizeStrategy.Video => options.Video.Remove,
        OptimizeStrategy.AlbumArt => options.AlbumArt.Remove,
        OptimizeStrategy.BackgroundArt => options.BackgroundArt.Remove,
        _ => false,
    };

    private static Func<IReadOnlyList<AssetFile>, CancellationToken, Task<OptimizeEstimate>> EstimatorFor(OptimizeStrategy strategy, OptimizeOptions options, OptimizerSet optimizers)
    {
        switch (strategy)
        {
            case OptimizeStrategy.Audio or OptimizeStrategy.StemMixdown:
                var audio = optimizers.Audio is { IsAvailable: true } a ? a : throw new InvalidOperationException("No audio optimizer is available.");
                var audioOptions = strategy == OptimizeStrategy.Audio ? options.Audio : options.StemMixdown;
                return (files, ct) => audio.EstimateAsync(files, audioOptions, ct);

            case OptimizeStrategy.Video:
                var video = optimizers.Video is { IsAvailable: true } v ? v : throw new InvalidOperationException("No video optimizer is available.");
                return (files, ct) => video.EstimateAsync(files[0], options.Video, ct);

            case OptimizeStrategy.AlbumArt or OptimizeStrategy.BackgroundArt:
                var image = optimizers.Image is { IsAvailable: true } i ? i : throw new InvalidOperationException("No image optimizer is available.");
                var imageOptions = strategy == OptimizeStrategy.AlbumArt ? options.AlbumArt : options.BackgroundArt;
                return (files, ct) => image.EstimateAsync(files[0], imageOptions, ct);

            default:
                throw new ArgumentOutOfRangeException(nameof(strategy), strategy, null);
        }
    }

    private static IEnumerable<Unit> UnitsOf(OptimizeStrategy strategy, ChartAssets chart)
    {
        switch (strategy)
        {
            case OptimizeStrategy.Audio:
                foreach (var asset in OptimizationPlanner.OnePerStem(chart.Assets.Where(a => a.Kind == AssetKind.Audio)))
                    yield return new Unit([asset], asset.Length, asset.CodecKey);
                break;

            case OptimizeStrategy.StemMixdown:
                if (OptimizationPlanner.MixdownOf(chart) is { } mixdown)
                    yield return new Unit(mixdown.Sources, mixdown.All.Sum(a => a.Length), "audio/mixdown");
                break;

            default:
                var kind = strategy switch
                {
                    OptimizeStrategy.Video => AssetKind.Video,
                    OptimizeStrategy.AlbumArt => AssetKind.AlbumArt,
                    _ => AssetKind.BackgroundImage,
                };
                foreach (var asset in chart.Assets.Where(a => a.Kind == kind))
                    yield return new Unit([asset], asset.Length, asset.CodecKey);
                break;
        }
    }
}
