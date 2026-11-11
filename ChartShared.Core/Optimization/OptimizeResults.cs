using ChartShared.Core.Scanning;

namespace ChartShared.Core.Optimization;

/// <summary>How optimizing one chart went.</summary>
public enum OptimizeItemStatus
{
    /// <summary>Something was changed.</summary>
    Succeeded,

    /// <summary>The chart was left alone; <see cref="OptimizeItemResult.SkipReason"/> says why.</summary>
    Skipped,

    /// <summary>It failed, and the chart is as it was.</summary>
    Failed,
}

/// <summary>Why a chart was left alone.</summary>
public enum OptimizeItemSkipReason
{
    /// <summary>It wasn't skipped.</summary>
    None,

    /// <summary>The plan has nothing to change.</summary>
    NothingToDo,

    /// <summary>Chart Packer holds unsaved metadata changes to it.</summary>
    HasUnsavedChanges,

    /// <summary>Another app holds the chart.</summary>
    InUseElsewhere,

    /// <summary>It changed since it was planned.</summary>
    ChartChanged,

    /// <summary>The output folder already has a chart where this one would go.</summary>
    OutputExists,
}

/// <summary>What happened to one of a chart's files, or to its metadata.</summary>
public enum AssetOutcome
{
    /// <summary>The change was made.</summary>
    Applied,

    /// <summary>It was left alone.</summary>
    Skipped,

    /// <summary>Optimizing it failed; the chart's other files carried on.</summary>
    Failed,
}

/// <summary>What happened to one file, or to the chart's metadata, with its size before and after.</summary>
/// <param name="StoredName">The file's name; <c>metadata</c> for metadata cleanup.</param>
/// <param name="Strategy">The strategy.</param>
/// <param name="Kind">What was planned.</param>
/// <param name="BytesBefore">Its size before.</param>
/// <param name="BytesAfter">Its size after; for a skip or a failure, as it was; 0 for a removal.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="OutputName">The name it was written under.</param>
/// <param name="Detail">The plan's skip reason, or the failure's message.</param>
public sealed record AssetResult(
    string StoredName,
    OptimizeStrategy Strategy,
    OptimizeActionKind Kind,
    long BytesBefore,
    long BytesAfter,
    AssetOutcome Outcome,
    string? OutputName = null,
    string? Detail = null);

/// <summary>How optimizing one chart went.</summary>
/// <param name="Entry">The chart.</param>
/// <param name="Status">The outcome.</param>
/// <param name="SkipReason">Why it was skipped.</param>
/// <param name="Assets">What happened to each file the plan covered.</param>
/// <param name="BytesBefore">The chart's whole size before.</param>
/// <param name="BytesAfter">Its size after; as it was when skipped or failed.</param>
/// <param name="OutputPath">
/// Where the optimized chart is: the chart, or its copy in the output folder. <see langword="null"/> when nothing was
/// written, so a chart that's skipped or fails is never copied to an output folder.
/// </param>
/// <param name="ErrorMessage">What failed, for <see cref="OptimizeItemStatus.Failed"/>.</param>
public sealed record OptimizeItemResult(
    SongEntry Entry,
    OptimizeItemStatus Status,
    OptimizeItemSkipReason SkipReason,
    IReadOnlyList<AssetResult> Assets,
    long BytesBefore,
    long BytesAfter,
    string? OutputPath = null,
    string? ErrorMessage = null)
{
    /// <summary>What was saved; negative if the chart got larger.</summary>
    public long BytesSaved => BytesBefore - BytesAfter;
}

/// <summary>How a run went.</summary>
/// <param name="Items">Each chart's result, in the order they were run.</param>
public sealed record OptimizeJobResult(IReadOnlyList<OptimizeItemResult> Items)
{
    /// <summary>How many charts were changed.</summary>
    public int Succeeded => Items.Count(i => i.Status == OptimizeItemStatus.Succeeded);

    /// <summary>How many charts were left alone.</summary>
    public int Skipped => Items.Count(i => i.Status == OptimizeItemStatus.Skipped);

    /// <summary>How many charts failed.</summary>
    public int Failed => Items.Count(i => i.Status == OptimizeItemStatus.Failed);

    /// <summary>The size of the charts before.</summary>
    public long BytesBefore => Items.Sum(i => i.BytesBefore);

    /// <summary>The size of the charts after.</summary>
    public long BytesAfter => Items.Sum(i => i.BytesAfter);

    /// <summary>What was saved overall.</summary>
    public long BytesSaved => BytesBefore - BytesAfter;
}

/// <summary>Progress through a run.</summary>
/// <param name="Done">How many charts are finished.</param>
/// <param name="Total">How many charts there are.</param>
/// <param name="Current">The chart being worked on; <see langword="null"/> once all are done.</param>
/// <param name="CurrentAsset">The file being optimized, when there is one.</param>
/// <param name="Completed">The result of the chart that just finished; <see langword="null"/> when this report is only a start.</param>
/// <param name="FileFraction">How far through <paramref name="CurrentAsset"/> the optimizer is, 0 to 1; <see langword="null"/> when this report isn't about that.</param>
public sealed record OptimizeProgress(int Done, int Total, SongEntry? Current, string? CurrentAsset, OptimizeItemResult? Completed, double? FileFraction = null);

/// <summary>What the run needs from the app that hosts it.</summary>
/// <param name="Locks">The app's lock set, so a chart it holds for an unsaved edit counts as its own; a private one when null.</param>
/// <param name="Gate">The app's gate, so a write waits for copies of the chart in progress; a private one when null.</param>
/// <param name="HasDrafts">Whether a chart, by full path, has unsaved metadata changes. Such a chart is skipped, as writing under it would be overwritten by the save.</param>
/// <param name="ReleaseFiles">Called once a chart is held and before it's written, so the app can let go of files it has open, such as the inspector's audio and video.</param>
public sealed record OptimizeJobServices(
    SongLockSet? Locks = null,
    SongFileGate? Gate = null,
    Func<string, bool>? HasDrafts = null,
    Func<SongEntry, CancellationToken, Task>? ReleaseFiles = null);
