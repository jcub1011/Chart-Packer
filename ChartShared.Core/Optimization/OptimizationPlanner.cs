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
    public static Task<IReadOnlyList<ChartPlan>> PlanAllAsync(
        IReadOnlyList<ChartAssets> charts, OptimizeOptions options, OptimizerSet optimizers, IProgress<(int Done, int Total)>? progress = null, CancellationToken cancellationToken = default) =>
        PlanAllAsync(charts, options, optimizers, null, progress, cancellationToken);

    /// <summary>
    /// Plans every chart in <paramref name="charts"/> as the overload without <paramref name="services"/> does, sharing
    /// their estimate cache and video gate between the plans.
    /// </summary>
    /// <param name="services">The estimate cache and video gate the plans share; none when null.</param>
    /// <param name="progress">Reports charts planned and charts to plan, as each is planned.</param>
    /// <exception cref="IOException">A chart can't be read to clean its metadata; none of the plans is returned.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">The plan was cancelled.</exception>
    public static async Task<IReadOnlyList<ChartPlan>> PlanAllAsync(
        IReadOnlyList<ChartAssets> charts, OptimizeOptions options, OptimizerSet optimizers, PlanServices? services, IProgress<(int Done, int Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(charts);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(optimizers);

        var plans = new ChartPlan[charts.Count];
        var sync = new object();
        int done = 0;

        async ValueTask PlanOneAsync(int i, CancellationToken token)
        {
            plans[i] = await PlanAsync(charts[i], options, optimizers, services, token).ConfigureAwait(false);
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
    /// <param name="Settings">The settings that shape the estimate, as <see cref="EstimateCache.SettingsKey"/> gives them.</param>
    private sealed record Step(
        OptimizeStrategy Strategy, OptimizeActionKind Kind, bool Remove, bool Available, string Settings, Func<AssetFile, Task<OptimizeEstimate>> Estimate,
        Func<ChartAsset, bool>? Compatibility = null, bool ConvertForCompatibility = false);

    /// <summary>Plans <paramref name="chart"/>.</summary>
    /// <exception cref="IOException">The chart can't be read to clean its metadata.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">The plan was cancelled.</exception>
    public static Task<ChartPlan> PlanAsync(ChartAssets chart, OptimizeOptions options, OptimizerSet optimizers, CancellationToken cancellationToken = default) =>
        PlanAsync(chart, options, optimizers, null, cancellationToken);

    /// <summary>
    /// Plans <paramref name="chart"/>, taking each file's estimate from <paramref name="services"/>' cache where it has
    /// one, in which case the file is neither extracted nor measured, and keeping there the ones it makes.
    /// </summary>
    /// <exception cref="IOException">The chart can't be read to clean its metadata.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">The plan was cancelled.</exception>
    public static async Task<ChartPlan> PlanAsync(ChartAssets chart, OptimizeOptions options, OptimizerSet optimizers, PlanServices? services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(optimizers);
        services ??= PlanServices.None;

        using var stager = AssetStager.InTemp(chart.Entry);
        var actions = new List<PlannedAction>();
        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The names the plan's actions will write, so two files can't be planned onto one name, as album.png and
        // album.jpg would both be to album.webp.
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plan = new PlanState(chart, options.SavingsMargin, stager, actions, written, services.Cache, cancellationToken);

        if (options.StemMixdown.Enabled)
            await PlanMixdownAsync(plan, options, optimizers.Audio, consumed).ConfigureAwait(false);

        if (options.Audio.Enabled)
        {
            var step = new Step(OptimizeStrategy.Audio, OptimizeActionKind.Transcode, false, optimizers.Audio?.IsAvailable == true,
                EstimateCache.SettingsKey(OptimizeStrategy.Audio, options), file => optimizers.Audio!.EstimateAsync([file], options.Audio, cancellationToken));
            await PlanFilesAsync(plan, OnePerStem(chart.Assets.Where(a => a.Kind == AssetKind.Audio && !consumed.Contains(a.StoredName))), step).ConfigureAwait(false);
        }

        if (options.Video.Enabled)
        {
            var step = new Step(OptimizeStrategy.Video, OptimizeActionKind.Recompress, options.Video.Remove, optimizers.Video?.IsAvailable == true,
                EstimateCache.SettingsKey(OptimizeStrategy.Video, options), file => EstimateVideoAsync(optimizers.Video!, file, options.Video, services.VideoGate, cancellationToken),
                asset => VideoCodecs.IsUnsupported(asset.CodecKey), options.Video.ConvertUnsupported);
            await PlanFilesAsync(plan, chart.Assets.Where(a => a.Kind == AssetKind.Video), step).ConfigureAwait(false);
        }

        if (options.AlbumArt.Enabled)
        {
            var step = new Step(OptimizeStrategy.AlbumArt, OptimizeActionKind.Recompress, options.AlbumArt.Remove, optimizers.Image?.IsAvailable == true,
                EstimateCache.SettingsKey(OptimizeStrategy.AlbumArt, options), file => optimizers.Image!.EstimateAsync(file, options.AlbumArt, cancellationToken));
            await PlanFilesAsync(plan, chart.Assets.Where(a => a.Kind == AssetKind.AlbumArt), step).ConfigureAwait(false);
        }

        if (options.BackgroundArt.Enabled)
        {
            var step = new Step(OptimizeStrategy.BackgroundArt, OptimizeActionKind.Recompress, options.BackgroundArt.Remove, optimizers.Image?.IsAvailable == true,
                EstimateCache.SettingsKey(OptimizeStrategy.BackgroundArt, options), file => optimizers.Image!.EstimateAsync(file, options.BackgroundArt, cancellationToken));
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
        ChartAssets Chart, double Margin, AssetStager Stager, List<PlannedAction> Actions, HashSet<string> Written, EstimateCache? Cache, CancellationToken CancellationToken);

    /// <summary>
    /// The estimate for <paramref name="sources"/>: the cache's when it has one, when nothing is extracted or measured, and
    /// otherwise <paramref name="measure"/>'s of the staged files, which the cache then keeps. A failure isn't kept.
    /// </summary>
    private static async Task<OptimizeEstimate> EstimateAsync(
        PlanState plan, OptimizeStrategy strategy, IReadOnlyList<ChartAsset> sources, string settings, Func<IReadOnlyList<AssetFile>, Task<OptimizeEstimate>> measure)
    {
        if (plan.Cache?.TryGet(plan.Chart, strategy, sources, settings) is { } cached)
            return cached;

        var files = new List<AssetFile>(sources.Count);
        foreach (var source in sources)
            files.Add(await plan.Stager.GetAsync(source, plan.CancellationToken).ConfigureAwait(false));

        var estimate = await measure(files).ConfigureAwait(false);
        plan.Cache?.Set(plan.Chart, strategy, sources, settings, estimate);
        return estimate;
    }

    // Video estimates are the ones that use every core they're given, so charts can be planned many at a time while only
    // a few of their videos are measured at once.
    private static async Task<OptimizeEstimate> EstimateVideoAsync(IVideoOptimizer video, AssetFile file, VideoOptimizeOptions options, SemaphoreSlim? gate, CancellationToken cancellationToken)
    {
        if (gate is null)
            return await video.EstimateAsync(file, options, cancellationToken).ConfigureAwait(false);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await video.EstimateAsync(file, options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

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
        // answer would change nothing. When it's on, it's converted
        // whatever it saves. It's judged by the codec the inspector probed: a video it couldn't probe has none, and goes by the
        // ordinary rules (the optimizer's own probe reads the same files, so one the inspector can't read fails its estimate).
        bool compatibility = step.Compatibility?.Invoke(asset) == true;
        if (compatibility && !step.ConvertForCompatibility)
            return Skipped(step.Strategy, names, null, asset, asset.Length, OptimizeSkipReason.UnsupportedCodec);

        OptimizeEstimate estimate;
        try
        {
            estimate = await EstimateAsync(plan, step.Strategy, [asset], step.Settings, files => step.Estimate(files[0])).ConfigureAwait(false);
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
        string codec = MixdownCodec(sources);

        PlannedAction action;
        if (audio is not { IsAvailable: true })
        {
            action = new PlannedAction(OptimizeStrategy.StemMixdown, OptimizeActionKind.Skip, names, null, codec, input, input, OptimizeSkipReason.OptimizerUnavailable);
        }
        else
        {
            try
            {
                var estimate = await EstimateAsync(plan, OptimizeStrategy.StemMixdown, sources, EstimateCache.SettingsKey(OptimizeStrategy.StemMixdown, options),
                    files => audio.EstimateAsync(files, options.StemMixdown, plan.CancellationToken)).ConfigureAwait(false);
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

    /// <summary>The codec a mixdown of <paramref name="sources"/> is grouped under: theirs when they share one, <c>audio/mixed</c> otherwise.</summary>
    internal static string MixdownCodec(IReadOnlyList<ChartAsset> sources) =>
        sources.Select(s => s.CodecKey).Distinct().Count() == 1 ? sources[0].CodecKey : "audio/mixed";

    /// <summary>
    /// What the enabled strategies of <paramref name="options"/> could each do to <paramref name="chart"/>, without reading
    /// it: one unit per file, or per mixdown, in the order the planner takes them. Metadata cleanup has none, as its size
    /// isn't known without reading the chart.
    /// </summary>
    /// <remarks>
    /// With stem mixdown on, the stems it would mix are listed for audio too when that's on: the planner transcodes them
    /// when the mixdown is skipped. <see cref="SavingsModel"/> counts a stem the mixdown took as an audio unit that saved
    /// nothing, so the two don't add up to more than the planner does.
    /// </remarks>
    public static IReadOnlyList<PlanUnit> UnitsOf(ChartAssets chart, OptimizeOptions options)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(options);

        var units = new List<PlanUnit>();
        if (options.StemMixdown.Enabled && MixdownOf(chart) is { } mixdown)
            units.Add(new PlanUnit(OptimizeStrategy.StemMixdown, MixdownCodec(mixdown.Sources), mixdown.Sources, mixdown.All.Sum(a => a.Length)));

        if (options.Audio.Enabled)
        {
            foreach (var asset in OnePerStem(chart.Assets.Where(a => a.Kind == AssetKind.Audio)))
                units.Add(new PlanUnit(OptimizeStrategy.Audio, asset.CodecKey, [asset], asset.Length, IsOpus(asset) ? 0 : null));
        }

        if (options.Video.Enabled)
        {
            foreach (var asset in chart.Assets.Where(a => a.Kind == AssetKind.Video))
            {
                long? known = options.Video.Remove ? asset.Length
                    : VideoCodecs.IsUnsupported(asset.CodecKey) && !options.Video.ConvertUnsupported ? 0
                    : null;
                units.Add(new PlanUnit(OptimizeStrategy.Video, asset.CodecKey, [asset], asset.Length, known));
            }
        }

        if (options.AlbumArt.Enabled)
        {
            foreach (var asset in chart.Assets.Where(a => a.Kind == AssetKind.AlbumArt))
                units.Add(new PlanUnit(OptimizeStrategy.AlbumArt, asset.CodecKey, [asset], asset.Length, options.AlbumArt.Remove ? asset.Length : null));
        }

        if (options.BackgroundArt.Enabled)
        {
            foreach (var asset in chart.Assets.Where(a => a.Kind == AssetKind.BackgroundImage))
                units.Add(new PlanUnit(OptimizeStrategy.BackgroundArt, asset.CodecKey, [asset], asset.Length, options.BackgroundArt.Remove ? asset.Length : null));
        }

        return units;
    }

    // Opus is what audio is transcoded to, so a file that's Opus already is left alone, whatever its bitrate.
    private static bool IsOpus(ChartAsset asset) =>
        asset.Extension.Equals(".opus", StringComparison.OrdinalIgnoreCase) || asset.CodecKey.Equals("audio/opus", StringComparison.OrdinalIgnoreCase);

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

/// <summary>What planning shares between charts.</summary>
/// <param name="Cache">Where estimates are looked up before a file is measured, and kept after; none when null.</param>
/// <param name="VideoGate">Held while a video is measured, so only as many as it admits are at once; none when null.</param>
public sealed record PlanServices(EstimateCache? Cache = null, SemaphoreSlim? VideoGate = null)
{
    /// <summary>No cache and no gate.</summary>
    public static PlanServices None { get; } = new();
}

/// <summary>One thing a strategy could do to a chart: a file it would shrink or remove, or the stems it would mix.</summary>
/// <param name="Strategy">The strategy.</param>
/// <param name="CodecKey">The input's codec, as the planner's action for it has.</param>
/// <param name="Sources">The files that are read: one, or a mixdown's one per stem.</param>
/// <param name="InputBytes">The size of what it would replace: for a mixdown, every format of the stems.</param>
/// <param name="KnownSavingBytes">
/// What it saves when that's known without measuring: a removal saves the file, and a file that's left alone whatever
/// its estimate (already Opus, an unsupported video codec that isn't converted) saves nothing. Null when it must be measured.
/// </param>
public sealed record PlanUnit(OptimizeStrategy Strategy, string CodecKey, IReadOnlyList<ChartAsset> Sources, long InputBytes, long? KnownSavingBytes = null)
{
    /// <summary>The kind of media it is: units of one type are expected to shrink alike, so they're sampled and predicted together.</summary>
    public MediaType Type => new(Strategy, CodecKey);

    /// <summary>Whether it has to be measured to know what it saves.</summary>
    public bool NeedsMeasuring => KnownSavingBytes is null;

    /// <summary>Whether <paramref name="action"/> is the planner's decision about this unit.</summary>
    public bool Matches(PlannedAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action.Strategy != Strategy)
            return false;

        // A chart has at most one mixdown; any other action is about the one file it reads.
        return Strategy == OptimizeStrategy.StemMixdown
            || (action.SourceNames.Count > 0 && action.SourceNames[0].Equals(Sources[0].StoredName, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>A kind of media as estimates group it: a strategy and the codec it's applied to, as audio in <c>audio/mp3</c>.</summary>
/// <param name="Strategy">The strategy.</param>
/// <param name="CodecKey">The input's codec.</param>
public readonly record struct MediaType(OptimizeStrategy Strategy, string CodecKey) : IComparable<MediaType>
{
    /// <inheritdoc/>
    public int CompareTo(MediaType other) =>
        Strategy != other.Strategy ? Strategy.CompareTo(other.Strategy) : string.CompareOrdinal(CodecKey, other.CodecKey);

    /// <inheritdoc/>
    public override string ToString() => $"{Strategy}:{CodecKey}";
}
