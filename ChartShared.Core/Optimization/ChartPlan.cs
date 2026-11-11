namespace ChartShared.Core.Optimization;

/// <summary>What a <see cref="PlannedAction"/> does.</summary>
public enum OptimizeActionKind
{
    /// <summary>Nothing; <see cref="PlannedAction.Reason"/> says why.</summary>
    Skip,

    /// <summary>Re-encodes a file in another codec: audio to Opus, say.</summary>
    Transcode,

    /// <summary>Re-encodes a file in the same kind of codec at a lower size: video or an image.</summary>
    Recompress,

    /// <summary>Deletes a file.</summary>
    Remove,

    /// <summary>Mixes several stems into one.</summary>
    MixDown,

    /// <summary>Cleans up the chart's metadata (<see cref="MetadataCleanup"/>).</summary>
    CleanMetadata,
}

/// <summary>Why a file or step is left as it is.</summary>
public enum OptimizeSkipReason
{
    /// <summary>It isn't skipped.</summary>
    None,

    /// <summary>The saving is smaller than <see cref="OptimizeOptions.SavingsMargin"/>.</summary>
    BelowMargin,

    /// <summary>The output would be as large as the input, or larger.</summary>
    WouldGrow,

    /// <summary>The optimizer says it's already as the settings would leave it.</summary>
    AlreadyOptimal,

    /// <summary>There's no optimizer for it, or it can't run here.</summary>
    OptimizerUnavailable,

    /// <summary>The optimized file's name is already taken by another file in the chart.</summary>
    OutputNameTaken,

    /// <summary>Metadata cleanup has nothing to change.</summary>
    NothingToClean,

    /// <summary>The file couldn't be read to estimate it.</summary>
    EstimateFailed,
}

/// <summary>One thing the planner decided about a chart.</summary>
/// <param name="Strategy">The strategy it belongs to.</param>
/// <param name="Kind">What it does; <see cref="OptimizeActionKind.Skip"/> when it's left alone.</param>
/// <param name="SourceNames">The stored names it reads and replaces or removes. Empty for <see cref="OptimizeActionKind.CleanMetadata"/>.</param>
/// <param name="OutputName">The stored name it writes; <see langword="null"/> for a removal or a skip with no estimate.</param>
/// <param name="CodecKey">The input's codec, as the savings estimates group it.</param>
/// <param name="InputBytes">The size of what it replaces.</param>
/// <param name="EstimatedOutputBytes">The estimated size of what it writes; 0 for a removal.</param>
/// <param name="Reason">Why it's a skip; <see cref="OptimizeSkipReason.None"/> otherwise.</param>
public sealed record PlannedAction(
    OptimizeStrategy Strategy,
    OptimizeActionKind Kind,
    IReadOnlyList<string> SourceNames,
    string? OutputName,
    string? CodecKey,
    long InputBytes,
    long EstimatedOutputBytes,
    OptimizeSkipReason Reason = OptimizeSkipReason.None)
{
    /// <summary>Whether it leaves the chart alone.</summary>
    public bool IsSkip => Kind == OptimizeActionKind.Skip;

    /// <summary>What it's expected to save; negative when the output would be larger.</summary>
    public long EstimatedSavingBytes => InputBytes - EstimatedOutputBytes;
}

/// <summary>What the planner decided about one chart.</summary>
/// <param name="Chart">The chart as it was inspected.</param>
/// <param name="Actions">What to do, and what's skipped and why.</param>
/// <param name="Metadata">The metadata cleanup; <see langword="null"/> when it's off or has nothing to change.</param>
public sealed record ChartPlan(ChartAssets Chart, IReadOnlyList<PlannedAction> Actions, MetadataCleanupPlan? Metadata = null)
{
    /// <summary>The actions that change the chart.</summary>
    public IEnumerable<PlannedAction> Work => Actions.Where(a => !a.IsSkip);

    /// <summary>Whether there's anything to change.</summary>
    public bool HasWork => Actions.Any(a => !a.IsSkip);
}
