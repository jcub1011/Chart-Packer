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
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartPacker.ViewModels
{
    /// <summary>A warning or error under a job item.</summary>
    public sealed record JobMessageRow(string Text, bool IsError);

    /// <summary>One item's outcome in the results panel.</summary>
    /// <param name="Name">The item's name.</param>
    /// <param name="Detail">What happened, e.g. where it was written or why it was skipped.</param>
    /// <param name="Status">How it ended.</param>
    /// <param name="Messages">The item's warnings and errors. Info findings are left out.</param>
    public sealed record JobItemRow(string Name, string Detail, JobItemStatus Status, IReadOnlyList<JobMessageRow> Messages)
    {
        public bool IsFailed => Status == JobItemStatus.Failed;

        /// <summary>Finished, but with something the user should look at.</summary>
        public bool IsAttention => Status is JobItemStatus.Skipped or JobItemStatus.SucceededWithWarnings or JobItemStatus.CompletedWithErrors;

        public bool HasMessages => Messages.Count > 0;

        public string Glyph => Status switch
        {
            JobItemStatus.Succeeded => "✓",
            JobItemStatus.SucceededWithWarnings or JobItemStatus.CompletedWithErrors => "!",
            JobItemStatus.Skipped => "–",
            _ => "✕",
        };
    }

    /// <summary>
    /// The results panel along the bottom of the window. It is always shown: it waits for a job, shows
    /// progress while a pack or unpack job runs, then a summary and one row per item until the next job.
    /// </summary>
    /// <remarks>
    /// Only the user expands or collapses the item rows; a job starting leaves them as they were.
    /// </remarks>
    public partial class JobViewModel : ViewModelBase
    {
        private readonly SettingsService _settings;
        private CancellationTokenSource? _cts;
        private JobKind _kind;
        private int _total;
        private int _succeeded;
        private int _skipped;
        private int _failed;
        private bool _cancelled;

        // A job has run (or is running) this session, so there's a summary to show.
        private bool _hasRun;

        public JobViewModel(SettingsService settings)
        {
            _settings = settings;
            IsExpanded = settings.Settings.JobPanelExpanded;
        }

        public AvaloniaList<JobItemRow> Items { get; } = [];

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
        public partial bool IsRunning { get; set; }

        /// <summary>The item rows are shown, and the panel can be resized.</summary>
        [ObservableProperty]
        public partial bool IsExpanded { get; set; }

        private int Done => _succeeded + _skipped + _failed;

        /// <summary>Items finished, as a percentage.</summary>
        public double Progress => _total == 0 ? 0 : 100.0 * Done / _total;

        public string ExpandButtonText => IsExpanded ? Strings.CollapseButton : Strings.ExpandButton;

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
            Items.Clear();
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
        private void ToggleExpanded() => IsExpanded = !IsExpanded;

        partial void OnIsExpandedChanged(bool value)
        {
            _settings.Settings.JobPanelExpanded = value;
            OnPropertyChanged(nameof(ExpandButtonText));
        }

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
                .Select(f => new JobMessageRow($"{f.RuleId}: {f.Message}", f.Severity == Severity.Error))
                .ToList();

            Items.Add(new JobItemRow(result.Entry.DisplayName, Describe(kind, result), result.Status, messages));
            RefreshState();
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
