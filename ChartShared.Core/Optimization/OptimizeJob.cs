using ChartShared.Core.Dedup;
using ChartShared.Core.Ini;
using ChartShared.Core.Metadata;
using ChartShared.Core.Packing;
using ChartShared.Core.Scanning;
using ChartShared.Core.Sng;
using ChartShared.Core.Validation;

namespace ChartShared.Core.Optimization;

/// <summary>
/// Applies a plan: works through the charts, <see cref="OptimizeOptions.Parallelism"/> at a time, optimizing each one's files
/// into a staging folder beside it and then swapping the result in, in place or as a copy in an output folder.
/// </summary>
/// <remarks>
/// <para>A <c>.sng</c> is written with <see cref="SngWriter.RewriteAsync"/> and a folder with
/// <see cref="SongFolderFiles.ApplyAsync"/>, which replace what they change whole, so a chart that fails is as it was.
/// Its <c>song.ini</c> is edited, formatting kept, by <see cref="SongIni.ApplyEditsAsync"/>, as a folder's files can't
/// include it. Each chart is held with <see cref="SongLockSet.TryHold"/> and <see cref="SongFileGate"/> for as long as it's written.</para>
/// <para>A chart that's held by another app, has unsaved changes, changed since it was planned, or has no work is
/// skipped. A chart that fails is reported and never stops the run; a file that fails is left as it was, and the chart's
/// others are still done. Only cancellation stops the run, by throwing after the charts in flight have cleaned up their
/// staging files. The results of the charts finished by then were reported through the progress.</para>
/// <para>With <see cref="OptimizeOptions.Parallelism"/> above 1, charts are worked on at once but their results stay in the
/// order of the plans. Progress reports are made one at a time, never overlapping, but those of different charts
/// interleave: each carries the chart it's about, and <see cref="OptimizeProgress.Done"/> is how many charts have
/// finished (so it rises by one with each completed report).</para>
/// </remarks>
public static class OptimizeJob
{
    private const string MetadataName = "metadata";

    /// <summary>Runs <paramref name="plans"/>, <see cref="OptimizeOptions.Parallelism"/> charts at a time.</summary>
    /// <param name="services">What the host app contributes; none by default.</param>
    /// <param name="progress">Reports as each chart starts, as each file starts, and as each chart finishes.</param>
    /// <exception cref="OperationCanceledException">The run was cancelled.</exception>
    public static async Task<OptimizeJobResult> RunAsync(
        IReadOnlyList<ChartPlan> plans,
        OptimizeOptions options,
        OptimizerSet optimizers,
        OptimizeJobServices? services = null,
        IProgress<OptimizeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(optimizers);

        services ??= new OptimizeJobServices();
        var reporter = new Reporter(progress);
        var context = new Context(options, optimizers, services.Locks ?? new SongLockSet(SongLockHolder.Unknown), services.Gate ?? new SongFileGate(), services, reporter);
        var results = new OptimizeItemResult[plans.Count];

        async ValueTask RunOneAsync(int i, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var plan = plans[i];
            reporter.Report(plans.Count, plan.Chart.Entry, null, null);

            OptimizeItemResult result;
            try
            {
                result = await RunChartAsync(plan, context, plans.Count, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One chart failing never stops the rest.
                result = new OptimizeItemResult(plan.Chart.Entry, OptimizeItemStatus.Failed, OptimizeItemSkipReason.None, SkippedAssets(plan), plan.Chart.TotalBytes, plan.Chart.TotalBytes, null, ex.Message);
            }

            results[i] = result;
            reporter.Report(plans.Count, plan.Chart.Entry, null, result);
        }

        int parallelism = Math.Max(1, options.Parallelism);
        if (parallelism == 1)
        {
            for (int i = 0; i < plans.Count; i++)
                await RunOneAsync(i, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, plans.Count),
                new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
                (i, token) => RunOneAsync(i, token)).ConfigureAwait(false);
        }

        return new OptimizeJobResult(results);
    }

    private sealed record Context(OptimizeOptions Options, OptimizerSet Optimizers, SongLockSet Locks, SongFileGate Gate, OptimizeJobServices Services, Reporter Progress);

    /// <summary>
    /// Keeps the count of charts finished, and passes progress reports to the host one at a time and in the order they were
    /// made, so a host's progress never has two calls in flight and a report's count never goes back. A report is queued under
    /// a lock; the thread that finds none being delivered delivers the queue, outside the lock, so a slow host blocks that
    /// thread alone, not the others making reports.
    /// </summary>
    private sealed class Reporter(IProgress<OptimizeProgress>? inner)
    {
        private readonly object _sync = new();
        private readonly Queue<OptimizeProgress> _pending = new();
        private int _finished;
        private bool _delivering;

        /// <summary>Reports, carrying the count of charts finished, which a <paramref name="completed"/> result raises by one.</summary>
        public void Report(int total, SongEntry? current, string? asset, OptimizeItemResult? completed, double? fileFraction = null)
        {
            lock (_sync)
            {
                int count = completed is null ? _finished : ++_finished;
                if (inner is null)
                    return;

                _pending.Enqueue(new OptimizeProgress(count, total, current, asset, completed, fileFraction));
                if (_delivering)
                    return;
                _delivering = true;
            }

            try
            {
                while (true)
                {
                    OptimizeProgress next;
                    lock (_sync)
                    {
                        if (!_pending.TryDequeue(out next!))
                        {
                            _delivering = false;
                            return;
                        }
                    }

                    inner.Report(next);
                }
            }
            catch
            {
                // A host that throws leaves what's queued for the next report to deliver.
                lock (_sync)
                    _delivering = false;
                throw;
            }
        }
    }

    private static async Task<OptimizeItemResult> RunChartAsync(ChartPlan plan, Context context, int total, CancellationToken cancellationToken)
    {
        var entry = plan.Chart.Entry;
        var skipped = SkippedAssets(plan);
        long size = plan.Chart.TotalBytes;
        OptimizeItemResult Skip(OptimizeItemSkipReason reason) => new(entry, OptimizeItemStatus.Skipped, reason, skipped, size, size);

        if (!plan.HasWork)
            return Skip(OptimizeItemSkipReason.NothingToDo);
        if (context.Services.HasDrafts?.Invoke(entry.FullPath) == true)
            return Skip(OptimizeItemSkipReason.HasUnsavedChanges);

        string? target = context.Options.OutputFolder is { } folder ? OutputTarget(entry, folder) : null;
        if (target is not null && (File.Exists(target) || Directory.Exists(target)))
            return Skip(OptimizeItemSkipReason.OutputExists);

        using var held = context.Locks.TryHold(entry.FullPath);
        if (held is null)
            return Skip(OptimizeItemSkipReason.InUseElsewhere);
        using var gate = await context.Gate.WriteAsync(target is null ? [entry.FullPath] : [entry.FullPath, target], cancellationToken).ConfigureAwait(false);

        // Once the chart is held, the app lets go of what it has open, such as the inspector's audio and video.
        if (context.Services.ReleaseFiles is { } release)
            await release(entry, cancellationToken).ConfigureAwait(false);

        if (plan.Chart.Stamp is { } expected && EntryStamps.Capture(entry.FullPath, entry.Kind, cancellationToken) != expected)
            return Skip(OptimizeItemSkipReason.ChartChanged);

        long before = await SizeOfAsync(entry.Kind, entry.FullPath, cancellationToken).ConfigureAwait(false);
        string staging = StagingPath.For(entry.FullPath);
        string? copy = target is null ? null : StagingPath.For(target);
        try
        {
            Directory.CreateDirectory(staging);
            var change = await StageAsync(plan, context, staging, total, cancellationToken).ConfigureAwait(false);
            if (!change.HasChanges)
            {
                var status = change.Assets.Any(a => a.Outcome == AssetOutcome.Failed) ? OptimizeItemStatus.Failed : OptimizeItemStatus.Skipped;
                return new OptimizeItemResult(entry, status, status == OptimizeItemStatus.Skipped ? OptimizeItemSkipReason.NothingToDo : OptimizeItemSkipReason.None,
                    change.Assets, before, before, null, change.Assets.FirstOrDefault(a => a.Outcome == AssetOutcome.Failed)?.Detail);
            }

            string written = entry.FullPath;
            if (target is null)
            {
                await ApplyAsync(entry.Kind, entry.FullPath, change, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await CopyChartAsync(entry, copy!, cancellationToken).ConfigureAwait(false);
                await ApplyAsync(entry.Kind, copy!, change, cancellationToken).ConfigureAwait(false);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (entry.Kind == SongEntryKind.SngFile)
                    await SongFileCommit.MoveOverAsync(copy!, target, cancellationToken).ConfigureAwait(false);
                else
                    Directory.Move(copy!, target);
                written = target;
            }

            long after = await SizeOfAsync(entry.Kind, written, cancellationToken).ConfigureAwait(false);
            return new OptimizeItemResult(entry, OptimizeItemStatus.Succeeded, OptimizeItemSkipReason.None, change.Assets, before, after, written);
        }
        catch
        {
            // A failed or cancelled rewrite can leave its own temp file beside the .sng.
            if (entry.Kind == SongEntryKind.SngFile)
            {
                StagingPath.TryDeleteFile(entry.FullPath + SngWriter.TempSuffix);
                if (copy is not null)
                    StagingPath.TryDeleteFile(copy + SngWriter.TempSuffix);
            }

            throw;
        }
        finally
        {
            StagingPath.TryDeleteDirectory(staging);
            if (copy is not null)
            {
                StagingPath.TryDeleteFile(copy);
                StagingPath.TryDeleteDirectory(copy);
            }
        }
    }

    /// <summary>What staging a chart's optimized files came to.</summary>
    private sealed class Change
    {
        public List<AssetResult> Assets { get; } = [];

        public HashSet<string> Removes { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<AddedFile> Adds { get; } = [];

        public MetadataDraft? Edits { get; set; }

        /// <summary>Images stored under another name now (<c>album.png</c> to <c>album.jpg</c>), old name to new.</summary>
        public Dictionary<string, string> Renames { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool HasChanges => Removes.Count > 0 || Adds.Count > 0 || Edits is { HasMetadataChanges: true };
    }

    /// <summary>Optimizes each of the plan's files into <paramref name="staging"/>; nothing in the chart is touched.</summary>
    private static async Task<Change> StageAsync(ChartPlan plan, Context context, string staging, int total, CancellationToken cancellationToken)
    {
        var entry = plan.Chart.Entry;
        var change = new Change();
        // A .sng's files are extracted beside the outputs, in a folder of their own, which goes once they've been read.
        using var stager = new AssetStager(entry, () => Path.Combine(staging, "src"));
        change.Assets.AddRange(SkippedAssets(plan));

        foreach (var action in plan.Actions.Where(a => !a.IsSkip))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = action.SourceNames.FirstOrDefault() ?? MetadataName;
            context.Progress.Report(total, entry, name, null);

            if (action.Kind == OptimizeActionKind.Remove)
            {
                change.Removes.UnionWith(action.SourceNames);
                change.Assets.Add(new AssetResult(name, action.Strategy, action.Kind, action.InputBytes, 0, AssetOutcome.Applied, EstimatedBytesAfter: 0));
            }
            else if (action.Kind == OptimizeActionKind.CleanMetadata)
            {
                await StageMetadataAsync(plan, action, staging, change, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var reporter = context.Progress;
                IProgress<double> fileProgress = new InlineProgress<double>(f => reporter.Report(total, entry, name, null, f));
                await StageOptimizedAsync(plan, action, context, stager, staging, change, fileProgress, cancellationToken).ConfigureAwait(false);
            }
        }

        if (change.Renames.Count > 0 && !await StageReferencesAsync(entry, change, cancellationToken).ConfigureAwait(false))
            RevertRenames(change);

        return change;
    }

    /// <summary>
    /// Points the metadata at the images that were renamed, so a <c>background = background.png</c> doesn't name a file
    /// that's gone. It's applied after any cleanup edits, whose keys it then doesn't revive.
    /// </summary>
    /// <returns>
    /// Whether the renames are safe to make: the metadata was read and updated, or there's none that could name an image.
    /// <see langword="false"/> when the metadata is there but can't be read, so a key may name a file that's about to go.
    /// </returns>
    private static async Task<bool> StageReferencesAsync(SongEntry entry, Change change, CancellationToken cancellationToken)
    {
        var pairs = await MetadataCleanup.ReadMetadataAsync(entry, cancellationToken).ConfigureAwait(false);
        if (pairs is null)
        {
            // A folder with no song.ini has no key to name an image, which is then found by its name alone. Any other
            // metadata that can't be read might name it.
            return entry.Kind == SongEntryKind.SongFolder && SongIni.FindIn(entry.FullPath) is null;
        }

        var edits = change.Edits ?? new MetadataDraft();
        if (ImageOptimization.UpdateReferences(edits.ApplyTo(pairs), change.Renames, edits))
            change.Edits = edits;
        return true;
    }

    /// <summary>
    /// Undoes the renamed images' changes, as the metadata can't be pointed at their new names: each is left as it was, and
    /// reported as failed.
    /// </summary>
    private static void RevertRenames(Change change)
    {
        foreach (var (oldName, newName) in change.Renames)
        {
            change.Removes.Remove(oldName);
            foreach (var added in change.Adds.Where(a => a.StoredName.Equals(newName, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                StagingPath.TryDeleteFile(added.SourcePath);
                change.Adds.Remove(added);
            }

            int index = change.Assets.FindIndex(a => a.Outcome == AssetOutcome.Applied && a.StoredName.Equals(oldName, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                var applied = change.Assets[index];
                change.Assets[index] = applied with
                {
                    BytesAfter = applied.BytesBefore,
                    Outcome = AssetOutcome.Failed,
                    Detail = "The chart's metadata can't be read, so it can't be pointed at the renamed image.",
                };
            }
        }

        change.Renames.Clear();
    }

    private static async Task StageOptimizedAsync(
        ChartPlan plan, PlannedAction action, Context context, AssetStager stager, string staging, Change change, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        string name = action.SourceNames[0];
        string output = Path.Combine(staging, action.OutputName!);
        try
        {
            var assets = action.SourceNames.Select(n => plan.Chart.Assets.First(a => a.StoredName.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
            var sources = action.Kind == OptimizeActionKind.MixDown ? OptimizationPlanner.OnePerStem(assets) : assets;
            var files = new List<AssetFile>(sources.Count);
            foreach (var source in sources)
                files.Add(await stager.GetAsync(source, cancellationToken).ConfigureAwait(false));

            await OptimizeAsync(action, files, output, context, progress, cancellationToken).ConfigureAwait(false);

            var info = new FileInfo(output);
            if (!info.Exists)
                throw new IOException($"The optimizer wrote no file for '{name}'.");

            // The estimate was a guess; a result that's no smaller than what it replaces isn't kept, unless the file was
            // converted so the games can play it, when being larger is the price.
            if (info.Length >= action.InputBytes && !action.ForCompatibility)
            {
                info.Delete();
                change.Assets.Add(new AssetResult(name, action.Strategy, action.Kind, action.InputBytes, action.InputBytes, AssetOutcome.Skipped, action.OutputName, nameof(OptimizeSkipReason.WouldGrow), action.EstimatedOutputBytes));
                return;
            }

            change.Removes.UnionWith(action.SourceNames);
            change.Adds.Add(new AddedFile(action.OutputName!, output, info.Length));
            if (action.Strategy is OptimizeStrategy.AlbumArt or OptimizeStrategy.BackgroundArt
                && !action.OutputName!.Equals(name, StringComparison.OrdinalIgnoreCase))
                change.Renames[name] = action.OutputName;
            change.Assets.Add(new AssetResult(name, action.Strategy, action.Kind, action.InputBytes, info.Length, AssetOutcome.Applied, action.OutputName, null, action.EstimatedOutputBytes));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // This file stays as it was; the chart's others carry on.
            StagingPath.TryDeleteFile(output);
            change.Assets.Add(new AssetResult(name, action.Strategy, action.Kind, action.InputBytes, action.InputBytes, AssetOutcome.Failed, action.OutputName, ex.Message, action.EstimatedOutputBytes));
        }
    }

    /// <summary>Reports on the calling thread, where <see cref="Progress{T}"/> would post to a synchronization context and arrive out of order.</summary>
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static Task OptimizeAsync(PlannedAction action, IReadOnlyList<AssetFile> files, string output, Context context, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var options = context.Options;
        var optimizers = context.Optimizers;
        switch (action.Strategy)
        {
            case OptimizeStrategy.Audio when optimizers.Audio is { IsAvailable: true } audio:
                return audio.OptimizeAsync(files, output, options.Audio, progress, cancellationToken);
            case OptimizeStrategy.StemMixdown when optimizers.Audio is { IsAvailable: true } audio:
                return audio.OptimizeAsync(files, output, options.StemMixdown, progress, cancellationToken);
            case OptimizeStrategy.Video when optimizers.Video is { IsAvailable: true } video:
                return video.OptimizeAsync(files[0], output, options.Video, progress, cancellationToken);
            case OptimizeStrategy.AlbumArt when optimizers.Image is { IsAvailable: true } image:
                return image.OptimizeAsync(files[0], output, options.AlbumArt, cancellationToken);
            case OptimizeStrategy.BackgroundArt when optimizers.Image is { IsAvailable: true } image:
                return image.OptimizeAsync(files[0], output, options.BackgroundArt, cancellationToken);
            default:
                throw new InvalidOperationException($"No optimizer is available for {action.Strategy}.");
        }
    }

    private static async Task StageMetadataAsync(ChartPlan plan, PlannedAction action, string staging, Change change, CancellationToken cancellationToken)
    {
        var cleanup = plan.Metadata!;
        change.Edits = cleanup.Edits;
        if (cleanup.NewMidiBytes is { } midi && cleanup.MidiName is { } midiName)
        {
            string path = Path.Combine(staging, midiName);
            await File.WriteAllBytesAsync(path, midi, cancellationToken).ConfigureAwait(false);
            // The same name is removed and added: SngWriter drops removed names before it checks an add for a duplicate,
            // and SongFolderFiles.ApplyAsync leaves a removal with an add's exact path for the add to replace.
            change.Removes.Add(midiName);
            change.Adds.Add(new AddedFile(midiName, path, midi.Length));
        }

        change.Assets.Add(new AssetResult(MetadataName, OptimizeStrategy.Metadata, action.Kind, action.InputBytes, action.EstimatedOutputBytes, AssetOutcome.Applied));
    }

    /// <summary>Writes the staged change to the chart at <paramref name="path"/>.</summary>
    private static async Task ApplyAsync(SongEntryKind kind, string path, Change change, CancellationToken cancellationToken)
    {
        if (kind == SongEntryKind.SngFile)
        {
            var read = await SngReader.ReadAsync(path, cancellationToken).ConfigureAwait(false);
            if (read.HasErrors)
                throw new IOException(read.Findings.First(f => f.Severity == Severity.Error).Message);

            IReadOnlyList<KeyValuePair<string, string>> pairs = [.. read.Metadata.Select(p => KeyValuePair.Create(p.Key, p.Value))];
            if (change.Edits is { } edits)
                pairs = edits.ApplyTo(pairs);

            var add = change.Adds.Select(a => new SngSourceFile(a.StoredName, a.SourcePath, new FileInfo(a.SourcePath).Length)).ToList();
            var findings = await SngWriter.RewriteAsync(path, pairs, change.Removes, add, cancellationToken).ConfigureAwait(false);
            if (findings.FirstOrDefault(f => f.Severity == Severity.Error) is { } error)
                throw new IOException(error.Message);
            return;
        }

        // song.ini is written once the new files are in place and before the removed ones go, as a saved draft does it.
        string? songIni = SongIni.FindIn(path);
        Func<CancellationToken, Task>? writeSongIni = change.Edits is { HasMetadataChanges: true } edit && songIni is not null
            ? ct => SongIni.ApplyEditsAsync(songIni, edit, ct)
            : null;
        if (change.Removes.Count > 0 || change.Adds.Count > 0)
            await SongFolderFiles.ApplyAsync(path, change.Removes, change.Adds, cancellationToken, writeSongIni).ConfigureAwait(false);
        else if (writeSongIni is not null)
            await writeSongIni(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Copies the chart to <paramref name="copy"/>, a staging path, for the output folder.</summary>
    private static async Task CopyChartAsync(SongEntry entry, string copy, CancellationToken cancellationToken)
    {
        if (entry.Kind == SongEntryKind.SngFile)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(entry.FullPath, copy);
            return;
        }

        Directory.CreateDirectory(copy);
        foreach (var file in await SongFolderListing.ListAsync(entry.FullPath, cancellationToken).ConfigureAwait(false))
        {
            string destination = Path.Combine(copy, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var source = new FileStream(Path.Combine(entry.FullPath, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)), FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
            await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string OutputTarget(SongEntry entry, string outputFolder)
    {
        string relative = entry.RelativePath == "." ? entry.DisplayName + (entry.Kind == SongEntryKind.SngFile ? ".sng" : "") : entry.RelativePath;
        return Path.Combine(outputFolder, relative);
    }

    private static async Task<long> SizeOfAsync(SongEntryKind kind, string path, CancellationToken cancellationToken) =>
        kind == SongEntryKind.SngFile
            ? new FileInfo(path).Length
            : (await SongFolderListing.ListAsync(path, cancellationToken).ConfigureAwait(false)).Sum(f => f.Length);

    private static List<AssetResult> SkippedAssets(ChartPlan plan) =>
    [
        .. plan.Actions.Where(a => a.IsSkip).Select(a => new AssetResult(
            a.SourceNames.FirstOrDefault() ?? MetadataName, a.Strategy, a.Kind, a.InputBytes, a.InputBytes, AssetOutcome.Skipped, a.OutputName, a.Reason.ToString())),
    ];
}
