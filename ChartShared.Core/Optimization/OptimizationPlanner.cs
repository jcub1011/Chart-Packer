using ChartShared.Core.Scanning;

namespace ChartShared.Core.Optimization;

/// <summary>
/// Decides what optimizing a chart does: for each enabled strategy and each file it applies to, a transcode,
/// recompression, removal, mixdown or metadata cleanup, or a skip with its reason.
/// </summary>
/// <remarks>
/// <para>A file is skipped when the optimizer says it's already optimal, when the output would be as large as the
/// input, or when the saving is under <see cref="OptimizeOptions.SavingsMargin"/>, in that order. What's already
/// optimal is the optimizer's to say, as it owns the codec: already Opus, or already VP8 WebM within the target.</para>
/// <para>A video in a codec the games can't all play (<see cref="VideoCodecs.IsUnsupported"/>) is only converted when
/// <see cref="VideoOptimizeOptions.ConvertUnsupported"/> is on, and then whatever the conversion saves: neither the
/// margin nor the larger-file rule applies, as the point is that the games can play it.</para>
/// <para>Nothing is written to the chart. <c>highway.*</c> images, charts, and unregistered files are never touched.
/// A song with several formats of one stem has only the first, in <see cref="AudioStems.Extensions"/> order, planned.</para>
/// </remarks>
public static class OptimizationPlanner
{
    private const string MixedStem = "song";

    /// <summary>
    /// Plans every chart in <paramref name="charts"/>, <see cref="OptimizeOptions.Parallelism"/> at a time, which is where
    /// video estimates take their time. The plans are in the order of the charts.
    /// </summary>
    /// <param name="progress">Reports charts planned and charts to plan, as each is planned.</param>
    /// <exception cref="IOException">A chart can't be read to clean its metadata; none of the plans is returned.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">The plan was cancelled.</exception>
    public static async Task<IReadOnlyList<ChartPlan>> PlanAllAsync(
        IReadOnlyList<ChartAssets> charts, OptimizeOptions options, OptimizerSet optimizers, IProgress<(int Done, int Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(charts);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(optimizers);

        var plans = new ChartPlan[charts.Count];
        var sync = new object();
        int done = 0;

        async ValueTask PlanOneAsync(int i, CancellationToken token)
        {
            plans[i] = await PlanAsync(charts[i], options, optimizers, token).ConfigureAwait(false);
            if (progress is null)
                return;
            lock (sync)
                progress.Report((++done, charts.Count));
        }

        int parallelism = Math.Max(1, options.Parallelism);
        if (parallelism == 1)
        {
            for (int i = 0; i < charts.Count; i++)
                await PlanOneAsync(i, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, charts.Count),
                new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
                (i, token) => PlanOneAsync(i, token)).ConfigureAwait(false);
        }

        return plans;
    }

    /// <summary>One strategy's way of estimating a file, for <see cref="PlanFileAsync"/>.</summary>
    /// <param name="Compatibility">
    /// Says whether a file is converted for compatibility rather than size, as a video in an unsupported codec is; null when
    /// none is. Whether that's done is <paramref name="ConvertForCompatibility"/>.
    /// </param>
    private sealed record Step(
        OptimizeStrategy Strategy, OptimizeActionKind Kind, bool Remove, bool Available, Func<AssetFile, Task<OptimizeEstimate>> Estimate,
        Func<ChartAsset, bool>? Compatibility = null, bool ConvertForCompatibility = false);

    /// <summary>Plans <paramref name="chart"/>.</summary>
    /// <exception cref="IOException">The chart can't be read to clean its metadata.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">The plan was cancelled.</exception>
    public static async Task<ChartPlan> PlanAsync(ChartAssets chart, OptimizeOptions options, OptimizerSet optimizers, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(optimizers);

        using var stager = AssetStager.InTemp(chart.Entry);
        var actions = new List<PlannedAction>();
        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The names the plan's actions will write, so two files can't be planned onto one name, as album.png and
        // album.jpg would both be to album.webp.
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plan = new PlanState(chart, options.SavingsMargin, stager, actions, written, cancellationToken);

        if (options.StemMixdown.Enabled)
            await PlanMixdownAsync(plan, options, optimizers.Audio, consumed).ConfigureAwait(false);

        if (options.Audio.Enabled)
        {
            var step = new Step(OptimizeStrategy.Audio, OptimizeActionKind.Transcode, false, optimizers.Audio?.IsAvailable == true,
                file => optimizers.Audio!.EstimateAsync([file], options.Audio, cancellationToken));
            await PlanFilesAsync(plan, OnePerStem(chart.Assets.Where(a => a.Kind == AssetKind.Audio && !consumed.Contains(a.StoredName))), step).ConfigureAwait(false);
        }

        if (options.Video.Enabled)
        {
            var step = new Step(OptimizeStrategy.Video, OptimizeActionKind.Recompress, options.Video.Remove, optimizers.Video?.IsAvailable == true,
                file => optimizers.Video!.EstimateAsync(file, options.Video, cancellationToken),
                asset => VideoCodecs.IsUnsupported(asset.CodecKey), options.Video.ConvertUnsupported);
            await PlanFilesAsync(plan, chart.Assets.Where(a => a.Kind == AssetKind.Video), step).ConfigureAwait(false);
        }

        if (options.AlbumArt.Enabled)
        {
            var step = new Step(OptimizeStrategy.AlbumArt, OptimizeActionKind.Recompress, options.AlbumArt.Remove, optimizers.Image?.IsAvailable == true,
                file => optimizers.Image!.EstimateAsync(file, options.AlbumArt, cancellationToken));
            await PlanFilesAsync(plan, chart.Assets.Where(a => a.Kind == AssetKind.AlbumArt), step).ConfigureAwait(false);
        }

        if (options.BackgroundArt.Enabled)
        {
            var step = new Step(OptimizeStrategy.BackgroundArt, OptimizeActionKind.Recompress, options.BackgroundArt.Remove, optimizers.Image?.IsAvailable == true,
                file => optimizers.Image!.EstimateAsync(file, options.BackgroundArt, cancellationToken));
            await PlanFilesAsync(plan, chart.Assets.Where(a => a.Kind == AssetKind.BackgroundImage), step).ConfigureAwait(false);
        }

        MetadataCleanupPlan? metadata = null;
        if (options.Metadata.Enabled)
        {
            var cleanup = await MetadataCleanup.AnalyzeAsync(chart.Entry, options.Metadata, cancellationToken).ConfigureAwait(false);
            if (cleanup.HasChanges)
            {
                metadata = cleanup;
                actions.Add(new PlannedAction(OptimizeStrategy.Metadata, OptimizeActionKind.CleanMetadata,
                    cleanup.MidiName is null ? [] : [cleanup.MidiName], null, null, cleanup.BytesBefore, cleanup.BytesAfter));
            }
            else
            {
                actions.Add(new PlannedAction(OptimizeStrategy.Metadata, OptimizeActionKind.Skip, [], null, null, 0, 0, OptimizeSkipReason.NothingToClean));
            }
        }

        return new ChartPlan(chart, actions, metadata);
    }

    /// <summary>What planning one chart carries from file to file.</summary>
    /// <param name="Written">The names the actions planned so far will write.</param>
    private sealed record PlanState(
        ChartAssets Chart, double Margin, AssetStager Stager, List<PlannedAction> Actions, HashSet<string> Written, CancellationToken CancellationToken);

    /// <summary>
    /// A planning failure that skips a file rather than the chart: anything but a cancel, as an optimizer can fail in more
    /// ways than the I/O and decoding ones its interface documents (a native binding, say), as <see cref="OptimizeJob"/> allows.
    /// </summary>
    internal static bool IsEstimateFailure(Exception ex) => ex is not OperationCanceledException;

    /// <summary>
    /// The stems a mixdown would mix: <c>All</c> is every file of them, in every format, as all of those go; <c>Sources</c>
    /// is the one file per stem that's decoded. <see langword="null"/> when there are fewer than two stems to mix.
    /// </summary>
    internal static (IReadOnlyList<ChartAsset> All, IReadOnlyList<ChartAsset> Sources)? MixdownOf(ChartAssets chart)
    {
        var mixable = chart.Assets.Where(a => a.Kind == AssetKind.Audio && a.Stem is not (AudioStems.Preview or AudioStems.Crowd)).ToList();
        var sources = OnePerStem(mixable);
        if (sources.Count < 2)
            return null;

        return ([.. mixable.Where(a => sources.Any(s => s.Stem == a.Stem))], sources);
    }

    private static async Task PlanFilesAsync(PlanState plan, IEnumerable<ChartAsset> assets, Step step)
    {
        foreach (var asset in assets)
            plan.Actions.Add(await PlanFileAsync(plan, asset, step).ConfigureAwait(false));
    }

    /// <summary>Plans one file: a removal, or the estimate's verdict.</summary>
    private static async Task<PlannedAction> PlanFileAsync(PlanState plan, ChartAsset asset, Step step)
    {
        string[] names = [asset.StoredName];
        if (step.Remove)
            return new PlannedAction(step.Strategy, OptimizeActionKind.Remove, names, null, asset.CodecKey, asset.Length, 0);
        if (!step.Available)
            return Skipped(step.Strategy, names, null, asset, asset.Length, OptimizeSkipReason.OptimizerUnavailable);

        // A file that's only converted so the games can play it: when that's off it's left alone, and not estimated, as the
        // answer would only be shown (SavingsEstimator shows what converting costs, by codec). When it's on, it's converted
        // whatever it saves. It's judged by the codec the inspector probed: a video it couldn't probe has none, and goes by the
        // ordinary rules (the optimizer's own probe reads the same files, so one the inspector can't read fails its estimate).
        bool compatibility = step.Compatibility?.Invoke(asset) == true;
        if (compatibility && !step.ConvertForCompatibility)
            return Skipped(step.Strategy, names, null, asset, asset.Length, OptimizeSkipReason.UnsupportedCodec);

        OptimizeEstimate estimate;
        try
        {
            estimate = await step.Estimate(await plan.Stager.GetAsync(asset, plan.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsEstimateFailure(ex))
        {
            return Skipped(step.Strategy, names, null, asset, asset.Length, OptimizeSkipReason.EstimateFailed);
        }

        string output = Path.GetFileNameWithoutExtension(asset.StoredName) + estimate.OutputExtension;
        // The savings margin and the larger-file rule don't apply to a conversion for compatibility; what's already optimal does.
        var reason = compatibility ? (estimate.AlreadyOptimal ? OptimizeSkipReason.AlreadyOptimal : OptimizeSkipReason.None) : Judge(asset.Length, estimate, plan.Margin);
        if (reason == OptimizeSkipReason.None && NameTaken(plan, output, names))
            reason = OptimizeSkipReason.OutputNameTaken;

        if (reason != OptimizeSkipReason.None)
            return Skipped(step.Strategy, names, output, asset, estimate.OutputBytes, reason);

        plan.Written.Add(output);
        return new PlannedAction(step.Strategy, compatibility ? OptimizeActionKind.Transcode : step.Kind, names, output, asset.CodecKey, asset.Length, estimate.OutputBytes,
            ForCompatibility: compatibility);
    }

    private static async Task PlanMixdownAsync(PlanState plan, OptimizeOptions options, IAudioOptimizer? audio, HashSet<string> consumed)
    {
        if (MixdownOf(plan.Chart) is not { } mixdown)
            return;

        var (all, sources) = mixdown;
        // Every format of a mixed stem goes, not just the one that was mixed.
        string[] names = [.. all.Select(a => a.StoredName)];
        long input = all.Sum(a => a.Length);
        string codec = sources.Select(s => s.CodecKey).Distinct().Count() == 1 ? sources[0].CodecKey : "audio/mixed";

        PlannedAction action;
        if (audio is not { IsAvailable: true })
        {
            action = new PlannedAction(OptimizeStrategy.StemMixdown, OptimizeActionKind.Skip, names, null, codec, input, input, OptimizeSkipReason.OptimizerUnavailable);
        }
        else
        {
            try
            {
                var files = new List<AssetFile>();
                foreach (var source in sources)
                    files.Add(await plan.Stager.GetAsync(source, plan.CancellationToken).ConfigureAwait(false));

                var estimate = await audio.EstimateAsync(files, options.StemMixdown, plan.CancellationToken).ConfigureAwait(false);
                string output = MixedStem + estimate.OutputExtension;
                var reason = Judge(input, estimate, plan.Margin);
                if (reason == OptimizeSkipReason.None && NameTaken(plan, output, names))
                    reason = OptimizeSkipReason.OutputNameTaken;

                if (reason == OptimizeSkipReason.None)
                    plan.Written.Add(output);
                action = new PlannedAction(OptimizeStrategy.StemMixdown,
                    reason == OptimizeSkipReason.None ? OptimizeActionKind.MixDown : OptimizeActionKind.Skip,
                    names, output, codec, input, estimate.OutputBytes, reason);
            }
            catch (Exception ex) when (IsEstimateFailure(ex))
            {
                action = new PlannedAction(OptimizeStrategy.StemMixdown, OptimizeActionKind.Skip, names, null, codec, input, input, OptimizeSkipReason.EstimateFailed);
            }
        }

        plan.Actions.Add(action);
        if (!action.IsSkip)
            consumed.UnionWith(names);
    }

    private static PlannedAction Skipped(OptimizeStrategy strategy, string[] names, string? output, ChartAsset asset, long estimatedOutput, OptimizeSkipReason reason) =>
        new(strategy, OptimizeActionKind.Skip, names, output, asset.CodecKey, asset.Length, estimatedOutput, reason);

    // The output is taken when another file, one this action doesn't replace, has its name, or an earlier action writes it.
    private static bool NameTaken(PlanState plan, string output, IReadOnlyCollection<string> replaced) =>
        plan.Written.Contains(output)
        || plan.Chart.Assets.Any(a => a.StoredName.Equals(output, StringComparison.OrdinalIgnoreCase)
            && !replaced.Contains(a.StoredName, StringComparer.OrdinalIgnoreCase));

    internal static IReadOnlyList<ChartAsset> OnePerStem(IEnumerable<ChartAsset> audio) =>
    [
        .. audio.GroupBy(a => a.Stem!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(a => ExtensionOrder(a.Extension)).First())
            .OrderBy(a => AudioStems.OrderOf(a.Stem!)),
    ];

    private static int ExtensionOrder(string extension)
    {
        for (int i = 0; i < AudioStems.Extensions.Count; i++)
        {
            if (AudioStems.Extensions[i].Equals(extension, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return int.MaxValue;
    }

    /// <summary>Why optimizing a file of <paramref name="input"/> bytes to <paramref name="estimate"/> should be skipped, or <see cref="OptimizeSkipReason.None"/>.</summary>
    internal static OptimizeSkipReason Judge(long input, OptimizeEstimate estimate, double margin)
    {
        if (estimate.AlreadyOptimal)
            return OptimizeSkipReason.AlreadyOptimal;
        if (estimate.OutputBytes >= input)
            return OptimizeSkipReason.WouldGrow;
        if ((double)(input - estimate.OutputBytes) / input < margin)
            return OptimizeSkipReason.BelowMargin;
        return OptimizeSkipReason.None;
    }
}
