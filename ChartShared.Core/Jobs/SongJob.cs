using ChartShared.Core.Packing;
using ChartShared.Core.Scanning;
using ChartShared.Core.Sng;
using ChartShared.Core.Validation;
namespace ChartShared.Core.Jobs;

/// <summary>Packs or unpacks a list of library entries, one at a time.</summary>
/// <remarks>
/// <para>
/// A bad item never stops the job (AC4.2): an entry of the wrong kind, an existing output under
/// <see cref="OverwritePolicy.Skip"/>, a plan with errors or an I/O failure is reported for that
/// item and the job moves on. Only cancellation stops it, after the item in flight has cleaned up.
/// </para>
/// <para>
/// Two items can want the same output, e.g. <c>A/Song</c> and <c>B/Song</c> packed with
/// <see cref="OutputLayout.Flat"/>. The first to be written keeps it. A later one is renamed under
/// <see cref="OverwritePolicy.Rename"/>, and otherwise skipped as
/// <see cref="SkipReason.DuplicateOutput"/>, so the job never overwrites its own output.
/// </para>
/// </remarks>
public static class SongJob
{
    /// <summary>Runs the job, reporting each item's result as it finishes.</summary>
    /// <param name="kind">Pack or unpack.</param>
    /// <param name="entries">The items, in the order to process them.</param>
    /// <param name="scanRoot">The folder the entries were scanned from.</param>
    /// <param name="destinationRoot">Where outputs go.</param>
    /// <param name="policy">What to do when an output already exists.</param>
    /// <param name="progress">Receives each item's result.</param>
    /// <param name="layout">Whether outputs mirror each entry's place under <paramref name="scanRoot"/> or all go directly in <paramref name="destinationRoot"/>.</param>
    /// <param name="options">Pack options; the defaults when <see langword="null"/>.</param>
    /// <param name="transcoder">Transcodes non-Opus stems when <see cref="PackOptions.TranscodeToOpus"/>; no transcoding when <see langword="null"/>.</param>
    /// <param name="exportProgress">Receives transcode and pack progress for the item in flight; may be <see langword="null"/>.</param>
    /// <param name="cancellationToken">Stops the job.</param>
    /// <exception cref="OperationCanceledException">The job was cancelled.</exception>
    public static async Task RunAsync(
        JobKind kind,
        IReadOnlyList<SongEntry> entries,
        string scanRoot,
        string destinationRoot,
        OverwritePolicy policy,
        IProgress<JobItemResult> progress,
        OutputLayout layout = OutputLayout.Mirror,
        PackOptions? options = null,
        IPackTranscoder? transcoder = null,
        IProgress<ExportProgress>? exportProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrWhiteSpace(scanRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentNullException.ThrowIfNull(progress);

        var job = new Job(kind, scanRoot, destinationRoot, policy, layout, options, transcoder, exportProgress);
        foreach (SongEntry entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JobItemResult result = await job.RunItemAsync(entry, cancellationToken).ConfigureAwait(false);
            progress.Report(result);
        }
    }

    private sealed class Job(
        JobKind kind,
        string scanRoot,
        string destinationRoot,
        OverwritePolicy policy,
        OutputLayout layout,
        PackOptions? options,
        IPackTranscoder? transcoder,
        IProgress<ExportProgress>? exportProgress)
    {
        // Outputs this job has written, preferred and actual paths both, and the item that wrote each.
        private readonly Dictionary<string, SongEntry> _claimed = new(OutputPaths.PathComparer);

        public async Task<JobItemResult> RunItemAsync(SongEntry entry, CancellationToken cancellationToken)
        {
            if (kind == JobKind.Pack && entry.Kind == SongEntryKind.SngFile)
                return Skipped(entry, SkipReason.AlreadyPacked, null);
            if (kind == JobKind.Unpack && entry.Kind == SongEntryKind.SongFolder)
                return Skipped(entry, SkipReason.AlreadyUnpacked, null);

            try
            {
                return kind == JobKind.Pack
                    ? await PackAsync(entry, cancellationToken).ConfigureAwait(false)
                    : await UnpackAsync(entry, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new JobItemResult(entry, JobItemStatus.Failed, SkipReason.None, null, [], ex.Message);
            }
        }

        private async Task<JobItemResult> PackAsync(SongEntry entry, CancellationToken cancellationToken)
        {
            PackPlan plan = await PackPlanner.PlanAsync(entry.FullPath, options, cancellationToken).ConfigureAwait(false);
            if (plan.HasErrors)
                return new JobItemResult(entry, JobItemStatus.Failed, SkipReason.None, null, plan.Findings);

            string preferred = OutputPaths.PackTarget(entry, scanRoot, destinationRoot, layout);
            if (CheckTarget(entry, preferred, isDirectory: false, out string? target) is { } skipped)
                return skipped;

            PackOptions effective = options ?? PackOptions.Default;
            string? transcodeFolder = null;
            try
            {
                if (effective.TranscodeToOpus && transcoder is not null && transcoder.IsAvailable)
                {
                    transcodeFolder = StagingPath.For(target!);
                    Directory.CreateDirectory(transcodeFolder);
                    IReadOnlyList<SngSourceFile> included = await transcoder.TranscodeAsync(
                        plan, transcodeFolder, effective.OpusBitrateKbps, exportProgress, cancellationToken).ConfigureAwait(false);
                    plan = plan with { Included = included };
                }

                long totalBytes = plan.Included.Sum(f => f.Length);
                string packName = Path.GetFileName(target!);
                var verify = await Packer.PackAsync(
                    plan,
                    target!,
                    policy == OverwritePolicy.Overwrite,
                    exportProgress is null ? null : new Progress<long>(written => exportProgress.Report(
                        new ExportProgress(
                            ExportPhase.Packing,
                            0, 1, packName,
                            totalBytes <= 0 ? 1 : Math.Clamp((double)written / totalBytes, 0, 1)))),
                    cancellationToken).ConfigureAwait(false);
                Finding[] findings = [.. plan.Findings, .. verify];

                if (verify.Any(f => f.Severity == Severity.Error))
                    return new JobItemResult(entry, JobItemStatus.Failed, SkipReason.None, null, findings);

                Claim(entry, preferred, target!);
                return new JobItemResult(entry, StatusFor(findings), SkipReason.None, target, findings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return new JobItemResult(entry, JobItemStatus.Failed, SkipReason.None, null, [], ex.Message);
            }
            finally
            {
                if (transcodeFolder is not null)
                    StagingPath.TryDeleteDirectory(transcodeFolder);
            }
        }

        private async Task<JobItemResult> UnpackAsync(SongEntry entry, CancellationToken cancellationToken)
        {
            string preferred = OutputPaths.UnpackTarget(entry, scanRoot, destinationRoot, layout);
            if (CheckTarget(entry, preferred, isDirectory: true, out string? target) is { } skipped)
                return skipped;

            UnpackResult result = await Unpacker.UnpackAsync(entry.FullPath, target!, policy == OverwritePolicy.Overwrite, cancellationToken).ConfigureAwait(false);
            if (!result.Extracted)
                return new JobItemResult(entry, JobItemStatus.Failed, SkipReason.None, null, result.Findings);

            Claim(entry, preferred, target!);
            JobItemStatus status = result.CompletedWithErrors ? JobItemStatus.CompletedWithErrors : StatusFor(result.Findings);
            return new JobItemResult(entry, status, SkipReason.None, target, result.Findings);
        }

        /// <summary>
        /// Decides where <paramref name="entry"/> writes. Returns a skip result, or
        /// <see langword="null"/> with the path in <paramref name="target"/>.
        /// </summary>
        private JobItemResult? CheckTarget(SongEntry entry, string preferred, bool isDirectory, out string? target)
        {
            target = null;

            // Renaming resolves the clash on its own, because the earlier output now exists.
            if (policy != OverwritePolicy.Rename && _claimed.TryGetValue(Path.GetFullPath(preferred), out SongEntry? owner))
                return new JobItemResult(entry, JobItemStatus.Skipped, SkipReason.DuplicateOutput, preferred, [], ConflictsWith: owner);

            target = OutputPaths.Resolve(preferred, policy, isDirectory);
            return target is null ? Skipped(entry, SkipReason.OutputExists, preferred) : null;
        }

        private void Claim(SongEntry entry, string preferred, string target)
        {
            _claimed.TryAdd(Path.GetFullPath(preferred), entry);
            _claimed.TryAdd(Path.GetFullPath(target), entry);
        }
    }

    private static JobItemStatus StatusFor(IReadOnlyList<Finding> findings) =>
        findings.Any(f => f.Severity == Severity.Warning) ? JobItemStatus.SucceededWithWarnings : JobItemStatus.Succeeded;

    private static JobItemResult Skipped(SongEntry entry, SkipReason reason, string? existing) =>
        new(entry, JobItemStatus.Skipped, reason, existing, []);
}
