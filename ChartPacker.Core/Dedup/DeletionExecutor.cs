using ChartPacker.Core.Scanning;
using ChartPacker.Core.Trash;

namespace ChartPacker.Core.Dedup;

/// <summary>What happened to one copy the duplicate finder was asked to delete.</summary>
public enum DeletionStatus
{
    /// <summary>It's in the trash.</summary>
    Trashed,

    /// <summary>The OS couldn't recycle it and the user agreed to delete it permanently.</summary>
    DeletedPermanently,

    /// <summary>It was left alone; see <see cref="DeletionOutcome.Reason"/>.</summary>
    Skipped,

    /// <summary>Moving it to the trash failed; see <see cref="DeletionOutcome.Error"/>.</summary>
    Failed,
}

/// <summary>Why a copy was left alone.</summary>
public enum DeletionSkipReason
{
    /// <summary>It wasn't skipped.</summary>
    None,

    /// <summary>The copy being kept is gone.</summary>
    KeeperMissing,

    /// <summary>The copy being kept changed since it was compared.</summary>
    KeeperChanged,

    /// <summary>The copy is already gone.</summary>
    VictimMissing,

    /// <summary>The copy changed since it was compared.</summary>
    VictimChanged,

    /// <summary>The copy has unsaved metadata edits.</summary>
    HasUnsavedChanges,

    /// <summary>The OS warned it couldn't be recycled, and the user chose to keep it.</summary>
    DeclinedPermanentDelete,
}

/// <summary>The outcome for one <see cref="DeletionItem"/>.</summary>
public sealed record DeletionOutcome(DeletionItem Item, DeletionStatus Status, DeletionSkipReason Reason = DeletionSkipReason.None, string? Error = null);

/// <summary>Sends duplicate copies to the trash, checking each one first.</summary>
/// <remarks>
/// Before each copy is touched, both it and the copy being kept are checked against how they
/// looked when they were compared; anything missing or changed is skipped. Nothing here deletes
/// a file itself: copies only ever go through <see cref="ITrash"/>.
/// </remarks>
public static class DeletionExecutor
{
    /// <summary>Deletes <paramref name="items"/> one at a time, logging each outcome.</summary>
    /// <param name="items">The copies to delete.</param>
    /// <param name="trash">Where they go.</param>
    /// <param name="log">Where outcomes are recorded. If it can't be opened, nothing is deleted.</param>
    /// <param name="hasDrafts">Whether the song at a path has unsaved metadata edits.</param>
    /// <param name="progress">Told each outcome as it happens.</param>
    /// <param name="cancellationToken">Stops before the next copy; a copy being moved is finished.</param>
    /// <returns>The outcomes, in order.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="items"/> break a <see cref="DeletionPlan.Validate"/> rule.</exception>
    /// <exception cref="IOException">The log can't be opened or written. The run stops, but the outcome that couldn't be written has been reported.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the log is denied.</exception>
    /// <exception cref="OperationCanceledException">Cancelled before the first copy.</exception>
    public static Task<IReadOnlyList<DeletionOutcome>> RunAsync(
        IReadOnlyList<DeletionItem> items,
        ITrash trash,
        DeletionLog log,
        Func<string, bool>? hasDrafts = null,
        IProgress<DeletionOutcome>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(trash);
        ArgumentNullException.ThrowIfNull(log);
        hasDrafts ??= _ => false;

        DeletionPlan.Validate(items, hasDrafts);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run<IReadOnlyList<DeletionOutcome>>(() =>
        {
            using var session = log.Open(trash.Kind);
            var outcomes = new List<DeletionOutcome>(items.Count);
            foreach (var item in items)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                var outcome = Delete(item, trash, hasDrafts);
                outcomes.Add(outcome);

                // Reported before it's logged, so a copy that's gone is still known to be gone if the log then fails.
                progress?.Report(outcome);
                session.Write(outcome);
            }

            return outcomes;
        }, CancellationToken.None);
    }

    private static DeletionOutcome Delete(DeletionItem item, ITrash trash, Func<string, bool> hasDrafts)
    {
        try
        {
            if (hasDrafts(item.Victim.FullPath))
                return Skip(item, DeletionSkipReason.HasUnsavedChanges);

            var keeper = EntryStamps.Capture(item.Keeper.Path, item.Keeper.Kind);
            if (keeper is null)
                return Skip(item, DeletionSkipReason.KeeperMissing);
            if (keeper != item.Keeper.Stamp)
                return Skip(item, DeletionSkipReason.KeeperChanged);

            var victim = EntryStamps.Capture(item.Victim.FullPath, item.Victim.Kind);
            if (victim is null)
                return Skip(item, DeletionSkipReason.VictimMissing);
            if (victim != item.VictimStamp)
                return Skip(item, DeletionSkipReason.VictimChanged);

            bool isDirectory = item.Victim.Kind == SongEntryKind.SongFolder;
            var result = trash.MoveToTrash(item.Victim.FullPath, isDirectory);
            switch (result.Status)
            {
                case TrashStatus.Declined:
                    return Skip(item, DeletionSkipReason.DeclinedPermanentDelete);
                case TrashStatus.Failed:
                    return new DeletionOutcome(item, DeletionStatus.Failed, Error: result.Error);
            }

            if (isDirectory ? Directory.Exists(item.Victim.FullPath) : File.Exists(item.Victim.FullPath))
                return new DeletionOutcome(item, DeletionStatus.Failed, Error: "It's still there after being moved to the trash.");

            return new DeletionOutcome(item, result.Status == TrashStatus.DeletedPermanently ? DeletionStatus.DeletedPermanently : DeletionStatus.Trashed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DeletionOutcome(item, DeletionStatus.Failed, Error: ex.Message);
        }
    }

    private static DeletionOutcome Skip(DeletionItem item, DeletionSkipReason reason) => new(item, DeletionStatus.Skipped, reason);
}
