using ChartPacker.Core.Scanning;
using ChartPacker.Core.Validation;

namespace ChartPacker.Core.Jobs;

/// <summary>What a job does to each item.</summary>
public enum JobKind
{
    /// <summary>Song folder to <c>.sng</c>.</summary>
    Pack,

    /// <summary><c>.sng</c> to song folder.</summary>
    Unpack,
}

/// <summary>What to do when an item's output already exists (project spec §8).</summary>
public enum OverwritePolicy
{
    /// <summary>Leave the existing output alone and skip the item.</summary>
    Skip,

    /// <summary>Replace the existing <c>.sng</c>, or write into the existing folder.</summary>
    Overwrite,

    /// <summary>Write to the first free name of the form <c>Name (2)</c>.</summary>
    Rename,
}

/// <summary>How an item ended.</summary>
public enum JobItemStatus
{
    Succeeded,
    SucceededWithWarnings,

    /// <summary>The output was written, but part of the input was left out (unpack only).</summary>
    CompletedWithErrors,

    Skipped,
    Failed,
}

/// <summary>Why an item was skipped.</summary>
public enum SkipReason
{
    None,

    /// <summary>A <c>.sng</c> was selected for packing.</summary>
    AlreadyPacked,

    /// <summary>A song folder was selected for unpacking.</summary>
    AlreadyUnpacked,

    /// <summary>The output exists and the policy is <see cref="OverwritePolicy.Skip"/>.</summary>
    OutputExists,

    /// <summary>
    /// An earlier item in the same job already wrote this output (e.g. two folders with the same
    /// name packed flat), and the policy isn't <see cref="OverwritePolicy.Rename"/>.
    /// </summary>
    DuplicateOutput,
}

/// <summary>The outcome of one item in a job.</summary>
/// <param name="Entry">The item.</param>
/// <param name="Status">How it ended.</param>
/// <param name="SkipReason">Why it was skipped, when <paramref name="Status"/> is <see cref="JobItemStatus.Skipped"/>.</param>
/// <param name="OutputPath">The output written, or the existing one that caused a skip.</param>
/// <param name="Findings">Findings from planning, writing and verifying.</param>
/// <param name="ErrorMessage">The system error message when the item failed with an I/O error.</param>
/// <param name="ConflictsWith">The earlier item that wrote <paramref name="OutputPath"/>, when <paramref name="SkipReason"/> is <see cref="SkipReason.DuplicateOutput"/>.</param>
public sealed record JobItemResult(
    SongEntry Entry,
    JobItemStatus Status,
    SkipReason SkipReason,
    string? OutputPath,
    IReadOnlyList<Finding> Findings,
    string? ErrorMessage = null,
    SongEntry? ConflictsWith = null);
