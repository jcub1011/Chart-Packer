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
using ChartShared.Core.Jobs;
using ChartShared.Core.Scanning;
using ChartShared.Core.Validation;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartPacker.ViewModels
{
    /// <summary>How a job item ended, as the progress window sorts and filters it: problems first.</summary>
    public enum JobOutcome
    {
        /// <summary>It failed, or the job was cancelled before it finished.</summary>
        Failed,

        Skipped,

        /// <summary>It was packed or unpacked, including with warnings or left-out files.</summary>
        Completed,
    }

    /// <summary>A warning or error under a job item.</summary>
    /// <param name="RuleId">The validation rule that raised it.</param>
    public sealed record JobMessageRow(string RuleId, string Message, bool IsError);

    /// <summary>One item's outcome in the results panel.</summary>
    /// <param name="Entry">The song the job read, which double-clicking the row shows in the library.</param>
    /// <param name="Detail">What happened, e.g. where it was written or why it was skipped.</param>
    /// <param name="Status">How it ended.</param>
    /// <param name="Messages">The item's warnings and errors. Info findings are left out.</param>
    /// <param name="IsCanceled">The job was cancelled before this item finished, so it has no result.</param>
    public sealed record JobItemRow(SongEntry Entry, string Detail, JobItemStatus Status, IReadOnlyList<JobMessageRow> Messages, bool IsCanceled = false)
    {
        /// <summary>The item's name.</summary>
        public string Name => Entry.DisplayName;

        public bool IsFailed => Status == JobItemStatus.Failed && !IsCanceled;

        /// <summary>Finished, but with something the user should look at.</summary>
        public bool IsAttention => Status is JobItemStatus.Skipped or JobItemStatus.SucceededWithWarnings or JobItemStatus.CompletedWithErrors;

        public bool HasMessages => Messages.Count > 0;

        public JobOutcome Outcome => IsCanceled ? JobOutcome.Failed : Status switch
        {
            JobItemStatus.Failed => JobOutcome.Failed,
            JobItemStatus.Skipped => JobOutcome.Skipped,
            _ => JobOutcome.Completed,
        };

        public string Glyph => IsCanceled ? "⊘" : Status switch
        {
            JobItemStatus.Succeeded => "✓",
            JobItemStatus.SucceededWithWarnings or JobItemStatus.CompletedWithErrors => "!",
            JobItemStatus.Skipped => "–",
            _ => "✕",
        };

        /// <summary>Whether the name, detail or any message contains <paramref name="query"/>, ignoring case.</summary>
        public bool Matches(string query) =>
            Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || Detail.Contains(query, StringComparison.OrdinalIgnoreCase)
            || Messages.Any(m => m.RuleId.Contains(query, StringComparison.OrdinalIgnoreCase)
                || m.Message.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A chip in the progress window that narrows its list to one outcome, e.g. Skipped, with how many items had it.
    /// </summary>
    /// <param name="outcome">The outcome it shows.</param>
    /// <param name="titleFormat">Its label's format; <c>{0}</c> is the count.</param>
    /// <param name="changed">Called when <see cref="IsOn"/> changes.</param>
    public sealed partial class JobOutcomeChip(JobOutcome outcome, string titleFormat, Action changed) : ObservableObject
    {
        public JobOutcome Outcome { get; } = outcome;

        /// <summary>How many items had this outcome, whatever the search.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Label), nameof(IsAttention), nameof(IsFailed))]
        public partial int Count { get; set; }

        /// <summary>The name and count, e.g. "Skipped (2)".</summary>
        public string Label => string.Format(CultureInfo.CurrentCulture, TitleFormat, Count);

        /// <summary>Items were skipped, so the label is coloured as a warning.</summary>
        public bool IsAttention => Outcome == JobOutcome.Skipped && Count > 0;

        /// <summary>Items failed or were cancelled, so the label is coloured as an error.</summary>
        public bool IsFailed => Outcome == JobOutcome.Failed && Count > 0;

        /// <summary>Only rows of this outcome are shown, along with those of any other chip that's on.</summary>
        [ObservableProperty]
        public partial bool IsOn { get; set; }

        internal string TitleFormat
        {
            get;
            set
            {
                field = value;
                OnPropertyChanged(nameof(Label));
            }
        } = titleFormat;

        partial void OnIsOnChanged(bool value) => changed();
    }

    /// <summary>
    /// The progress window's list: a row per item, problems first (failed, then skipped, then completed), each
    /// outcome's rows in the order they finished. A search and the outcome chips narrow it.
    /// </summary>
    public sealed partial class JobResultList : ViewModelBase
    {
        // Every row, indexed by outcome, in the order they finished.
        private readonly List<JobItemRow>[] _all = [[], [], []];

        // How many of each outcome's rows are in Rows, to know where the next one goes.
        private readonly int[] _shown = new int[3];

        // The chips, indexed by outcome.
        private readonly JobOutcomeChip[] _chips;

        public JobResultList()
        {
            Failed = new(JobOutcome.Failed, Strings.JobGroupFailedFormat, Rebuild);
            Skipped = new(JobOutcome.Skipped, Strings.JobGroupSkippedFormat, Rebuild);
            Completed = new(JobOutcome.Completed, Strings.JobGroupPackedFormat, Rebuild);
            _chips = [Failed, Skipped, Completed];
            Chips = [Completed, Skipped, Failed];
        }

        /// <summary>Items that were packed or unpacked, including those with warnings or left-out files.</summary>
        public JobOutcomeChip Completed { get; }

        public JobOutcomeChip Skipped { get; }

        /// <summary>Items that failed, and those a cancelled job never finished.</summary>
        public JobOutcomeChip Failed { get; }

        /// <summary>The chips in the order the window shows them.</summary>
        public IReadOnlyList<JobOutcomeChip> Chips { get; }

        /// <summary>The rows the search and the chips let through; every row while neither narrows it.</summary>
        public AvaloniaList<JobItemRow> Rows { get; } = [];

        [ObservableProperty]
        public partial string SearchText { get; set; } = "";

        public bool HasRows => Total > 0;

        public bool IsSearching => Query.Length > 0;

        /// <summary>The search or a chip may be leaving rows out.</summary>
        public bool IsFiltering => IsSearching || AnyChipOn;

        /// <summary>There are rows, but none get through the search and the chips.</summary>
        public bool HasNoMatches => HasRows && Rows.Count == 0;

        public string NoMatchesText => IsSearching ? Format(Strings.JobSearchNoMatchesFormat, Query) : Strings.JobFilterNoMatches;

        /// <summary>How many rows there are, e.g. "120 songs", or how many are shown of them, e.g. "3 of 120 songs".</summary>
        public string CountText => IsFiltering
            ? Format(Strings.JobSearchCountFormat, Rows.Count, Total)
            : Format(Strings.JobResultCountFormat, Total);

        private int Total => _all.Sum(rows => rows.Count);

        private bool AnyChipOn => _chips.Any(c => c.IsOn);

        private string Query => SearchText?.Trim() ?? "";

        internal void Add(JobItemRow row)
        {
            int outcome = (int)row.Outcome;
            _all[outcome].Add(row);
            _chips[outcome].Count++;
            if (IsShown(row))
            {
                // After the shown rows of its own outcome and those listed before it.
                int index = 0;
                for (int i = 0; i <= outcome; i++)
                    index += _shown[i];
                Rows.Insert(index, row);
                _shown[outcome]++;
            }
            Refresh();
        }

        internal void Clear()
        {
            foreach (List<JobItemRow> rows in _all)
                rows.Clear();
            foreach (JobOutcomeChip chip in _chips)
            {
                chip.Count = 0;
                chip.IsOn = false;
            }
            SearchText = "";
            Rebuild();
        }

        // Only while searching, so Escape in an empty search box still reaches the window's Close button.
        [RelayCommand(CanExecute = nameof(IsSearching))]
        private void ClearSearch() => SearchText = "";

        partial void OnSearchTextChanged(string value)
        {
            OnPropertyChanged(nameof(IsSearching));
            ClearSearchCommand.NotifyCanExecuteChanged();
            Rebuild();
        }

        private bool IsShown(JobItemRow row) =>
            row.Matches(Query) && (!AnyChipOn || _chips[(int)row.Outcome].IsOn);

        /// <summary>Lists again the rows the search and the chips let through, after either changes.</summary>
        private void Rebuild()
        {
            Rows.Clear();
            for (int i = 0; i < _all.Length; i++)
            {
                int before = Rows.Count;
                Rows.AddRange(_all[i].Where(IsShown));
                _shown[i] = Rows.Count - before;
            }
            Refresh();
        }

        private void Refresh()
        {
            OnPropertyChanged(nameof(HasRows));
            OnPropertyChanged(nameof(IsFiltering));
            OnPropertyChanged(nameof(HasNoMatches));
            OnPropertyChanged(nameof(NoMatchesText));
            OnPropertyChanged(nameof(CountText));
        }

        private static string Format(string format, params object[] args) => string.Format(CultureInfo.CurrentCulture, format, args);
    }

    /// <summary>
    /// The pack/unpack job: the summary line along the bottom of the window and the progress window it
    /// opens. It waits for a job, shows progress while a pack or unpack job runs, then a summary and one
    /// row per item, grouped by outcome, until the next job.
    /// </summary>
    public partial class JobViewModel(IJobProgressWindowService details) : ViewModelBase
    {
        private CancellationTokenSource? _cts;
        private JobKind _kind;
        private int _total;
        private int _succeeded;
        private int _skipped;
        private int _failed;
        private bool _cancelled;

        // A job has run (or is running) this session, so there's a summary to show.
        private bool _hasRun;

        /// <summary>A row per item the job has finished, or a cancelled job never finished.</summary>
        public JobResultList Results { get; } = new();

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
        public partial bool IsRunning { get; set; }

        private int Done => _succeeded + _skipped + _failed;

        /// <summary>Items finished, as a percentage.</summary>
        public double Progress => _total == 0 ? 0 : 100.0 * Done / _total;

        /// <summary>No job has run yet, so the panel only says it's waiting.</summary>
        public bool IsWaiting => !_hasRun;

        public string SummaryText
        {
            get
            {
                if (!_hasRun)
                    return Strings.JobWaiting;

                if (IsRunning)
                {
                    string running = _kind == JobKind.Pack ? Strings.JobPackingFormat : Strings.JobUnpackingFormat;
                    return Format(running, Math.Min(Done + 1, _total), _total);
                }

                string finished = (_kind, _cancelled) switch
                {
                    (JobKind.Pack, false) => Strings.JobPackSummaryFormat,
                    (JobKind.Pack, true) => Strings.JobPackCancelledFormat,
                    (_, false) => Strings.JobUnpackSummaryFormat,
                    (_, true) => Strings.JobUnpackCancelledFormat,
                };
                return Format(finished, _succeeded, _skipped, _failed);
            }
        }

        /// <summary>Runs a job over <paramref name="entries"/>. Only one job runs at a time.</summary>
        public async Task RunAsync(JobKind kind, IReadOnlyList<SongEntry> entries, string scanRoot, string destination, OverwritePolicy policy, OutputLayout layout)
        {
            if (IsRunning)
                return;

            using var cts = new CancellationTokenSource();
            _cts = cts;
            _kind = kind;
            _total = entries.Count;
            _succeeded = _skipped = _failed = 0;
            _cancelled = false;
            _hasRun = true;
            Results.Completed.TitleFormat = kind == JobKind.Pack ? Strings.JobGroupPackedFormat : Strings.JobGroupUnpackedFormat;
            Results.Clear();
            IsRunning = true;
            RefreshState();

            // Progress<T> posts to the UI thread in order, ahead of this method's continuation.
            var progress = new Progress<JobItemResult>(result => Add(kind, result));

            try
            {
                await Task.Run(() => SongJob.RunAsync(kind, entries, scanRoot, destination, policy, progress, layout, cancellationToken: cts.Token), cts.Token);
            }
            catch (OperationCanceledException)
            {
                _cancelled = true;
                AddCanceled(kind, entries);
            }
            finally
            {
                _cts = null;
                IsRunning = false;
                RefreshState();
            }
        }

        [RelayCommand(CanExecute = nameof(IsRunning))]
        private void Cancel() => _cts?.Cancel();

        [RelayCommand]
        private void ShowDetails() => details.Show(this);

        /// <summary>Asks for a song to be selected in the library, which LibraryViewModel does.</summary>
        public event EventHandler<SongEntry>? ShowInLibraryRequested;

        /// <summary>Selects <paramref name="row"/>'s song in the library, as double-clicking the row does.</summary>
        public void ShowInLibrary(JobItemRow row) => ShowInLibraryRequested?.Invoke(this, row.Entry);

        private void Add(JobKind kind, JobItemResult result)
        {
            switch (result.Status)
            {
                case JobItemStatus.Skipped:
                    _skipped++;
                    break;
                case JobItemStatus.Failed:
                    _failed++;
                    break;
                default:
                    _succeeded++;
                    break;
            }

            var messages = result.Findings
                .Where(f => f.Severity != Severity.Info)
                .Select(f => new JobMessageRow(f.RuleId, f.Message, f.Severity == Severity.Error))
                .ToList();

            Results.Add(new JobItemRow(result.Entry, Describe(kind, result), result.Status, messages));
            RefreshState();
        }

        /// <summary>
        /// Adds a row for each item the job didn't finish. Results arrive in entry order, one per finished
        /// item, so those are the ones after the first <see cref="Done"/>.
        /// </summary>
        private void AddCanceled(JobKind kind, IReadOnlyList<SongEntry> entries)
        {
            string detail = kind == JobKind.Pack ? Strings.JobCanceledPack : Strings.JobCanceledUnpack;
            foreach (SongEntry entry in entries.Skip(Done))
                Results.Add(new JobItemRow(entry, detail, JobItemStatus.Failed, [], IsCanceled: true));
        }

        private static string Describe(JobKind kind, JobItemResult result) => result.Status switch
        {
            JobItemStatus.Skipped => result.SkipReason switch
            {
                SkipReason.AlreadyPacked => Format(Strings.JobAlreadyPackedFormat, Path.GetFileName(result.Entry.FullPath)),
                SkipReason.AlreadyUnpacked => Format(Strings.JobAlreadyUnpackedFormat, Path.GetFileName(result.Entry.FullPath)),
                SkipReason.DuplicateOutput => Format(Strings.JobDuplicateOutputFormat, result.OutputPath ?? "", result.ConflictsWith?.RelativePath ?? ""),
                _ => Format(Strings.JobOutputExistsFormat, result.OutputPath ?? ""),
            },
            JobItemStatus.Failed when result.ErrorMessage is not null => Format(Strings.JobFailedFormat, result.ErrorMessage),
            JobItemStatus.Failed => kind == JobKind.Pack ? Strings.JobNotPacked : Strings.JobNotUnpacked,
            JobItemStatus.CompletedWithErrors => Format(Strings.JobUnpackedWithErrorsFormat, result.OutputPath ?? ""),
            _ => Format(kind == JobKind.Pack ? Strings.JobPackedFormat : Strings.JobUnpackedFormat, result.OutputPath ?? ""),
        };

        private static string Format(string format, params object[] args) => string.Format(CultureInfo.CurrentCulture, format, args);

        /// <summary>Raises change notifications for every derived display state.</summary>
        private void RefreshState()
        {
            OnPropertyChanged(nameof(Progress));
            OnPropertyChanged(nameof(SummaryText));
            OnPropertyChanged(nameof(IsWaiting));
        }
    }
}
