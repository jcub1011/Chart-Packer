using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using ChartPacker.Resources;
using ChartPacker.Services;
using ChartShared.Core.Dedup;
using ChartShared.Core.Scanning;
using ChartShared.Core.Sng;
using ChartShared.Core.Trash;
using ChartShared.UI.Formatting;
using ChartShared.UI.Resources;
using ChartShared.UI.Services;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartPacker.ViewModels
{
    /// <summary>A choice of which copy to keep by default, as offered in the keep picker.</summary>
    public sealed record DedupKeepOption(DedupKindPreference Value, string Label);

    /// <summary>What happened to one copy the window was asked to delete.</summary>
    public sealed record DedupResultRow(string Path, string Status, bool IsProblem);

    /// <summary>
    /// The Deduplicate window: compares every song in the library, then shows exact copies, copies
    /// of one chart and different charts of one song, and moves the copies the user marks to the trash.
    /// </summary>
    /// <remarks>
    /// Every group keeps one copy, which can't be marked. Copies are only deleted after the user
    /// confirms, and <see cref="DeletionExecutor"/> checks each one again first.
    /// </remarks>
    public sealed partial class DedupWindowViewModel : ViewModelBase
    {
        private readonly DedupRequest _request;
        private readonly Func<string, bool> _hasDrafts;

        // Whether Chart Builder has the song at a path open, so deleting it asks first (charter-spec §4.8).
        private readonly Func<string, bool>? _isOpenInChartBuilder;
        private readonly SettingsService _settings;
        private readonly IConfirmationService _confirmation;
        private readonly IMergeWindowService _merge;
        private readonly ITrash? _trash;
        private readonly DeletionLog _log;
        private readonly CancellationTokenSource _closing = new();
        private readonly List<string> _removed = [];
        private CancellationTokenSource? _deleteCts;
        private Task _running = Task.CompletedTask;

        // The songs with unsaved metadata edits, taken once the scan has found the songs.
        private IReadOnlySet<string> _drafted = new HashSet<string>(StringComparer.Ordinal);
        private DedupAnalysis? _analysis;
        private int _trashedCount;
        private int _permanentCount;
        private int _problemCount;

        // Set while a whole tab is marked, so the tab's count is taken once rather than for every copy.
        private bool _isMarking;

        /// <param name="request">How to get the songs to compare, and where a merge writes.</param>
        /// <param name="hasDrafts">Whether the song at a path has unsaved metadata edits; those are never deleted. UI thread only.</param>
        /// <param name="settings">Holds the keep preference.</param>
        /// <param name="confirmation">Asks before anything is deleted.</param>
        /// <param name="merge">Shows the Merge window over this one.</param>
        /// <param name="trash">Where deleted copies go; <see langword="null"/> when this OS has none, which turns deleting off.</param>
        /// <param name="log">Records every deletion.</param>
        public DedupWindowViewModel(
            DedupRequest request,
            Func<string, bool> hasDrafts,
            SettingsService settings,
            IConfirmationService confirmation,
            IMergeWindowService merge,
            ITrash? trash,
            DeletionLog log,
            Func<string, bool>? isOpenInChartBuilder = null)
        {
            _isOpenInChartBuilder = isOpenInChartBuilder;
            _request = request;
            _hasDrafts = hasDrafts;
            _settings = settings;
            _confirmation = confirmation;
            _merge = merge;
            _trash = trash;
            _log = log;
            DescribeGate = new SemaphoreSlim(request.Parallelism);

            KeepOptions =
            [
                new(DedupKindPreference.PreferSng, Strings.DedupKeepSng),
                new(DedupKindPreference.PreferFolder, Strings.DedupKeepFolder),
            ];
            SelectedKeepOption = KeepOptions.First(o => o.Value == settings.Settings.DedupKeepPreference);
            ExactGroups.CollectionChanged += (_, _) => RefreshHeaders();
            SameChartGroups.CollectionChanged += (_, _) => RefreshHeaders();
            MergeGroups.CollectionChanged += (_, _) => RefreshHeaders();
        }

        /// <summary>Asks the window to close.</summary>
        public event Action? CloseRequested;

        public IReadOnlyList<DedupKeepOption> KeepOptions { get; }

        [ObservableProperty]
        public partial DedupKeepOption SelectedKeepOption { get; set; }

        public DedupKindPreference Preference => SelectedKeepOption.Value;

        public AvaloniaList<DedupGroupViewModel> ExactGroups { get; } = [];

        public AvaloniaList<DedupGroupViewModel> SameChartGroups { get; } = [];

        public AvaloniaList<DedupGroupViewModel> MergeGroups { get; } = [];

        public string ExactHeader => string.Format(CultureInfo.CurrentCulture, Strings.DedupTabExactFormat, ExactGroups.Count);

        public string SameChartHeader => string.Format(CultureInfo.CurrentCulture, Strings.DedupTabSameChartFormat, SameChartGroups.Count);

        public string MergeHeader => string.Format(CultureInfo.CurrentCulture, Strings.DedupTabMergeFormat, MergeGroups.Count);

        public bool HasNoExact => IsLoaded && ExactGroups.Count == 0;

        public bool HasNoSameChart => IsLoaded && SameChartGroups.Count == 0;

        public bool HasNoMerge => IsLoaded && MergeGroups.Count == 0;

        /// <summary>Songs that couldn't be compared, one line each.</summary>
        public AvaloniaList<string> Skipped { get; } = [];

        public bool HasSkipped => Skipped.Count > 0;

        public string SkippedHeader => string.Format(CultureInfo.CurrentCulture, Strings.DedupSkippedFormat, Skipped.Count);

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsReady))]
        [NotifyPropertyChangedFor(nameof(IsLoaded))]
        [NotifyPropertyChangedFor(nameof(CloseLabel))]
        [NotifyPropertyChangedFor(nameof(HasNoExact))]
        [NotifyPropertyChangedFor(nameof(HasNoSameChart))]
        [NotifyPropertyChangedFor(nameof(HasNoMerge))]
        public partial bool IsAnalyzing { get; set; } = true;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsReady))]
        [NotifyPropertyChangedFor(nameof(CloseLabel))]
        public partial bool IsDeleting { get; set; }

        /// <summary>The comparison has finished.</summary>
        public bool IsLoaded => !IsAnalyzing && _analysis is not null;

        /// <summary>Copies can be marked, merged and deleted.</summary>
        public bool IsReady => IsLoaded && !IsDeleting;

        [ObservableProperty]
        public partial string ProgressText { get; set; } = "";

        [ObservableProperty]
        public partial double ProgressValue { get; set; }

        [ObservableProperty]
        public partial bool IsProgressIndeterminate { get; set; } = true;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasError))]
        public partial string? ErrorMessage { get; set; }

        public bool HasError => ErrorMessage is not null;

        /// <summary>Deleting is turned off because this OS has no trash the app can use.</summary>
        public bool TrashUnavailable => _trash is null;

        public bool CanDelete => _trash is not null;

        /// <summary>What the trash is called here, e.g. "Recycle Bin".</summary>
        public string TrashName => _trash?.Kind == TrashKind.RecycleBin ? Strings.DedupTrashRecycleBin : Strings.DedupTrashTrash;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DeleteMarkedLabel))]
        [NotifyCanExecuteChangedFor(nameof(DeleteExactCommand))]
        public partial int ExactMarkedCount { get; set; }

        public string DeleteMarkedLabel => string.Format(CultureInfo.CurrentCulture, Strings.DedupDeleteMarkedFormat, ExactMarkedCount);

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SameChartDeleteMarkedLabel))]
        [NotifyCanExecuteChangedFor(nameof(DeleteSameChartCommand))]
        public partial int SameChartMarkedCount { get; set; }

        public string SameChartDeleteMarkedLabel => string.Format(CultureInfo.CurrentCulture, Strings.DedupDeleteMarkedFilesFormat, SameChartMarkedCount);

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(MergeDeleteMarkedLabel))]
        [NotifyCanExecuteChangedFor(nameof(DeleteMergeCommand))]
        public partial int MergeMarkedCount { get; set; }

        public string MergeDeleteMarkedLabel => string.Format(CultureInfo.CurrentCulture, Strings.DedupDeleteMarkedFilesFormat, MergeMarkedCount);

        public AvaloniaList<DedupResultRow> Results { get; } = [];

        public bool HasResults => Results.Count > 0;

        [ObservableProperty]
        public partial string ResultSummary { get; set; } = "";

        public string CloseLabel => IsDeleting ? Strings.DedupStop : IsAnalyzing ? SharedStrings.CancelButton : Strings.CloseButton;

        /// <summary>What the window changed, once it's closed.</summary>
        public DedupWindowResult Result => new([.. _removed], OutputsChanged);

        private bool OutputsChanged { get; set; }

        /// <summary>How many Same chart comparisons read files at once.</summary>
        internal SemaphoreSlim DescribeGate { get; }

        /// <summary>Waits for the library scan, then compares the songs and builds the groups.</summary>
        public Task AnalyzeAsync()
        {
            _running = RunAnalysisAsync();
            return _running;
        }

        private async Task RunAnalysisAsync()
        {
            var progress = new Progress<DedupProgress>(ShowProgress);
            try
            {
                var entries = await _request.LoadEntries(new Progress<DedupScanProgress>(ShowScanProgress), _closing.Token);
                if (entries is null)
                {
                    ErrorMessage = Strings.DedupScanIncomplete;
                    return;
                }

                // Taken here, on the UI thread: the comparison asks from other threads.
                _drafted = entries.Select(e => e.FullPath).Where(_hasDrafts).ToHashSet(StringComparer.Ordinal);
                _analysis = await DedupAnalyzer.AnalyzeAsync(entries, _drafted.Contains, progress, _request.Parallelism, _closing.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Nothing is shown without the analysis, so whatever stopped it is shown instead of
                // an empty window, and it isn't left to surface when the window closes.
                ErrorMessage = string.Format(CultureInfo.CurrentCulture, Strings.DedupAnalysisFailedFormat, ex.Message);
                return;
            }
            finally
            {
                IsAnalyzing = false;
            }

            foreach (var variant in _analysis.Exact)
                ExactGroups.Add(new DedupGroupViewModel(this, DedupTier.Exact, variant.Entries.Select(e => new[] { new DedupVariant([e]) })));
            foreach (var chart in _analysis.SameChart)
                SameChartGroups.Add(new DedupGroupViewModel(this, DedupTier.SameChart, chart.Variants.Select(v => new[] { v })));
            foreach (var song in _analysis.MergeCandidates)
                MergeGroups.Add(new DedupGroupViewModel(this, DedupTier.MergeCandidate, song.Charts.Select(c => c.Variants)));
            foreach (var skipped in _analysis.Skipped)
                Skipped.Add(string.Format(CultureInfo.CurrentCulture, Strings.DedupSkippedLineFormat, skipped.Entry.RelativePath, Describe(skipped)));

            OnPropertyChanged(nameof(IsLoaded));
            OnPropertyChanged(nameof(IsReady));
            OnPropertyChanged(nameof(HasSkipped));
            OnPropertyChanged(nameof(SkippedHeader));
            RefreshHeaders();
            RefreshCommands();

            foreach (var group in SameChartGroups)
                _ = group.DescribeAsync(_analysis.Hasher, _closing.Token);
        }

        private void ShowScanProgress(DedupScanProgress progress)
        {
            // Nothing can say how far a scan has got while it's still finding songs.
            IsProgressIndeterminate = progress.IsSearching || progress.Found == 0;
            ProgressValue = progress.Found == 0 ? 0 : 100.0 * progress.Read / progress.Found;
            string format = progress.IsSearching ? Strings.DedupPhaseSearchingFormat : Strings.DedupPhaseScanningFormat;
            ProgressText = string.Format(CultureInfo.CurrentCulture, format, progress.Read, progress.Found);
        }

        private void ShowProgress(DedupProgress progress)
        {
            IsProgressIndeterminate = progress.ItemsTotal == 0;
            ProgressValue = progress.ItemsTotal == 0 ? 0 : 100.0 * progress.ItemsDone / progress.ItemsTotal;
            string read = ByteSizeFormatter.Format((ulong)progress.BytesHashed);
            ProgressText = progress.Phase switch
            {
                DedupPhase.Reading => string.Format(CultureInfo.CurrentCulture, Strings.DedupPhaseReadingFormat, progress.ItemsDone, progress.ItemsTotal),
                DedupPhase.MatchingCharts => string.Format(CultureInfo.CurrentCulture, Strings.DedupPhaseMatchingFormat, progress.ItemsDone, progress.ItemsTotal, read),
                _ => string.Format(CultureInfo.CurrentCulture, Strings.DedupPhaseComparingFormat, progress.ItemsDone, progress.ItemsTotal, read),
            };
        }

        partial void OnSelectedKeepOptionChanged(DedupKeepOption value)
        {
            _settings.Settings.DedupKeepPreference = value.Value;
            foreach (var group in ExactGroups.Concat(SameChartGroups).Concat(MergeGroups))
                group.ApplyDefaultKeeper();
        }

        partial void OnIsDeletingChanged(bool value) => RefreshCommands();

        /// <summary>Marks every copy on the Exact tab that isn't being kept.</summary>
        [RelayCommand(CanExecute = nameof(CanMarkAll))]
        private void MarkAll() => Mark(DedupTier.Exact, ExactGroups);

        private bool CanMarkAll() => IsReady && CanDelete && ExactGroups.Count > 0;

        /// <summary>Deletes every marked copy on the Exact tab.</summary>
        [RelayCommand(CanExecute = nameof(CanDeleteExact))]
        private Task DeleteExactAsync() => DeleteMarkedAsync(ExactGroups);

        private bool CanDeleteExact() => CanDeleteMarked(ExactMarkedCount);

        /// <summary>Marks every copy of a merged group on the Same chart tab.</summary>
        [RelayCommand(CanExecute = nameof(CanMarkMergedSameChart))]
        private void MarkMergedSameChart() => Mark(DedupTier.SameChart, SameChartGroups.Where(g => g.HasMerged));

        private bool CanMarkMergedSameChart() => CanMarkMerged(SameChartGroups);

        /// <summary>Deletes every marked copy on the Same chart tab.</summary>
        [RelayCommand(CanExecute = nameof(CanDeleteSameChart))]
        private Task DeleteSameChartAsync() => DeleteMarkedAsync(SameChartGroups);

        private bool CanDeleteSameChart() => CanDeleteMarked(SameChartMarkedCount);

        /// <summary>Marks every source of a merged group on the Merge tab.</summary>
        [RelayCommand(CanExecute = nameof(CanMarkMergedMerge))]
        private void MarkMergedMerge() => Mark(DedupTier.MergeCandidate, MergeGroups.Where(g => g.HasMerged));

        private bool CanMarkMergedMerge() => CanMarkMerged(MergeGroups);

        /// <summary>Deletes every marked source on the Merge tab.</summary>
        [RelayCommand(CanExecute = nameof(CanDeleteMerge))]
        private Task DeleteMergeAsync() => DeleteMarkedAsync(MergeGroups);

        private bool CanDeleteMerge() => CanDeleteMarked(MergeMarkedCount);

        /// <summary>Marks every copy in <paramref name="groups"/>, all on <paramref name="tier"/>'s tab, that can be; the tab's count follows once, after.</summary>
        private void Mark(DedupTier tier, IEnumerable<DedupGroupViewModel> groups)
        {
            _isMarking = true;
            try
            {
                foreach (var member in groups.SelectMany(g => g.Members).Where(m => m.CanMark))
                    member.IsMarked = true;
            }
            finally
            {
                _isMarking = false;
            }

            OnMarksChanged(tier);
        }

        private bool CanMarkMerged(IEnumerable<DedupGroupViewModel> groups) => IsReady && CanDelete && groups.Any(g => g.HasMerged);

        private bool CanDeleteMarked(int markedCount) => IsReady && CanDelete && markedCount > 0;

        private Task DeleteMarkedAsync(IEnumerable<DedupGroupViewModel> groups) => DeleteAsync([.. groups.Where(g => g.MarkedCount > 0).SelectMany(g => g.PlanDeletion())]);

        /// <summary>Deletes the marked copies of one group.</summary>
        internal Task DeleteGroupAsync(DedupGroupViewModel group) => DeleteAsync(group.PlanDeletion());

        /// <summary>Opens the Merge window for a group's charts, one copy of each.</summary>
        internal async Task MergeAsync(DedupGroupViewModel group)
        {
            ErrorMessage = null;

            // Taken now: what the merge reads is what its sources are, whatever the rows show later.
            var sources = group.Members.Select(m => m.Representative).ToList();
            string? path = await _merge.ShowAsync([.. sources.Select(s => s.Entry)], _request.OutputFolder, _request.Policy);
            if (path is null)
                return;

            OutputsChanged = true;

            // With the Overwrite policy the merge may have replaced a song that was compared.
            RemoveEverywhere(path);

            try
            {
                var read = SngReader.Read(path);
                if (read.HasErrors || EntryStamps.Capture(path, SongEntryKind.SngFile) is not { } stamp)
                {
                    ErrorMessage = string.Format(CultureInfo.CurrentCulture, Strings.DedupMergeCheckFailedFormat, read.Findings.FirstOrDefault()?.Message ?? path);
                    return;
                }

                group.SetMergedOutput(new DeletionKeeper(path, SongEntryKind.SngFile, stamp), sources);
                if (group.Members.Count == 0)
                {
                    foreach (var groups in new[] { ExactGroups, SameChartGroups, MergeGroups })
                        groups.Remove(group);
                }

                RefreshCommands();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ErrorMessage = string.Format(CultureInfo.CurrentCulture, Strings.DedupMergeCheckFailedFormat, ex.Message);
            }
        }

        private async Task DeleteAsync(IReadOnlyList<DeletionItem> items)
        {
            if (_trash is not { } trash || items.Count == 0 || !IsReady)
                return;

            ErrorMessage = null;
            try
            {
                DeletionPlan.Validate(items, _drafted.Contains);
            }
            catch (InvalidOperationException ex)
            {
                ErrorMessage = string.Format(CultureInfo.CurrentCulture, Strings.DedupPlanInvalidFormat, ex.Message);
                return;
            }

            string size = ByteSizeFormatter.Format((ulong)items.Sum(i => i.Bytes));
            string message = string.Format(CultureInfo.CurrentCulture, Strings.DedupConfirmFormat, items.Count, size, TrashName);
            string button = string.Format(CultureInfo.CurrentCulture, Strings.DedupConfirmButtonFormat, TrashName);
            if (!await _confirmation.ConfirmAsync(Strings.DedupConfirmTitle, message, button))
                return;

            if (!await ChartBuilderSongs.ConfirmWriteAsync(
                    _confirmation, _isOpenInChartBuilder, [.. items.Select(i => i.Victim)],
                    Strings.ChartBuilderOpenDeleteOneFormat, Strings.ChartBuilderOpenDeleteManyFormat, Strings.DeleteAnywayButton))
            {
                return;
            }

            _deleteCts?.Dispose();
            _deleteCts = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
            IsDeleting = true;
            var progress = new Progress<DeletionOutcome>(ShowOutcome);
            var run = DeletionExecutor.RunAsync(items, trash, _log, _drafted.Contains, progress, _deleteCts.Token);
            _running = run;
            try
            {
                await run;
            }
            catch (OperationCanceledException)
            {
            }
            catch (InvalidOperationException ex)
            {
                ErrorMessage = string.Format(CultureInfo.CurrentCulture, Strings.DedupPlanInvalidFormat, ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ErrorMessage = string.Format(CultureInfo.CurrentCulture, Strings.DedupLogFailedFormat, ex.Message);
            }
            finally
            {
                IsDeleting = false;
                UpdateSummary();
            }
        }

        private void ShowOutcome(DeletionOutcome outcome)
        {
            var (status, problem) = outcome.Status switch
            {
                DeletionStatus.Trashed => (string.Format(CultureInfo.CurrentCulture, Strings.DedupResultTrashedFormat, TrashName), false),
                DeletionStatus.DeletedPermanently => (Strings.DedupResultPermanent, false),
                DeletionStatus.Failed => (string.Format(CultureInfo.CurrentCulture, Strings.DedupResultFailedFormat, outcome.Error), true),
                _ => (Describe(outcome.Reason), true),
            };
            Results.Insert(0, new DedupResultRow(outcome.Item.Victim.RelativePath, status, problem));
            switch (outcome.Status)
            {
                case DeletionStatus.Trashed:
                    _trashedCount++;
                    break;
                case DeletionStatus.DeletedPermanently:
                    _permanentCount++;
                    break;
                default:
                    _problemCount++;
                    break;
            }

            OnPropertyChanged(nameof(HasResults));

            if (outcome.Status is DeletionStatus.Trashed or DeletionStatus.DeletedPermanently)
            {
                _removed.Add(outcome.Item.Victim.FullPath);
                RemoveEverywhere(outcome.Item.Victim.FullPath);
            }

            UpdateSummary();
        }

        private void UpdateSummary()
        {
            if (HasResults)
                ResultSummary = string.Format(CultureInfo.CurrentCulture, Strings.DedupResultSummaryFormat, _trashedCount, TrashName, _permanentCount, _problemCount);
        }

        /// <summary>Takes a song out of every group, dropping groups left with nothing to compare.</summary>
        private void RemoveEverywhere(string path)
        {
            foreach (var groups in new[] { ExactGroups, SameChartGroups, MergeGroups })
            {
                foreach (var group in groups.ToList())
                {
                    if (group.RemoveEntry(path))
                        groups.Remove(group);
                }
            }

            OnMarksChanged(DedupTier.Exact);
            OnMarksChanged(DedupTier.SameChart);
            OnMarksChanged(DedupTier.MergeCandidate);
        }

        /// <summary>
        /// A mark changed on <paramref name="tier"/>'s tab; its count follows. While <see cref="Mark"/> marks a whole tab,
        /// it's counted once at the end rather than for each copy.
        /// </summary>
        internal void OnMarksChanged(DedupTier tier)
        {
            if (_isMarking)
                return;

            switch (tier)
            {
                case DedupTier.Exact:
                    ExactMarkedCount = ExactGroups.Sum(g => g.MarkedCount);
                    break;
                case DedupTier.SameChart:
                    SameChartMarkedCount = SameChartGroups.Sum(g => g.MarkedCount);
                    break;
                case DedupTier.MergeCandidate:
                    MergeMarkedCount = MergeGroups.Sum(g => g.MarkedCount);
                    break;
            }
        }

        /// <summary>Stops a deletion after the copy it's on; otherwise closes the window.</summary>
        [RelayCommand]
        private void Close()
        {
            if (IsDeleting)
            {
                _deleteCts?.Cancel();
                return;
            }

            _closing.Cancel();
            CloseRequested?.Invoke();
        }

        /// <summary>The window is closing some other way; stops the comparison, and any deletion after the copy it's on.</summary>
        public void OnClosed() => _closing.Cancel();

        /// <summary>Once the window has closed: waits for a deletion that was stopping to finish its last copy.</summary>
        public async Task ShutdownAsync()
        {
            OnClosed();
            try
            {
                await _running;
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
            }
        }

        private void RefreshHeaders()
        {
            OnPropertyChanged(nameof(ExactHeader));
            OnPropertyChanged(nameof(SameChartHeader));
            OnPropertyChanged(nameof(MergeHeader));
            OnPropertyChanged(nameof(HasNoExact));
            OnPropertyChanged(nameof(HasNoSameChart));
            OnPropertyChanged(nameof(HasNoMerge));
            MarkAllCommand.NotifyCanExecuteChanged();
            MarkMergedSameChartCommand.NotifyCanExecuteChanged();
            MarkMergedMergeCommand.NotifyCanExecuteChanged();
        }

        private void RefreshCommands()
        {
            MarkAllCommand.NotifyCanExecuteChanged();
            DeleteExactCommand.NotifyCanExecuteChanged();
            MarkMergedSameChartCommand.NotifyCanExecuteChanged();
            DeleteSameChartCommand.NotifyCanExecuteChanged();
            MarkMergedMergeCommand.NotifyCanExecuteChanged();
            DeleteMergeCommand.NotifyCanExecuteChanged();
            foreach (var group in ExactGroups.Concat(SameChartGroups).Concat(MergeGroups))
                group.RefreshCommands();
        }

        private static string Describe(DedupSkipped skipped) => skipped.Reason switch
        {
            DedupSkipReason.SngHasErrors => Strings.DedupSkipDamaged,
            DedupSkipReason.SongIniTooLarge => Strings.DedupSkipIniTooLarge,
            DedupSkipReason.ChangedDuringAnalysis => Strings.DedupSkipChanged,
            _ => string.Format(CultureInfo.CurrentCulture, Strings.DedupSkipUnreadableFormat, skipped.Detail),
        };

        private static string Describe(DeletionSkipReason reason) => reason switch
        {
            DeletionSkipReason.KeeperMissing => Strings.DedupResultKeeperMissing,
            DeletionSkipReason.KeeperChanged => Strings.DedupResultKeeperChanged,
            DeletionSkipReason.VictimMissing => Strings.DedupResultVictimMissing,
            DeletionSkipReason.VictimChanged => Strings.DedupResultVictimChanged,
            DeletionSkipReason.HasUnsavedChanges => Strings.DedupResultUnsaved,
            DeletionSkipReason.DeclinedPermanentDelete => Strings.DedupResultDeclined,
            _ => Strings.DedupResultVictimChanged,
        };
    }
}
