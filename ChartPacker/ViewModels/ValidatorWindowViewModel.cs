using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Threading;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Validation;
using ChartPacker.Core.Validation.QuickFixes;
using ChartPacker.Resources;
using ChartPacker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartPacker.ViewModels
{
    /// <summary>
    /// The Validator window: checks the selected songs' metadata, as drafted, lists the ones that fail, and shows the
    /// one picked in a full inspector beside the list, where it can be fixed and saved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The inspector shares the main window's drafts, so an edit made here is the same draft the main window shows,
    /// saved or not. Each listed song is checked again whenever its draft changes, and read again from disk once its
    /// draft is saved or discarded. A song that comes to pass stays listed, marked as passing, so the list doesn't
    /// move under the pointer.
    /// </para>
    /// <para>
    /// Issues with a quick fix can be ticked and fixed together. Fixes only go into the drafts; Save all changes
    /// writes every listed song's draft.
    /// </para>
    /// </remarks>
    public sealed partial class ValidatorWindowViewModel : ViewModelBase
    {
        private readonly IReadOnlyList<SongEntry> _entries;
        private readonly MetadataDraftStore _drafts;
        private readonly JobViewModel _job;
        private readonly DraftSaver _saver;
        private readonly IConfirmationService? _confirmation;
        private readonly int _parallelism;
        private readonly CancellationTokenSource _closing = new();

        // The listed songs, by full path.
        private readonly Dictionary<string, ValidatorSongViewModel> _byPath = new(StringComparer.Ordinal);

        // Set while many issues are ticked at once, so the counts are worked out once at the end.
        private bool _selectingAll;

        // A refresh of the counts is posted and not yet run, so a burst of songs read or fixed counts them once.
        private bool _refreshPending;

        // The counts, as last worked out: the listed songs that still fail, their issues with a quick fix, and how many of those are ticked.
        private int _failingCount;
        private int _fixableCount;
        private int _selectedFixCount;

        /// <param name="entries">The songs to check, in the order they're listed.</param>
        /// <param name="drafts">The main window's drafts, which the songs are checked as.</param>
        /// <param name="inspector">The inspector the picked song is shown in, sharing <paramref name="drafts"/>.</param>
        /// <param name="parallelism">How many songs are read at once.</param>
        /// <param name="job">The main window's job: nothing is saved while one runs.</param>
        /// <param name="saver">Writes the listed songs' drafts.</param>
        /// <param name="confirmation">Asks before saving metadata that still fails; none to save without asking.</param>
        public ValidatorWindowViewModel(
            IReadOnlyList<SongEntry> entries,
            MetadataDraftStore drafts,
            SongDetailViewModel inspector,
            int parallelism,
            JobViewModel job,
            DraftSaver saver,
            IConfirmationService? confirmation = null)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(parallelism, 1);
            _entries = entries;
            _drafts = drafts;
            _job = job;
            _saver = saver;
            _confirmation = confirmation;
            _parallelism = parallelism;
            Inspector = inspector;
            _drafts.DraftsChanged += OnDraftsChanged;
            _job.PropertyChanged += OnJobPropertyChanged;
            Inspector.PropertyChanged += OnInspectorPropertyChanged;
        }

        /// <summary>The picked song, editable and playable as in the main window.</summary>
        public SongDetailViewModel Inspector { get; }

        /// <summary>The songs that failed when checked, in selection order.</summary>
        public AvaloniaList<ValidatorSongViewModel> Songs { get; } = [];

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowPickPrompt), nameof(ShowInspector))]
        public partial ValidatorSongViewModel? SelectedSong { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusText), nameof(ShowAllPass))]
        public partial bool IsChecking { get; set; }

        /// <summary>How many songs have been read.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        public partial int Checked { get; set; }

        /// <summary>Save all changes is writing the listed songs; nothing can be edited meanwhile.</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(FixSelectedCommand), nameof(SaveAllCommand))]
        public partial bool IsSaving { get; set; }

        /// <summary>Which songs Save all changes couldn't save, and why.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSaveError))]
        public partial string? SaveError { get; set; }

        public bool HasSaveError => SaveError is not null;

        public int Total => _entries.Count;

        /// <summary>How many listed songs still fail.</summary>
        public int FailingCount => _failingCount;

        public string StatusText =>
            IsChecking ? string.Format(CultureInfo.CurrentCulture, Strings.ValidatorCheckingFormat, Checked, Total)
            : FailingCount == 0 ? string.Format(CultureInfo.CurrentCulture, Strings.ValidatorAllPassFormat, Total)
            : string.Format(CultureInfo.CurrentCulture, Strings.ValidatorSummaryFormat, FailingCount, Total);

        /// <summary>Every song was checked and none failed.</summary>
        public bool ShowAllPass => !IsChecking && Songs.Count == 0;

        public bool ShowInspector => SelectedSong is not null;

        public bool ShowPickPrompt => SelectedSong is null;

        /// <summary>Some listed issue has a quick fix.</summary>
        public bool HasFixes => _fixableCount > 0;

        /// <summary>How many ticked issues Fix selected would fix.</summary>
        public int SelectedFixCount => _selectedFixCount;

        public string FixSelectedText => string.Format(CultureInfo.CurrentCulture, Strings.ValidatorFixSelectedFormat, SelectedFixCount);

        /// <summary>
        /// Whether every issue with a quick fix is ticked: <see langword="null"/> when only some are. Setting it ticks
        /// or clears them all; clearing it while only some are ticked ticks them all, so a click on the partly ticked
        /// box selects everything first.
        /// </summary>
        public bool? AllFixesSelected
        {
            get => _selectedFixCount == 0 ? false : _selectedFixCount == _fixableCount ? true : null;
            set
            {
                bool select = value ?? true;
                if (value == false && AllFixesSelected is null)
                    select = true;

                _selectingAll = true;
                try
                {
                    foreach (var issue in FixableIssues)
                        issue.IsSelected = select;
                }
                finally
                {
                    _selectingAll = false;
                }

                RefreshSelection();
            }
        }

        private IEnumerable<ValidatorIssueViewModel> FixableIssues => Songs.SelectMany(s => s.Issues).Where(i => i.IsFixable);

        /// <summary>Reads and checks every song, listing the ones that fail as they're found.</summary>
        public async Task LoadAsync()
        {
            IsChecking = true;
            var options = new ParallelOptions { MaxDegreeOfParallelism = _parallelism, CancellationToken = _closing.Token };
            try
            {
                await Parallel.ForEachAsync(Enumerable.Range(0, _entries.Count), options, async (i, token) =>
                {
                    var report = await SongMetadataCheck.ReadAsync(_entries[i], token).ConfigureAwait(false);

                    // Drafts live on the UI thread, so the check against them is made there.
                    await Dispatcher.UIThread.InvokeAsync(() => Add(i, report));
                });
            }
            catch (OperationCanceledException)
            {
                return;
            }

            RefreshCounts();
            IsChecking = false;
        }

        /// <summary>Stops checking, lets go of the inspector's song and player, and stops following the drafts.</summary>
        public async Task ShutdownAsync()
        {
            _closing.Cancel();
            _drafts.DraftsChanged -= OnDraftsChanged;
            _job.PropertyChanged -= OnJobPropertyChanged;
            Inspector.PropertyChanged -= OnInspectorPropertyChanged;
            await Inspector.ShowAsync([]);
            Inspector.Detach();
            await Inspector.Player.WhenFilesClosedAsync();
            Inspector.Player.Dispose();
        }

        partial void OnSelectedSongChanged(ValidatorSongViewModel? value)
        {
            // While saving, the inspector holds no files open; it shows the song picked once the save is done.
            if (!IsSaving)
                _ = Inspector.ShowAsync(value is null ? [] : [value.Entry]);
        }

        // Drafts mustn't change while either this window or the inspector is writing them.
        private bool CanFixSelected() => !IsSaving && !Inspector.IsSaving && SelectedFixCount > 0;

        /// <summary>
        /// Applies the ticked issues' quick fixes to their songs' drafts, each worked out from the song as drafted
        /// now. Nothing is saved.
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanFixSelected))]
        private void FixSelected()
        {
            // Fixing a song checks it again, which replaces its issues, so the ticked ones are gathered first.
            var work = Songs
                .Select(s => (Song: s, Keys: s.Issues.Where(i => i.IsFixable && i.IsSelected).Select(i => i.Key).ToList()))
                .Where(w => w.Keys.Count > 0)
                .ToList();

            foreach (var (song, keys) in work)
            {
                var item = new MetadataItem(song.Report.Pairs, _drafts.Get(song.Path), song.Report.DuplicateKeys);
                foreach (string key in keys)
                    QuickFixRegistry.FixKey(item, key, includeHints: false);

                // Checks the song again, here and in any inspector showing it, which only has its rows to rebuild.
                _drafts.NotifyChanged(song.Path, DraftChange.Keys);
            }
        }

        /// <summary>The listed songs that can be saved and have something to save.</summary>
        private IEnumerable<ValidatorSongViewModel> SongsToSave => Songs.Where(s => s.Report.CanEdit && _drafts.HasDrafts(s.Path));

        private bool CanSaveAll() => !IsSaving && !Inspector.IsSaving && !_job.IsRunning && SongsToSave.Any();

        /// <summary>
        /// Writes the drafts of every listed song. Songs that fail keep their drafts. Songs whose metadata still fails
        /// validation are only saved once the user confirms it.
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanSaveAll))]
        private async Task SaveAllAsync()
        {
            int stillFailing = SongsToSave.Count(s => !s.IsFixed);
            if (stillFailing > 0 && _confirmation is not null)
            {
                string message = string.Format(CultureInfo.CurrentCulture, Strings.ValidatorSaveInvalidMessageFormat, stillFailing);
                if (!await _confirmation.ConfirmAsync(Strings.SaveInvalidTitle, message, Strings.SaveAnywayButton))
                    return;

                // The drafts, or the job, may have moved on while the dialog was open.
                if (!CanSaveAll())
                    return;
            }

            var targets = SongsToSave
                .Select(s => new SaveTarget(s.Entry, s.Report.SongIniPath, _drafts.Get(s.Path)))
                .ToList();

            IsSaving = true;
            SaveError = null;
            IReadOnlyList<string> failures;
            try
            {
                // The inspector's player and background video hold the picked song's files open, and mustn't open
                // them again, as for another song saved, until every song is saved.
                await Inspector.SuspendAsync();
                failures = await _saver.SaveAsync(targets, entry => _drafts.NotifyChanged(entry.FullPath, DraftChange.Saved));
            }
            finally
            {
                IsSaving = false;

                // The inspector let go of its song's files whether or not it was saved, so it's shown again, once.
                await Inspector.ResumeAsync(SelectedSong is null ? [] : [SelectedSong.Entry]);
            }

            if (failures.Count > 0)
                SaveError = string.Format(CultureInfo.CurrentCulture, Strings.SaveFailedFormat, string.Join(Environment.NewLine, failures));
        }

        private void Add(int index, SongMetadataReport report)
        {
            if (_closing.IsCancellationRequested)
                return;

            Checked++;
            var song = new ValidatorSongViewModel(index, report, OnIssueSelectionChanged);
            song.Check(DraftOf(report.Entry));
            if (song.IsFixed)
                return;

            // Reads finish in any order; the list keeps the selection's, which it's sorted by.
            int low = 0, high = Songs.Count;
            while (low < high)
            {
                int mid = (low + high) / 2;
                if (Songs[mid].Index < index)
                    low = mid + 1;
                else
                    high = mid;
            }

            Songs.Insert(low, song);
            _byPath[report.Entry.FullPath] = song;
            ScheduleRefreshCounts();
        }

        /// <summary>A listed song's draft changed, here or anywhere: it's checked again, from disk once the draft is gone.</summary>
        private void OnDraftsChanged(string path, DraftChange change)
        {
            if (!_byPath.TryGetValue(path, out var song))
                return;

            if (_drafts.HasDrafts(path))
            {
                song.Check(DraftOf(song.Entry));
                ScheduleRefreshCounts();
            }
            else
            {
                _ = RereadAsync(song);
            }
        }

        private async Task RereadAsync(ValidatorSongViewModel song)
        {
            try
            {
                var report = await SongMetadataCheck.ReadAsync(song.Entry, _closing.Token);
                song.Update(report, DraftOf(song.Entry));
                ScheduleRefreshCounts();
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void OnJobPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(JobViewModel.IsRunning))
                SaveAllCommand.NotifyCanExecuteChanged();
        }

        private void OnInspectorPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SongDetailViewModel.IsSaving))
            {
                FixSelectedCommand.NotifyCanExecuteChanged();
                SaveAllCommand.NotifyCanExecuteChanged();
            }
        }

        private void OnIssueSelectionChanged()
        {
            if (!_selectingAll)
                RefreshSelection();
        }

        private MetadataDraft? DraftOf(SongEntry entry) => _drafts.HasDrafts(entry.FullPath) ? _drafts.Get(entry.FullPath) : null;

        /// <summary>
        /// Counts the songs and issues again once the songs read or changed so far are in: each walks the whole list,
        /// so a burst of them, such as checking or fixing many songs, counts once rather than once per song.
        /// </summary>
        private void ScheduleRefreshCounts()
        {
            if (_refreshPending)
                return;

            _refreshPending = true;
            Dispatcher.UIThread.Post(RefreshCounts, DispatcherPriority.Background);
        }

        private void RefreshCounts()
        {
            _refreshPending = false;
            _failingCount = Songs.Count(s => !s.IsFixed);
            OnPropertyChanged(nameof(FailingCount));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(ShowAllPass));
            SaveAllCommand.NotifyCanExecuteChanged();
            RefreshSelection();
        }

        private void RefreshSelection()
        {
            int fixable = 0, selected = 0;
            foreach (var issue in FixableIssues)
            {
                fixable++;
                if (issue.IsSelected)
                    selected++;
            }

            _fixableCount = fixable;
            _selectedFixCount = selected;
            OnPropertyChanged(nameof(HasFixes));
            OnPropertyChanged(nameof(SelectedFixCount));
            OnPropertyChanged(nameof(FixSelectedText));
            OnPropertyChanged(nameof(AllFixesSelected));
            FixSelectedCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>A song in the Validator's list: what it's called, and which of its keys fail and why.</summary>
    public sealed partial class ValidatorSongViewModel : ObservableObject
    {
        // How many failing keys without a quick fix are spelled out; the rest are counted. Every fixable one is listed.
        private const int ListedIssues = 3;

        private readonly Action _selectionChanged;
        private SongMetadataReport _report;

        /// <param name="selectionChanged">Called when one of the song's issues is ticked or cleared.</param>
        public ValidatorSongViewModel(int index, SongMetadataReport report, Action selectionChanged)
        {
            Index = index;
            _report = report;
            _selectionChanged = selectionChanged;
            Title = report.Entry.DisplayName;
        }

        /// <summary>Where the song is in the selection.</summary>
        public int Index { get; }

        public SongEntry Entry => _report.Entry;

        public string Path => _report.Entry.FullPath;

        /// <summary>The metadata as last read from disk.</summary>
        internal SongMetadataReport Report => _report;

        /// <summary>"Name - Artist" as drafted, or the file or folder name.</summary>
        [ObservableProperty]
        public partial string Title { get; set; }

        /// <summary>The failing keys, each with why: every one with a quick fix, and the first few of the rest.</summary>
        [ObservableProperty]
        public partial IReadOnlyList<ValidatorIssueViewModel> Issues { get; set; } = [];

        /// <summary>How many failing keys aren't listed, as a line under the listed ones.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasMoreIssues))]
        public partial string? MoreIssuesText { get; set; }

        public bool HasMoreIssues => MoreIssuesText is not null;

        [ObservableProperty]
        public partial string? IssueCountText { get; set; }

        /// <summary>The song passes now: it was fixed since it was listed.</summary>
        [ObservableProperty]
        public partial bool IsFixed { get; set; }

        /// <summary>The metadata was read again with a new result, e.g. after saving.</summary>
        public void Update(SongMetadataReport report, MetadataDraft? draft)
        {
            _report = report;
            Check(draft);
        }

        /// <summary>
        /// Checks the metadata as <paramref name="draft"/> would save it. Issues ticked before stay ticked while they're
        /// still fixable. A song that can't be edited, such as a <c>.sng</c> with structural errors, has no fixes.
        /// </summary>
        public void Check(MetadataDraft? draft)
        {
            var item = new MetadataItem(_report.Pairs, draft ?? new MetadataDraft(), _report.DuplicateKeys);
            var pairs = item.Draft.ApplyTo(item.Original);
            var headline = SongHeadline.From(pairs);
            Title = headline switch
            {
                { Name: { } name, Artist: { } artist } => string.Format(CultureInfo.CurrentCulture, Strings.TitleFormat, name, artist),
                { Name: { } name } => name,
                _ => _report.Entry.DisplayName,
            };

            var selected = Issues.Where(i => i.IsSelected).Select(i => i.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (_report.ReadError is { } error)
            {
                Issues = [new ValidatorIssueViewModel("", string.Format(CultureInfo.CurrentCulture, Strings.ValidatorUnreadableFormat, error), null, false, _selectionChanged)];
                MoreIssuesText = null;
                IssueCountText = null;
                IsFixed = false;
                return;
            }

            // One line per failing key, its messages joined, with what its quick fixes do.
            var byKey = MetadataValidator.Validate(pairs, item.RemainingDuplicateKeys())
                .Where(MetadataValidator.IsFailing)
                .GroupBy(f => f.Key ?? "", StringComparer.OrdinalIgnoreCase)
                .ToList();

            var issues = new List<ValidatorIssueViewModel>();
            // Only a .sng with structural errors is read but can't be edited; the rest that can't couldn't be read.
            if (!_report.CanEdit && byKey.Count > 0)
            {
                string reason = string.Format(CultureInfo.CurrentCulture, Strings.SngHasErrorsFormat, _report.Entry.DisplayName);
                issues.Add(new ValidatorIssueViewModel("", reason, null, false, _selectionChanged));
            }

            int unfixableListed = 0, unlisted = 0;
            foreach (var group in byKey)
            {
                List<string> fixes = !_report.CanEdit ? [] : [.. group
                    .Select(f => QuickFixRegistry.Propose(f, pairs)?.Description)
                    .OfType<string>()
                    .Distinct(StringComparer.Ordinal)];
                string? fix = fixes.Count == 0 ? null : string.Join(", ", fixes);

                if (fix is null && unfixableListed == ListedIssues)
                {
                    unlisted++;
                    continue;
                }

                if (fix is null)
                    unfixableListed++;

                string text = string.Format(CultureInfo.CurrentCulture, Strings.ValidatorIssueLineFormat, group.Key,
                    string.Join(" ", group.Select(f => f.Message).Distinct(StringComparer.Ordinal)));
                issues.Add(new ValidatorIssueViewModel(group.Key, text, fix, fix is not null && selected.Contains(group.Key), _selectionChanged));
            }

            IsFixed = byKey.Count == 0;
            IssueCountText = IsFixed ? Strings.ValidatorFixed : string.Format(CultureInfo.CurrentCulture, Strings.ValidatorIssueCountFormat, byKey.Count);
            Issues = issues;
            MoreIssuesText = unlisted > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.ValidatorMoreIssuesFormat, unlisted) : null;
        }
    }

    /// <summary>A failing key of a song in the Validator's list, which can be ticked for Fix selected when it has a quick fix.</summary>
    public sealed partial class ValidatorIssueViewModel : ObservableObject
    {
        private readonly Action _selectionChanged;

        /// <param name="key">The failing key.</param>
        /// <param name="text">The key and why it fails.</param>
        /// <param name="fixDescription">What the key's quick fix does, or <see langword="null"/> without one.</param>
        /// <param name="isSelected">Whether it starts ticked.</param>
        /// <param name="selectionChanged">Called when it's ticked or cleared.</param>
        public ValidatorIssueViewModel(string key, string text, string? fixDescription, bool isSelected, Action selectionChanged)
        {
            Key = key;
            Text = text;
            FixDescription = fixDescription;
            IsSelected = isSelected;
            _selectionChanged = selectionChanged;
        }

        public string Key { get; }

        public string Text { get; }

        /// <summary>What the quick fix does, e.g. "Change to True"; <see langword="null"/> without one.</summary>
        public string? FixDescription { get; }

        public bool IsFixable => FixDescription is not null;

        /// <summary>The line under a fixable issue saying what fixing it does.</summary>
        public string? FixText => FixDescription is null ? null : string.Format(CultureInfo.CurrentCulture, Strings.ValidatorFixDescriptionFormat, FixDescription);

        /// <summary>Ticked for Fix selected.</summary>
        [ObservableProperty]
        public partial bool IsSelected { get; set; }

        // Null while the constructor sets the starting tick, which isn't a change to report.
        partial void OnIsSelectedChanged(bool value) => _selectionChanged?.Invoke();
    }
}
