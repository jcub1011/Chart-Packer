using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using ChartPacker.Core.Jobs;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Validation;
using ChartPacker.Resources;
using ChartPacker.Services;
using ChartPacker.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartPacker.ViewModels
{
    /// <summary>A warning or error under a job item.</summary>
    /// <param name="RuleId">The validation rule that raised it.</param>
    public sealed record JobMessageRow(string RuleId, string Message, bool IsError);

    /// <summary>One item's outcome in the results panel.</summary>
    /// <param name="Name">The item's name.</param>
    /// <param name="Detail">What happened, e.g. where it was written or why it was skipped.</param>
    /// <param name="Status">How it ended.</param>
    /// <param name="Messages">The item's warnings and errors. Info findings are left out.</param>
    /// <param name="IsCanceled">The job was cancelled before this item finished, so it has no result.</param>
    public sealed record JobItemRow(string Name, string Detail, JobItemStatus Status, IReadOnlyList<JobMessageRow> Messages, bool IsCanceled = false)
    {
        public bool IsFailed => Status == JobItemStatus.Failed && !IsCanceled;

        /// <summary>Finished, but with something the user should look at.</summary>
        public bool IsAttention => Status is JobItemStatus.Skipped or JobItemStatus.SucceededWithWarnings or JobItemStatus.CompletedWithErrors;

        public bool HasMessages => Messages.Count > 0;

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

    /// <summary>One tab of rows in the progress window, e.g. everything that was skipped, with its own search.</summary>
    /// <param name="titleFormat">The heading's format; <c>{0}</c> is the number of rows.</param>
    public sealed partial class JobResultGroup(string titleFormat) : ViewModelBase
    {
        private readonly List<JobItemRow> _all = [];

        /// <summary>The rows that match the search; every row while there's no search.</summary>
        public AvaloniaList<JobItemRow> Rows { get; } = [];

        [ObservableProperty]
        public partial string SearchText { get; set; } = "";

        /// <summary>The heading, with the number of rows, whatever the search.</summary>
        public string Title => Format(TitleFormat, _all.Count);

        public bool HasRows => _all.Count > 0;

        public bool IsSearching => Query.Length > 0;

        /// <summary>There are rows, but none match the search.</summary>
        public bool HasNoMatches => HasRows && Rows.Count == 0;

        public string NoMatchesText => Format(Strings.JobSearchNoMatchesFormat, Query);

        /// <summary>How many rows match, out of how many, e.g. "3 of 120".</summary>
        public string MatchCountText => Format(Strings.JobSearchCountFormat, Rows.Count, _all.Count);

        private string Query => SearchText?.Trim() ?? "";

        internal string TitleFormat
        {
            get;
            set
            {
                field = value;
                OnPropertyChanged(nameof(Title));
            }
        } = titleFormat;

        internal void Add(JobItemRow row)
        {
            _all.Add(row);
            if (row.Matches(Query))
                Rows.Add(row);
            Refresh();
        }

        internal void Clear()
        {
            _all.Clear();
            Rows.Clear();
            SearchText = "";
            Refresh();
        }

        // Only while searching, so Escape in an empty search box still reaches the window's Close button.
        [RelayCommand(CanExecute = nameof(IsSearching))]
        private void ClearSearch() => SearchText = "";

        partial void OnSearchTextChanged(string value)
        {
            string query = Query;
            Rows.Clear();
            Rows.AddRange(_all.Where(r => r.Matches(query)));
            OnPropertyChanged(nameof(IsSearching));
            ClearSearchCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(NoMatchesText));
            Refresh();
        }

        private void Refresh()
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(HasRows));
            OnPropertyChanged(nameof(HasNoMatches));
            OnPropertyChanged(nameof(MatchCountText));
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

        /// <summary>Items that were packed or unpacked, including those with warnings or left-out files.</summary>
        public JobResultGroup Completed { get; } = new(Strings.JobGroupPackedFormat);

        public JobResultGroup Skipped { get; } = new(Strings.JobGroupSkippedFormat);

        /// <summary>Items that failed, and those a cancelled job never finished.</summary>
        public JobResultGroup Failed { get; } = new(Strings.JobGroupFailedFormat);

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
            Completed.TitleFormat = kind == JobKind.Pack ? Strings.JobGroupPackedFormat : Strings.JobGroupUnpackedFormat;
            Completed.Clear();
            Skipped.Clear();
            Failed.Clear();
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

            var row = new JobItemRow(result.Entry.DisplayName, Describe(kind, result), result.Status, messages);
            JobResultGroup group = result.Status switch
            {
                JobItemStatus.Skipped => Skipped,
                JobItemStatus.Failed => Failed,
                _ => Completed,
            };
            group.Add(row);
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
                Failed.Add(new JobItemRow(entry.DisplayName, detail, JobItemStatus.Failed, [], IsCanceled: true));
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
