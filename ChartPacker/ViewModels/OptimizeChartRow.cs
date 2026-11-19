using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using ChartPacker.Resources;
using ChartShared.Core.Optimization;
using ChartShared.Core.Scanning;
using ChartShared.UI.Formatting;

namespace ChartPacker.ViewModels
{
    /// <summary>Where a chart is in the Optimize window.</summary>
    public enum OptimizeRowState
    {
        /// <summary>Its files haven't been listed yet.</summary>
        Reading,

        /// <summary>It couldn't be read, so it's left out.</summary>
        Unreadable,

        /// <summary>Its files are known, and nothing is switched on that would touch them.</summary>
        Listed,

        /// <summary>Its files are known, and nothing yet about what they'd save.</summary>
        Unknown,

        /// <summary>Its saving is predicted from measured files of the same types.</summary>
        Predicted,

        /// <summary>It's been measured: its saving is what a run plans to do.</summary>
        Measured,

        /// <summary>A run is under way and hasn't reached it.</summary>
        Waiting,

        /// <summary>A run has reached it before it was measured, and is measuring it now.</summary>
        Measuring,

        /// <summary>A run is working on it.</summary>
        Working,

        /// <summary>A run made it smaller.</summary>
        Saved,

        /// <summary>A run left it alone.</summary>
        Skipped,

        /// <summary>A run failed on it; it's as it was.</summary>
        Failed,
    }

    /// <summary>How the list's charts are grouped by its filter chips.</summary>
    public enum OptimizeRowGroup
    {
        /// <summary>It gets smaller, or did.</summary>
        Shrinks,

        /// <summary>There's nothing in it to shrink, or it was left alone.</summary>
        Nothing,

        /// <summary>It couldn't be read, or failed, or was left alone for a reason worth a look.</summary>
        Problem,
    }

    /// <summary>One of a chart's files in its breakdown: what happens to it, and its size before and after.</summary>
    public sealed record OptimizeFileRow(string Name, string Action, string Sizes, bool IsKept, bool IsFailed);

    /// <summary>
    /// A chart in the Optimize window's list: its size, what optimizing it saves (predicted, then measured, then done),
    /// and what happens to each of its files.
    /// </summary>
    /// <remarks>
    /// It's changed only on the UI thread, a field at a time, and then <see cref="Refresh"/> says everything it shows may
    /// have changed, so a burst of changes to a row the list isn't showing costs nothing.
    /// </remarks>
    public sealed class OptimizeChartRow : INotifyPropertyChanged
    {
        private static readonly PropertyChangedEventArgs Everything = new(string.Empty);

        private IReadOnlyList<OptimizeFileRow>? _files;
        private IReadOnlyList<OptimizeMediaPreviewViewModel> _previews = [];
        private IDisposable? _previewSession;
        private bool _isExpanded;

        public OptimizeChartRow(SongEntry entry)
        {
            Entry = entry;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public SongEntry Entry { get; }

        public string Name => Entry.DisplayName;

        public string Path => Entry.FullPath;

        /// <summary>The chart as it was read; null until it has been, or when it can't be.</summary>
        public ChartAssets? Assets { get; set; }

        /// <summary>What the strategies that are on could do to it.</summary>
        public IReadOnlyList<PlanUnit> Units { get; set; } = [];

        /// <summary>What a run would do to it; null until it's measured.</summary>
        public ChartPlan? Plan { get; set; }

        /// <summary>What it's expected to save: the plan's, or a prediction.</summary>
        public ChartSavings? Savings { get; set; }

        /// <summary>How a run went for it.</summary>
        public OptimizeItemResult? Result { get; set; }

        /// <summary>Why it couldn't be measured.</summary>
        public string? Error { get; set; }

        public OptimizeRowState State { get; set; }

        /// <summary>It was rewritten where it is, so what's known of its files is out of date.</summary>
        public bool IsStale { get; set; }

        /// <summary>The file a run is working on.</summary>
        public string? WorkingFile { get; set; }

        /// <summary>How far through <see cref="WorkingFile"/> the run is, 0 to 1.</summary>
        public double WorkingFraction { get; set; }

        /// <summary>Its size before, in bytes.</summary>
        public long SizeBytes => Result is { Status: OptimizeItemStatus.Succeeded } done ? done.BytesBefore : Assets?.TotalBytes ?? 0;

        /// <summary>What it saves, in bytes: what a run saved, or what's expected. Null when nothing's known.</summary>
        public long? SavingBytes => State switch
        {
            OptimizeRowState.Saved => Result?.BytesSaved,
            OptimizeRowState.Reading or OptimizeRowState.Unreadable or OptimizeRowState.Unknown or OptimizeRowState.Listed => null,
            OptimizeRowState.Measuring when Plan is null && Savings is not { IsComplete: true } => null,
            OptimizeRowState.Skipped or OptimizeRowState.Failed => 0,
            _ => Savings?.TotalBytes,
        };

        /// <summary>What the run was expected to save, for a chart it changed.</summary>
        public long? EstimatedSavingBytes => Plan is { } plan ? SavingsModel.Measured(plan).TotalBytes : null;

        public bool IsReadable => Assets is not null;

        /// <summary>Whether its saving is a prediction, not yet measured.</summary>
        public bool IsPredicted => State == OptimizeRowState.Predicted || (State is OptimizeRowState.Waiting or OptimizeRowState.Measuring && Plan is null);

        public bool IsWorking => State == OptimizeRowState.Working;

        public bool IsNotWorking => !IsWorking;

        public bool IsFailed => State == OptimizeRowState.Failed;

        /// <summary>Something about it wants a look: it can't be read, or a run left it alone for a reason, or it'd get larger.</summary>
        public bool IsAttention => State == OptimizeRowState.Unreadable
            || (State == OptimizeRowState.Skipped && Result?.SkipReason is not (OptimizeItemSkipReason.NothingToDo or OptimizeItemSkipReason.None or null))
            || (State == OptimizeRowState.Measured && Error is not null);

        /// <summary>Nothing happens to it, so it's shown quietly.</summary>
        public bool IsQuiet => State is OptimizeRowState.Reading or OptimizeRowState.Unknown or OptimizeRowState.Listed or OptimizeRowState.Working or OptimizeRowState.Measuring
            || (State is OptimizeRowState.Measured or OptimizeRowState.Predicted or OptimizeRowState.Waiting && SavingBytes is null or 0 && !HasOtherWork)
            || (State == OptimizeRowState.Skipped && !IsAttention);

        public OptimizeRowGroup Group
        {
            get
            {
                if (IsFailed || IsAttention)
                    return OptimizeRowGroup.Problem;
                return State == OptimizeRowState.Saved || SavingBytes is > 0 || HasOtherWork ? OptimizeRowGroup.Shrinks : OptimizeRowGroup.Nothing;
            }
        }

        // Work that saves no bytes worth counting, or costs some: metadata cleanup, a conversion for compatibility.
        private bool HasOtherWork => State is OptimizeRowState.Measured or OptimizeRowState.Waiting && Plan is { HasWork: true };

        /// <summary>Whether a run would change it, as far as is known: a chart that hasn't been measured might.</summary>
        public bool MayChange => IsReadable && !IsStale && State is not (OptimizeRowState.Saved or OptimizeRowState.Skipped)
            && (Plan is { } plan ? plan.HasWork : Savings is not { IsComplete: true, TotalBytes: 0 });

        // ---- What the row shows -------------------------------------------------------------------------------

        public string SizeText => IsReadable || Result is not null ? ByteSizeFormatter.Format((ulong)Math.Max(0, SizeBytes)) : string.Empty;

        /// <summary>The saving, or what stands in for one.</summary>
        public string SavingText
        {
            get
            {
                switch (State)
                {
                    case OptimizeRowState.Reading:
                        return Strings.OptimizeRowReading;
                    case OptimizeRowState.Unreadable:
                        return Strings.OptimizeRowUnreadable;
                    case OptimizeRowState.Listed:
                        return string.Empty;
                    case OptimizeRowState.Unknown:
                        return Strings.OptimizeRowNotMeasured;
                    case OptimizeRowState.Working:
                        return WorkingFile ?? string.Empty;
                    case OptimizeRowState.Measuring:
                        return Strings.OptimizeRowMeasuring;
                    case OptimizeRowState.Failed:
                        return Strings.OptimizeRowFailed;
                    case OptimizeRowState.Skipped:
                        return IsAttention ? Strings.OptimizeRowLeft : Strings.OptimizeRowNothing;
                    case OptimizeRowState.Saved:
                        return string.Format(CultureInfo.CurrentCulture, Strings.OptimizeStatusSavedFormat, Signed(Result?.BytesSaved ?? 0, plain: true));
                }

                if (SavingBytes is not { } saving || saving == 0)
                    return HasOtherWork ? Strings.OptimizeRowNoBytes : Strings.OptimizeRowNothing;

                string text = Signed(saving, plain: false);
                return IsPredicted ? string.Format(CultureInfo.CurrentCulture, Strings.OptimizeRowPredictedFormat, text) : text;
            }
        }

        /// <summary>The saving as a share of the chart, or empty when there's none to show.</summary>
        public string PercentText => SavingBytes is { } saving && saving > 0 && SizeBytes > 0 && State != OptimizeRowState.Working
            ? string.Format(CultureInfo.CurrentCulture, Strings.OptimizePercentFormat, Math.Max(1, (int)Math.Round(100.0 * saving / SizeBytes)))
            : string.Empty;

        /// <summary>
        /// The line under the name: what happens to the chart, or what's known of it. It always says something once the
        /// chart's been read, "Nothing done" when nothing that's switched on changes it, so every row is the same height and
        /// the list doesn't shift as charts are measured.
        /// </summary>
        public string Detail
        {
            get
            {
                string detail = DetailOrEmpty;
                return detail.Length > 0 || State is OptimizeRowState.Reading or OptimizeRowState.Unknown ? detail : Strings.OptimizeRowNothingDone;
            }
        }

        private string DetailOrEmpty
        {
            get
            {
                switch (State)
                {
                    case OptimizeRowState.Reading:
                        return string.Empty;
                    case OptimizeRowState.Unreadable:
                        return Error ?? string.Empty;
                    case OptimizeRowState.Failed:
                        return Result?.ErrorMessage ?? Error ?? string.Empty;
                    case OptimizeRowState.Skipped:
                        return IsAttention ? SkipText(Result?.SkipReason ?? OptimizeItemSkipReason.None) : string.Empty;
                    case OptimizeRowState.Listed:
                        return string.Empty;
                    case OptimizeRowState.Saved:
                        return EstimatedSavingBytes is { } estimated
                            ? string.Format(CultureInfo.CurrentCulture, Strings.OptimizeRowEstimatedFormat, Signed(estimated, plain: true))
                            : string.Empty;
                }

                if (Error is not null)
                    return Error;
                if (Plan is { } plan)
                    return WorkSummary(plan);
                // A chart with nothing to measure isn't predicted: what's known of it is all there is.
                return IsPredicted && Units.Any(u => u.NeedsMeasuring) ? Strings.OptimizeRowPredictedDetail : string.Empty;
            }
        }

        public string ToolTip => IsPredicted ? Strings.OptimizeRowPredictedToolTip : Path;

        // The bar: what's kept, what's saved for certain, and what's predicted to be.

        private double SavedShare => SizeBytes > 0 && SavingBytes is { } saving && saving > 0 ? Math.Min(1, (double)saving / SizeBytes) : 0;

        public double BarKept => IsReadable || Result is not null ? 1 - SavedShare : 0;

        public double BarSaved => IsPredicted ? 0 : SavedShare;

        public double BarPredicted => IsPredicted ? SavedShare : 0;

        /// <summary>Whether its breakdown is open, which the selected row's is.</summary>
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value)
                    return;
                _isExpanded = value;
                _files = null;
                Refresh();
            }
        }

        /// <summary>Its pictures, before and after, while its breakdown is open and it has some that change.</summary>
        public IReadOnlyList<OptimizeMediaPreviewViewModel> Previews => _previews;

        public bool HasPreviews => _previews.Count > 0;

        /// <summary>Shows <paramref name="previews"/> in place of those shown before, which are let go of with their session.</summary>
        /// <param name="session">What the previews read the chart through; disposed with them.</param>
        public void SetPreviews(IReadOnlyList<OptimizeMediaPreviewViewModel> previews, IDisposable? session)
        {
            var old = _previews;
            var oldSession = _previewSession;
            _previews = previews;
            _previewSession = session;
            foreach (var preview in old)
                preview.Dispose();
            oldSession?.Dispose();
            foreach (var preview in previews)
                preview.Start();
            PropertyChanged?.Invoke(this, Everything);
        }

        /// <summary>Lets go of the previews and the files they hold.</summary>
        public void ClearPreviews()
        {
            if (_previews.Count > 0 || _previewSession is not null)
                SetPreviews([], null);
        }

        /// <summary>Its files, and what happens to each. Made when the row is opened.</summary>
        public IReadOnlyList<OptimizeFileRow> Files => !_isExpanded ? [] : _files ??= BuildFiles();

        /// <summary>Says everything the row shows may have changed.</summary>
        public void Refresh()
        {
            _files = null;
            PropertyChanged?.Invoke(this, Everything);
        }

        // ---- Wording ------------------------------------------------------------------------------------------

        // "−12 MB" in the list; "12 MB" where the words around it say which way it went. One that grows is "+3 MB" either way.
        private static string Signed(long bytes, bool plain)
        {
            string size = ByteSizeFormatter.Format((ulong)Math.Abs(bytes));
            if (bytes < 0)
                return string.Format(CultureInfo.CurrentCulture, Strings.OptimizeRowGrowsFormat, size);
            return plain ? size : string.Format(CultureInfo.CurrentCulture, Strings.OptimizeRowSavesFormat, size);
        }

        private static string SkipText(OptimizeItemSkipReason reason) => reason switch
        {
            OptimizeItemSkipReason.HasUnsavedChanges => Strings.OptimizeSkipHasUnsavedChanges,
            OptimizeItemSkipReason.InUseElsewhere => Strings.OptimizeSkipInUse,
            OptimizeItemSkipReason.ChartChanged => Strings.OptimizeSkipChartChanged,
            OptimizeItemSkipReason.OutputExists => Strings.OptimizeSkipOutputExists,
            _ => string.Empty,
        };

        // "3 stems to Opus, video recompressed, metadata cleaned up".
        private static string WorkSummary(ChartPlan plan)
        {
            var parts = new List<string>();
            int stems = plan.Work.Count(a => a.Strategy == OptimizeStrategy.Audio);
            if (plan.Work.Any(a => a.Strategy == OptimizeStrategy.StemMixdown))
                parts.Add(Strings.OptimizeDetailMixdown);
            if (stems == 1)
                parts.Add(Strings.OptimizeDetailStemOne);
            else if (stems > 1)
                parts.Add(string.Format(CultureInfo.CurrentCulture, Strings.OptimizeDetailStemsFormat, stems));

            foreach (var video in plan.Work.Where(a => a.Strategy == OptimizeStrategy.Video))
            {
                parts.Add(video.Kind == OptimizeActionKind.Remove ? Strings.OptimizeDetailVideoRemoved
                    : video.ForCompatibility ? Strings.OptimizeDetailVideoConverted
                    : Strings.OptimizeDetailVideo);
            }

            if (plan.Work.Any(a => a.Strategy == OptimizeStrategy.AlbumArt))
                parts.Add(Strings.OptimizeDetailAlbumArt);
            if (plan.Work.Any(a => a.Strategy == OptimizeStrategy.BackgroundArt))
                parts.Add(Strings.OptimizeDetailBackground);
            if (plan.Work.Any(a => a.Strategy == OptimizeStrategy.Metadata))
                parts.Add(Strings.OptimizeDetailMetadata);
            if (parts.Count == 0)
                return string.Empty;

            string text = string.Join(Strings.OptimizeDetailSeparator, parts);
            return char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];
        }

        private IReadOnlyList<OptimizeFileRow> BuildFiles()
        {
            // Once a run has been at it, what happened; before that, what's planned; before that, what there is to measure.
            if (Result is { Assets.Count: > 0 } result)
                return [.. result.Assets.Select(FileOf)];
            if (Plan is { } plan)
                return [.. plan.Actions.Select(FileOf)];
            return
            [
                .. Units.Select(u => new OptimizeFileRow(
                    string.Join(Strings.OptimizeDetailSeparator, u.Sources.Select(s => s.StoredName)),
                    u.KnownSavingBytes switch
                    {
                        null => Strings.OptimizeFileNotMeasured,
                        0 => Strings.OptimizeFileKeptOptimal,
                        _ => Strings.OptimizeFileRemoved,
                    },
                    ByteSizeFormatter.Format((ulong)u.InputBytes),
                    u.KnownSavingBytes == 0,
                    false)),
            ];
        }

        private static OptimizeFileRow FileOf(PlannedAction action)
        {
            string name = action.SourceNames.Count == 0 ? Strings.OptimizeFileMetadata : string.Join(Strings.OptimizeDetailSeparator, action.SourceNames);
            string sizes = action.IsSkip || action.Kind == OptimizeActionKind.CleanMetadata
                ? action.InputBytes > 0 && action.Kind != OptimizeActionKind.CleanMetadata ? ByteSizeFormatter.Format((ulong)action.InputBytes) : string.Empty
                : string.Format(CultureInfo.CurrentCulture, Strings.OptimizeFileSizesFormat,
                    ByteSizeFormatter.Format((ulong)action.InputBytes), ByteSizeFormatter.Format((ulong)Math.Max(0, action.EstimatedOutputBytes)));
            return new OptimizeFileRow(name, ActionText(action.Strategy, action.Kind, action.Reason, action.OutputName, action.ForCompatibility), sizes, action.IsSkip, false);
        }

        private static OptimizeFileRow FileOf(AssetResult asset)
        {
            bool metadata = asset.Strategy == OptimizeStrategy.Metadata;
            string name = metadata ? Strings.OptimizeFileMetadata : asset.StoredName;
            string before = ByteSizeFormatter.Format((ulong)Math.Max(0, asset.BytesBefore));
            switch (asset.Outcome)
            {
                case AssetOutcome.Failed:
                    return new OptimizeFileRow(name, string.Format(CultureInfo.CurrentCulture, Strings.OptimizeStatusFailedFormat, asset.Detail ?? string.Empty), before, false, true);

                case AssetOutcome.Skipped:
                    var reason = Enum.TryParse<OptimizeSkipReason>(asset.Detail, out var parsed) ? parsed : OptimizeSkipReason.None;
                    return new OptimizeFileRow(name, ActionText(asset.Strategy, OptimizeActionKind.Skip, reason, asset.OutputName, false), metadata ? string.Empty : before, true, false);

                default:
                    string after = ByteSizeFormatter.Format((ulong)Math.Max(0, asset.BytesAfter));
                    // Metadata's sizes are counts of characters, not of bytes on disk, so they aren't shown as sizes.
                    string sizes = metadata ? string.Empty
                        : asset.EstimatedBytesAfter is { } estimated && asset.Kind != OptimizeActionKind.Remove
                            ? string.Format(CultureInfo.CurrentCulture, Strings.OptimizeFileSizesEstimatedFormat, before, after, ByteSizeFormatter.Format((ulong)Math.Max(0, estimated)))
                            : string.Format(CultureInfo.CurrentCulture, Strings.OptimizeFileSizesFormat, before, after);
                    return new OptimizeFileRow(name, ActionText(asset.Strategy, asset.Kind, OptimizeSkipReason.None, asset.OutputName, false), sizes, false, false);
            }
        }

        private static string ActionText(OptimizeStrategy strategy, OptimizeActionKind kind, OptimizeSkipReason reason, string? output, bool compatibility) => kind switch
        {
            OptimizeActionKind.Transcode when compatibility || strategy == OptimizeStrategy.Video => Strings.OptimizeFileConverted,
            OptimizeActionKind.Transcode => Strings.OptimizeFileToOpus,
            OptimizeActionKind.Recompress => Strings.OptimizeFileRecompressed,
            OptimizeActionKind.Remove => Strings.OptimizeFileRemoved,
            OptimizeActionKind.MixDown => string.Format(CultureInfo.CurrentCulture, Strings.OptimizeFileMixdownFormat, output ?? string.Empty),
            OptimizeActionKind.CleanMetadata => Strings.OptimizeFileCleaned,
            _ => reason switch
            {
                OptimizeSkipReason.BelowMargin => Strings.OptimizeFileKeptBelowMargin,
                OptimizeSkipReason.WouldGrow => Strings.OptimizeFileKeptWouldGrow,
                OptimizeSkipReason.AlreadyOptimal => Strings.OptimizeFileKeptOptimal,
                OptimizeSkipReason.OptimizerUnavailable => Strings.OptimizeFileKeptUnavailable,
                OptimizeSkipReason.OutputNameTaken => string.Format(CultureInfo.CurrentCulture, Strings.OptimizeFileKeptNameTakenFormat, output ?? string.Empty),
                OptimizeSkipReason.NothingToClean => Strings.OptimizeFileNothingToClean,
                OptimizeSkipReason.EstimateFailed => Strings.OptimizeFileKeptUnreadable,
                OptimizeSkipReason.UnsupportedCodec => Strings.OptimizeFileKeptUnsupported,
                _ => Strings.OptimizeFileKept,
            },
        };
    }
}
