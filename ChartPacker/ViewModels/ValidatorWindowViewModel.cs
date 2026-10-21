using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Threading;
using ChartPacker.Resources;
using ChartPacker.Services;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charting.Saving;
using ChartShared.Core.Charts;
using ChartShared.Core.Metadata;
using ChartShared.Core.Scanning;
using ChartShared.Core.Text;
using ChartShared.Core.Validation;
using ChartShared.Core.Validation.QuickFixes;
using ChartShared.UI.Services;
using ChartShared.UI.ViewModels;
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
    /// The songs' charts are linted too (charter-spec C16 item 5), on the same workers, with each song's <c>song.ini</c> as
    /// drafted, and its chart as drafted. Chart findings are listed after a song's metadata issues, and never stop a song
    /// being packed. A song is listed for a chart Warning or Error, not for chart Info alone. Two have quick fixes here:
    /// an unrated part (CHR083) is given the difficulty estimator's rating, and a part's missing difficulties (CHR087)
    /// are generated from those above them, by the rules the Generate difficulties dialog is left with.
    /// </para>
    /// <para>
    /// Issues with a quick fix can be ticked and fixed together. Fixes only go into the drafts; Save all changes
    /// writes every listed song's draft.
    /// </para>
    /// <para>
    /// The filter narrows the issues shown to those with or without a quick fix, of some types, and of some keys;
    /// only the types and keys that fail in the listed songs are offered. A song shows
    /// while one of its issues does. Ticking every fix, and Fix selected, only reach the issues shown; ticks on hidden
    /// ones are kept for when they show again. A song shown stays shown when it's checked again, as one that comes to
    /// pass does, until the filter changes.
    /// </para>
    /// </remarks>
    public sealed partial class ValidatorWindowViewModel : ViewModelBase
    {
        private readonly IReadOnlyList<SongEntry> _entries;
        private readonly MetadataDraftStore _drafts;
        private readonly JobViewModel _job;
        private readonly DraftSaver _saver;
        private readonly IConfirmationService? _confirmation;
        private readonly IGenerateDifficultiesDialog? _generate;
        private readonly GeneratedChartStash? _stash;

        // The songs whose difficulties are being generated and not yet staged, and those of them whose drafts changed
        // meanwhile, here or anywhere: what was generated for those was worked out from drafts that are gone.
        private readonly HashSet<string> _generating = new(StringComparer.Ordinal);
        private readonly HashSet<string> _changedWhileGenerating = new(StringComparer.Ordinal);

        // Holds the songs saved, so none another Chart Builder or Chart Packer has open is written over (charter-spec §4.8).
        private readonly SongLocks? _locks;
        private readonly SettingsService _settings;
        private readonly int _parallelism;
        private readonly CancellationTokenSource _closing = new();

        // Settles once the check lists its first failing song, or finishes or stops without one.
        private readonly TaskCompletionSource<bool> _firstFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // The listed songs, by full path.
        private readonly Dictionary<string, ValidatorSongViewModel> _byPath = new(StringComparer.Ordinal);

        // The songs with a draft when the check started, whose charts are linted once they're read, with the draft.
        private readonly HashSet<string> _draftedAtStart = new(StringComparer.Ordinal);

        // The songs in ShownSongs, to look up without walking it.
        private readonly HashSet<ValidatorSongViewModel> _shown = [];

        // The IDs of the type chips that are on.
        private readonly HashSet<string> _typesOn = new(StringComparer.Ordinal);

        // The keys of the key chips that are on; keys are compared ignoring case, as song.ini does.
        private readonly HashSet<string> _keysOn = new(StringComparer.OrdinalIgnoreCase);

        // Set while the search is cleared and every chip is turned off at once, so the list is filtered once at the end.
        private bool _clearingFilters;

        // Set while many issues are ticked at once, so the counts are worked out once at the end.
        private bool _selectingAll;

        // A refresh of the counts is posted and not yet run, so a burst of songs changed or fixed counts them once.
        private bool _refreshPending;

        // A refresh of the counts after songs the check read is waiting out CheckingCountsDelay.
        private bool _checkRefreshPending;

        // The search, trimmed, as songs are matched against it; empty for none.
        private string _query = "";

        // The song the inspector shows, which a song let go of and given back while the shown list is replaced leaves alone.
        private ValidatorSongViewModel? _inspected;

        // How long after a song's draft changes its chart is linted again, so a burst of edits lints once.
        private static readonly TimeSpan RelintDelay = TimeSpan.FromMilliseconds(300);

        // Songs read and checked by the workers, waiting to be taken in on the UI thread a batch at a time.
        private readonly UiBatchQueue<ReadSong> _read;

        // How often they're taken in while checking: often enough to look live, seldom enough that the window lays
        // itself out and draws a few times a second rather than every frame.
        private static readonly TimeSpan TakeReadInterval = TimeSpan.FromMilliseconds(100);

        // How long one batch may hold the UI thread; what's left goes in the next, after input and drawing.
        private static readonly TimeSpan TakeReadBudget = TimeSpan.FromMilliseconds(30);

        // How many songs read may wait to be taken in before the workers wait for the UI thread to catch up.
        private const int ReadQueueCapacity = 1024;

        // How long the search waits for typing to stop before the list is filtered.
        private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(200);

        // Filters the list once typing in the search stops.
        private readonly DispatcherTimer _searchTimer;

        // How many workers lint and check at once, however many read: one core is left to the UI thread, as with
        // every core busy linting the window stutters. Reading isn't held to it, so a slow drive is read as widely as
        // the library scan reads it.
        private readonly SemaphoreSlim _checkGate = new(Math.Max(Environment.ProcessorCount - 1, 1));

        // While checking, the counts walk a list still growing, so after songs read they're worked out this often at most.
        private static readonly TimeSpan CheckingCountsDelay = TimeSpan.FromMilliseconds(250);

        // More runs of songs shown or hidden at once than this, and the list is replaced in one go rather than run by
        // run: each run is a change the view hears of.
        private const int ReplaceShownThreshold = 64;

        // Set while the shown list is replaced, when the view lets go of the picked song and is given it back.
        private bool _replacingShown;

        /// <summary>A song a worker read, with its chart's findings and, when it had no draft, what checking it found.</summary>
        private sealed record ReadSong(int Index, SongMetadataReport Report, SongChartLintReport? Lint, ValidatorSongCheck? Check);

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
        /// <param name="settings">Holds where the window's splitter was left.</param>
        /// <param name="confirmation">Asks before saving metadata that still fails; none to save without asking.</param>
        /// <param name="locks">Holds the songs while they're saved; one open elsewhere stops the save.</param>
        /// <param name="generate">Asks for the rules missing difficulties are generated by; none to offer no generation.</param>
        /// <param name="stash">Keeps the charts generated until their songs are saved; none to offer no generation.</param>
        public ValidatorWindowViewModel(
            IReadOnlyList<SongEntry> entries,
            MetadataDraftStore drafts,
            SongDetailViewModel inspector,
            int parallelism,
            JobViewModel job,
            DraftSaver saver,
            SettingsService settings,
            IConfirmationService? confirmation = null,
            SongLocks? locks = null,
            IGenerateDifficultiesDialog? generate = null,
            GeneratedChartStash? stash = null)
        {
            _locks = locks;
            _generate = generate;
            _stash = stash;
            ArgumentOutOfRangeException.ThrowIfLessThan(parallelism, 1);
            _entries = entries;
            _drafts = drafts;
            _job = job;
            _saver = saver;
            _settings = settings;
            _confirmation = confirmation;
            _parallelism = parallelism;
            _read = new UiBatchQueue<ReadSong>(TakeReadInterval, TakeRead, TakeReadBudget, ReadQueueCapacity);
            _searchTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = SearchDelay };
            _searchTimer.Tick += (_, _) => ApplySearch();
            Inspector = inspector;
            QuickFixChip = new CountedFilterChipViewModel("quick", Strings.ValidatorFilterQuickFix, Strings.ValidatorFilterChipToolTipFormat, OnFilterChanged);
            ManualChip = new CountedFilterChipViewModel("manual", Strings.ValidatorFilterManual, Strings.ValidatorFilterChipToolTipFormat, OnFilterChanged);
            FixChips = [QuickFixChip, ManualChip];
            _drafts.DraftsChanged += OnDraftsChanged;
            _job.PropertyChanged += OnJobPropertyChanged;
            Inspector.PropertyChanged += OnInspectorPropertyChanged;
        }

        /// <summary>The picked song, editable and playable as in the main window.</summary>
        public SongDetailViewModel Inspector { get; }

        /// <summary>Share of the window's width given to the song list. The view applies it on open and writes it back on close.</summary>
        public double ValidatorListFraction
        {
            get => _settings.Settings.ValidatorListFraction;
            set => _settings.Settings.ValidatorListFraction = value;
        }

        /// <summary>
        /// Completes with <see langword="true"/> once the check lists its first failing song, or with
        /// <see langword="false"/> when it finishes, or is stopped, with none listed. Faults when the check breaks off
        /// on an error with none listed.
        /// </summary>
        public Task<bool> FirstFailureFound => _firstFailure.Task;

        /// <summary>The songs that failed when checked, in selection order.</summary>
        public AvaloniaList<ValidatorSongViewModel> Songs { get; } = [];

        /// <summary>The listed songs the filter shows, in selection order.</summary>
        public AvaloniaList<ValidatorSongViewModel> ShownSongs { get; } = [];

        /// <summary>Shows only issues with a quick fix (with <see cref="ManualChip"/>, every issue).</summary>
        public CountedFilterChipViewModel QuickFixChip { get; }

        /// <summary>Shows only issues without a quick fix (with <see cref="QuickFixChip"/>, every issue).</summary>
        public CountedFilterChipViewModel ManualChip { get; }

        /// <summary>The Quick fix and Manual fix chips.</summary>
        public IReadOnlyList<CountedFilterChipViewModel> FixChips { get; }

        /// <summary>A chip for each type of issue the listed songs have, and any turned on since they stopped having it.</summary>
        public AvaloniaList<CountedFilterChipViewModel> TypeChips { get; } = [];

        public bool HasTypeChips => TypeChips.Count > 0;

        /// <summary>A chip for each metadata key that fails in a listed song, and any turned on since it stopped failing.</summary>
        public AvaloniaList<CountedFilterChipViewModel> KeyChips { get; } = [];

        public bool HasKeyChips => KeyChips.Count > 0;

        /// <summary>How many chips are on, for the badge on the filter button.</summary>
        public int ActiveFilterCount => FixChips.Count(c => c.IsOn) + _typesOn.Count + _keysOn.Count;

        public bool HasActiveFilters => ActiveFilterCount > 0;

        /// <summary>Narrows the list to the songs whose title, artist, charter or path contains it.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSearch), nameof(IsNarrowed))]
        [NotifyCanExecuteChangedFor(nameof(ClearSearchCommand), nameof(ClearFiltersCommand))]
        public partial string? SearchText { get; set; }

        public bool HasSearch => !string.IsNullOrWhiteSpace(SearchText);

        /// <summary>The search or a chip leaves some listed songs out.</summary>
        public bool IsNarrowed => HasActiveFilters || HasSearch;

        /// <summary>How many of the listed songs the search and filter show.</summary>
        public string ShownSummaryText => string.Format(CultureInfo.CurrentCulture, Strings.ValidatorShownSummaryFormat, ShownSongs.Count, Songs.Count);

        /// <summary>Songs are listed but the search and filter show none of them.</summary>
        public bool ShowNoMatches => IsNarrowed && ShownSongs.Count == 0 && Songs.Count > 0;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowPickPrompt), nameof(ShowInspector))]
        public partial ValidatorSongViewModel? SelectedSong { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusText), nameof(AllPass), nameof(HasFailing), nameof(IsBusy))]
        public partial bool IsChecking { get; set; }

        /// <summary>Fix selected is generating missing difficulties; nothing else is fixed or saved meanwhile.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusText), nameof(AllPass), nameof(HasFailing), nameof(IsBusy))]
        [NotifyCanExecuteChangedFor(nameof(FixSelectedCommand), nameof(SaveAllCommand))]
        public partial bool IsFixing { get; set; }

        /// <summary>How many of the songs being generated are done.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        public partial int FixedCount { get; set; }

        /// <summary>How many songs are being generated.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        public partial int ToFixCount { get; set; }

        /// <summary>Checking or generating is going on, which the footer's spinner shows.</summary>
        public bool IsBusy => IsChecking || IsFixing;

        /// <summary>Which songs Fix selected couldn't generate difficulties for, and why.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasFixError))]
        public partial string? FixError { get; set; }

        public bool HasFixError => FixError is not null;

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

        /// <summary>The check is done and every listed song passes now.</summary>
        public bool AllPass => !IsBusy && FailingCount == 0;

        /// <summary>The check is done and some listed songs still fail.</summary>
        public bool HasFailing => !IsBusy && FailingCount > 0;

        public string StatusText =>
            IsFixing ? string.Format(CultureInfo.CurrentCulture, Strings.ValidatorGeneratingFormat, FixedCount, ToFixCount)
            : IsChecking ? string.Format(CultureInfo.CurrentCulture, Strings.ValidatorCheckingFormat, Checked, Total)
            : FailingCount == 0 ? string.Format(CultureInfo.CurrentCulture, Strings.ValidatorAllPassFormat, Total)
            : string.Format(CultureInfo.CurrentCulture, Strings.ValidatorSummaryFormat, FailingCount, Total);

        public bool ShowInspector => _inspected is not null;

        public bool ShowPickPrompt => _inspected is null;

        /// <summary>Some issue shown has a quick fix.</summary>
        public bool HasFixes => _fixableCount > 0;

        /// <summary>How many ticked issues Fix selected would fix.</summary>
        public int SelectedFixCount => _selectedFixCount;

        public string FixSelectedText => string.Format(CultureInfo.CurrentCulture, Strings.ValidatorFixSelectedFormat, SelectedFixCount);

        /// <summary>
        /// Whether every issue shown with a quick fix is ticked: <see langword="null"/> when only some are. Setting it
        /// ticks or clears them all; clearing it while only some are ticked ticks them all, so a click on the partly
        /// ticked box selects everything first.
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

        private IEnumerable<ValidatorIssueViewModel> FixableIssues => ShownSongs.SelectMany(s => s.ShownIssues).Where(i => i.IsFixable);

        /// <summary>Reads and checks every song, listing the ones that fail as they're found.</summary>
        public async Task LoadAsync()
        {
            IsChecking = true;
            foreach (var entry in _entries)
            {
                if (_drafts.HasDrafts(entry.FullPath))
                    _draftedAtStart.Add(entry.FullPath);
            }

            var options = new ParallelOptions { MaxDegreeOfParallelism = _parallelism, CancellationToken = _closing.Token };
            _read.Start();
            try
            {
                await Parallel.ForEachAsync(Enumerable.Range(0, _entries.Count), options, async (i, token) =>
                {
                    // The song is opened once, for its metadata and then its chart and audio.
                    var (report, files) = await SongMetadataCheck.OpenAsync(_entries[i], token).ConfigureAwait(false);
                    bool drafted = _draftedAtStart.Contains(report.Entry.FullPath);

                    SongChartLintReport? lint = null;
                    ValidatorSongCheck? check = null;
                    try
                    {
                        // A drafted song is linted and checked once it's listed, against its draft: nothing to do here.
                        if (!drafted)
                        {
                            // The song stays open for its chart only when a worker is free to lint it now. One kept open
                            // while it waits would hold the file against a job or a save writing it meanwhile, so it's
                            // let go of, and opened again once it's its turn.
                            if (!_checkGate.Wait(0))
                            {
                                files?.Dispose();
                                files = null;
                                await _checkGate.WaitAsync(token).ConfigureAwait(false);
                            }

                            try
                            {
                                // The chart is linted on this worker, with the song.ini as read.
                                if (report.ReadError is null)
                                {
                                    lint = files is not null
                                        ? await SongChartLint.LintAsync(files, report.Pairs, null, LibraryChartLint.Cache, token).ConfigureAwait(false)
                                        : await SongChartLint.LintAsync(report.Entry, report.Pairs, null, LibraryChartLint.Cache, token).ConfigureAwait(false);
                                }

                                // A song without a draft is checked here too. Drafts live on the UI thread, so the check
                                // of a song with one is made there, against it.
                                check = ValidatorSongViewModel.Evaluate(report, lint, null, CanGenerateDifficulties);
                            }
                            finally
                            {
                                _checkGate.Release();
                            }
                        }
                    }
                    finally
                    {
                        files?.Dispose();
                    }

                    // Waits while the UI thread is behind taking in the songs read, so they don't pile up unheld.
                    await _read.ReportAsync(new ReadSong(i, report, lint, check), token).ConfigureAwait(false);
                });

                // The songs read last are still to be taken in before the counts are final.
                await _read.TakeRestAsync();

                RefreshCounts();
                IsChecking = false;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                // Once a song is listed the window is open, and keeps what was found, all of it read so far, done
                // checking as far as it got.
                _read.TakeAll();
                RefreshCounts();
                IsChecking = false;
                _firstFailure.TrySetException(ex);
            }
            finally
            {
                _read.Stop();
                _firstFailure.TrySetResult(Songs.Count > 0);
            }
        }

        /// <summary>Stops checking, as the progress shown before the window opens is canceled.</summary>
        public void CancelCheck() => _closing.Cancel();

        /// <summary>Stops checking, lets go of the inspector's song and player, and stops following the drafts.</summary>
        public async Task ShutdownAsync()
        {
            _closing.Cancel();
            _searchTimer.Stop();
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
            // The same song, let go of and given back while the shown list was replaced: nothing to show again.
            if (_replacingShown)
                return;

            _inspected = value;

            // A song picked opens its list of issues, which stays open once another is picked.
            if (value is not null)
                value.IsExpanded = true;

            // While saving, the inspector holds no files open; it shows the song picked once the save is done.
            if (!IsSaving)
                _ = Inspector.ShowAsync(value is null ? [] : [value.Entry]);
        }

        /// <summary>Opens or closes a song's list of issues, as clicking the song again does once it's picked.</summary>
        public static void ToggleExpanded(ValidatorSongViewModel song) => song.IsExpanded = !song.IsExpanded;

        // Drafts mustn't change while either this window or the inspector is writing them, or while difficulties are
        // being generated into them.
        private bool CanFixSelected() => !IsSaving && !IsFixing && !Inspector.IsSaving && SelectedFixCount > 0;

        /// <summary>
        /// Applies the quick fixes of the ticked issues shown to their songs' drafts, each worked out from the song as
        /// drafted now: the metadata's, and the suggested ratings, at once; then, with the rules the Generate difficulties
        /// dialog is left with, the songs' missing difficulties, off the UI thread. Nothing is saved.
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanFixSelected))]
        private async Task FixSelectedAsync()
        {
            // Fixing a song checks it again, which replaces its issues, so the ticked ones are gathered first.
            var work = ShownSongs
                .Select(s => (Song: s, Issues: s.ShownIssues.Where(i => i.IsFixable && i.IsSelected).ToList()))
                .Where(w => w.Issues.Count > 0)
                .ToList();

            var generate = new List<(ValidatorSongViewModel Song, ValidatorGenerateFix? Missing, ValidatorStarPowerFix? StarPower)>();
            foreach (var (song, issues) in work)
            {
                MetadataItem? item = null;
                bool changed = false;
                ValidatorGenerateFix? missing = null;
                ValidatorStarPowerFix? starPower = null;
                foreach (var issue in issues)
                {
                    switch (issue.ChartFix)
                    {
                        case null:
                            item ??= new MetadataItem(song.Report.Pairs, _drafts.Get(song.Path), song.Report.DuplicateKeys);
                            QuickFixRegistry.FixKey(item, issue.Key, includeHints: false);
                            changed = true;
                            break;
                        case ValidatorRatingFix rating:
                            _drafts.Get(song.Path).Set(rating.Key, rating.Tier.ToString(CultureInfo.InvariantCulture));
                            changed = true;
                            break;
                        case ValidatorGenerateFix difficulties:
                            missing = difficulties;
                            break;
                        case ValidatorStarPowerFix parts:
                            starPower = parts;
                            break;
                    }
                }

                // A song's star power and missing difficulties are one chart, so they're generated together.
                if (missing is not null || starPower is not null)
                    generate.Add((song, missing, starPower));

                // Checks the song again, here and in any inspector showing it, which only has its rows to rebuild.
                if (changed)
                    _drafts.NotifyChanged(song.Path, DraftChange.Keys);
            }

            if (generate.Count > 0)
                await GenerateAsync(generate);
        }

        /// <summary>
        /// Generates the star power and missing difficulties of <paramref name="songs"/> into their drafts, the difficulties
        /// by the rules the Generate difficulties dialog is left with, and star power with its defaults: as many at once as
        /// there are cores but one, the footer counting them done. The dialog is shown only when some song has difficulties
        /// to generate. Songs that can't be generated are listed in <see cref="FixError"/>, and keep their drafts as they were.
        /// </summary>
        private async Task GenerateAsync(IReadOnlyList<(ValidatorSongViewModel Song, ValidatorGenerateFix? Missing, ValidatorStarPowerFix? StarPower)> songs)
        {
            if (_generate is null || _stash is not { } stash)
                return;

            // Each song as drafted now, its draft copied, as the work is done off the UI thread the drafts live on.
            var requests = songs
                .Select(w =>
                {
                    var draft = DraftOf(w.Song.Entry);
                    var targets = w.Missing?.Targets ?? new Dictionary<ChartInstrument, IReadOnlyList<ChartDifficulty>>();
                    return new GenerateDifficultiesSong(w.Song.Entry, w.Song.Title, w.Song.DraftedPairs(draft), draft?.Clone(), targets)
                    {
                        StarPower = w.StarPower?.Parts ?? [],
                    };
                })
                .ToList();

            // From here: the main window can still edit them while the dialog's open.
            _generating.UnionWith(requests.Select(r => r.Entry.FullPath));
            var failures = new ConcurrentQueue<string>();
            try
            {
                // Star power alone has no rules to pick, so it needs no dialog.
                var withTargets = requests.Where(static r => r.Targets.Count > 0).ToList();
                var rules = withTargets.Count == 0 ? ReductionDefaults.Shipped : await _generate.ShowAsync(withTargets);
                if (rules is null || _closing.IsCancellationRequested)
                    return;

                FixError = null;
                FixedCount = 0;
                ToFixCount = requests.Count;
                IsFixing = true;
                var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(Environment.ProcessorCount - 1, 1), CancellationToken = _closing.Token };
                await Parallel.ForEachAsync(requests, options, async (request, token) =>
                {
                    GeneratedChart? chart = null;
                    string? failure = null;
                    try
                    {
                        chart = await SongDifficultyGeneration.GenerateAsync(request.Entry, request.Pairs, request.Draft, rules, request.Work, stash, token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                    {
                        failure = ex.Message;
                    }

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        FixedCount++;
                        failure ??= Stage(request, chart);
                        if (failure is not null)
                            failures.Enqueue(string.Format(CultureInfo.CurrentCulture, Strings.ValidatorGenerateSongFailedFormat, request.Title, failure));
                    });
                });
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                IsFixing = false;
                _generating.Clear();
                _changedWhileGenerating.Clear();
            }

            if (!failures.IsEmpty)
                FixError = string.Format(CultureInfo.CurrentCulture, Strings.ValidatorGenerateFailedFormat, string.Join(Environment.NewLine, failures));
        }

        // A song's generated chart, into its draft as it is now; why it wasn't, or null once it is.
        private string? Stage(GenerateDifficultiesSong request, GeneratedChart? chart)
        {
            if (chart is null || _closing.IsCancellationRequested)
                return null;
            string path = request.Entry.FullPath;

            // Out of the set first, so staging it doesn't count as a change made meanwhile.
            _generating.Remove(path);
            if (_changedWhileGenerating.Contains(path))
                return Strings.ValidatorGenerateEditedMeanwhile;
            var draft = _drafts.Get(path);
            if (!SongDifficultyGeneration.CanGenerate(draft))
                return Strings.ValidatorGenerateChangedMeanwhile;
            chart.ApplyTo(draft);
            _drafts.NotifyChanged(path, DraftChange.Edited);
            return null;
        }

        /// <summary>The listed songs that can be saved and have something to save.</summary>
        private IEnumerable<ValidatorSongViewModel> SongsToSave => Songs.Where(s => s.Report.CanEdit && _drafts.HasDrafts(s.Path));

        private bool CanSaveAll() => !IsSaving && !IsFixing && !Inspector.IsSaving && !_job.IsRunning && SongsToSave.Any();

        /// <summary>
        /// Writes the drafts of every listed song. Songs that fail keep their drafts. Songs whose metadata still fails
        /// validation are only saved once the user confirms it.
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanSaveAll))]
        private async Task SaveAllAsync()
        {
            // Only the metadata is saved here, so it's what's asked about; a chart's problems are Chart Builder's to fix.
            int stillFailing = SongsToSave.Count(s => s.FailsMetadata);
            if (stillFailing > 0 && _confirmation is not null)
            {
                string message = string.Format(CultureInfo.CurrentCulture, Strings.ValidatorSaveInvalidMessageFormat, stillFailing);
                if (!await _confirmation.ConfirmAsync(Strings.SaveInvalidTitle, message, Strings.SaveAnywayButton))
                    return;

                // The drafts, or the job, may have moved on while the dialog was open.
                if (!CanSaveAll())
                    return;
            }

            var songs = SongsToSave.ToList();
            IDisposable? held = null;
            if (_locks is not null)
            {
                IReadOnlyList<(string Path, SongLockHolder Holder)> refused;
                try
                {
                    held = _locks.TryHold([.. songs.Select(s => s.Path)], out refused);
                }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
                {
                    SaveError = string.Format(CultureInfo.CurrentCulture, Strings.NotSavedInUseFormat, ex.Message);
                    return;
                }

                if (held is null)
                {
                    string NameOf(string path) => songs.First(s => s.Path == path).Entry.DisplayName;
                    SaveError = string.Format(CultureInfo.CurrentCulture, Strings.NotSavedInUseFormat, SongLocks.Describe(refused, NameOf));
                    return;
                }
            }

            var targets = songs
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
                held?.Dispose();
                IsSaving = false;

                // The inspector let go of its song's files whether or not it was saved, so it's shown again, once.
                await Inspector.ResumeAsync(SelectedSong is null ? [] : [SelectedSong.Entry]);
            }

            if (failures.Count > 0)
                SaveError = string.Format(CultureInfo.CurrentCulture, Strings.SaveFailedFormat, string.Join(Environment.NewLine, failures));
        }

        /// <summary>
        /// Takes in a batch of the songs read, and lists the ones that fail together. Below input and drawing, so the
        /// window keeps drawing and answering while a long check runs.
        /// </summary>
        private void TakeRead(IEnumerable<ReadSong> reads)
        {
            if (_closing.IsCancellationRequested)
            {
                _read.Clear();
                return;
            }

            var failing = new List<ValidatorSongViewModel>();
            int taken = 0;
            foreach (var read in reads)
            {
                taken++;
                if (Add(read) is { } song)
                    failing.Add(song);
            }

            if (taken == 0)
                return;

            Checked += taken;
            List(failing);
            ScheduleCheckRefreshCounts();
        }

        /// <summary>
        /// Checks a song read against its draft, if it has one now; returns it if it fails, or has a suggested fix, to be
        /// listed. A song with a draft has its chart linted again, with the draft: it was linted with the song.ini as read,
        /// if at all.
        /// </summary>
        private ValidatorSongViewModel? Add(ReadSong read)
        {
            var report = read.Report;
            // Checked by its worker, and still without a draft: one that passes, with nothing to suggest, is done with.
            if (read.Check is { } check && !_drafts.HasDrafts(report.Entry.FullPath))
            {
                if (!check.IsListed)
                    return null;

                var checkedSong = new ValidatorSongViewModel(read.Index, report, CanGenerateDifficulties, OnIssueSelectionChanged, IsIssueShown);
                checkedSong.SetChartLint(read.Lint);
                checkedSong.Apply(check);
                return checkedSong;
            }

            var song = new ValidatorSongViewModel(read.Index, report, CanGenerateDifficulties, OnIssueSelectionChanged, IsIssueShown);
            song.SetChartLint(read.Lint);
            song.Check(DraftOf(report.Entry));
            if (report.ReadError is null)
                _ = RelintAsync(song, delay: false);
            return song.IsListed ? song : null;
        }

        /// <summary>Lists <paramref name="song"/>, which fails, in its place.</summary>
        private void List(ValidatorSongViewModel song) => List([song]);

        /// <summary>Lists <paramref name="songs"/>, which fail, in their places, those not listed already. The caller counts them.</summary>
        private void List(IReadOnlyList<ValidatorSongViewModel> songs)
        {
            var added = new List<ValidatorSongViewModel>(songs.Count);
            foreach (var song in songs)
            {
                if (_byPath.TryAdd(song.Path, song))
                    added.Add(song);
            }

            if (added.Count == 0)
                return;

            added.Sort((a, b) => a.Index.CompareTo(b.Index));
            InsertSorted(Songs, added);

            var shown = added.Where(IsSongShown).ToList();
            InsertSorted(ShownSongs, shown);
            _shown.UnionWith(shown);

            OnPropertyChanged(nameof(ShownSummaryText));
            OnPropertyChanged(nameof(ShowNoMatches));
            _firstFailure.TrySetResult(true);
        }

        /// <summary>Reads finish in any order; the lists keep the selection's, which they're sorted by.</summary>
        private static void InsertSorted(AvaloniaList<ValidatorSongViewModel> list, ValidatorSongViewModel song) =>
            list.Insert(PlaceOf(list, song.Index), song);

        /// <summary>
        /// Puts <paramref name="songs"/>, in selection order, in their places. Songs that go between the same two are
        /// put in together, so the list (and the view over it) hears of each run of them once rather than of every song.
        /// </summary>
        private static void InsertSorted(AvaloniaList<ValidatorSongViewModel> list, IReadOnlyList<ValidatorSongViewModel> songs)
        {
            for (int start = 0; start < songs.Count;)
            {
                int at = PlaceOf(list, songs[start].Index);
                int next = at < list.Count ? list[at].Index : int.MaxValue;
                int end = start + 1;
                while (end < songs.Count && songs[end].Index < next)
                    end++;

                if (end - start == 1)
                    list.Insert(at, songs[start]);
                else
                    list.InsertRange(at, songs.Skip(start).Take(end - start));
                start = end;
            }
        }

        // Where a song at selection index `index` goes in a list sorted by it.
        private static int PlaceOf(AvaloniaList<ValidatorSongViewModel> list, int index)
        {
            int low = 0, high = list.Count;
            while (low < high)
            {
                int mid = (low + high) / 2;
                if (list[mid].Index < index)
                    low = mid + 1;
                else
                    high = mid;
            }

            return low;
        }

        /// <summary>
        /// Whether the filter shows <paramref name="issue"/>: it has a quick fix or not as the Fix chips ask, one of the
        /// types turned on, and one of the keys turned on.
        /// </summary>
        private bool IsIssueShown(ValidatorIssueViewModel issue)
        {
            if ((QuickFixChip.IsOn || ManualChip.IsOn) && !(issue.IsFixable ? QuickFixChip.IsOn : ManualChip.IsOn))
                return false;

            if (_keysOn.Count > 0 && !_keysOn.Contains(issue.Key))
                return false;

            return _typesOn.Count == 0 || issue.Types.Any(_typesOn.Contains);
        }

        private bool IsSongShown(ValidatorSongViewModel song) => MatchesSearch(song) && (!HasActiveFilters || song.HasShownIssue);

        /// <summary>Whether the song's title, artist, charter (as shown) or path contains the search, ignoring case.</summary>
        private bool MatchesSearch(ValidatorSongViewModel song)
        {
            if (_query.Length == 0)
                return true;

            return Contains(song.Title) || Contains(song.Artist) || Contains(song.CharterText) || Contains(song.Details);

            bool Contains(string? text) => text?.Contains(_query, StringComparison.CurrentCultureIgnoreCase) == true;
        }

        partial void OnSearchTextChanged(string? value)
        {
            _searchTimer.Stop();

            // Clear filters shows every song once, when it's done.
            if (_clearingFilters)
            {
                _query = value?.Trim() ?? "";
                return;
            }

            // A search cleared shows every song at once; one typed waits for the typing to stop, so the list is
            // filtered once rather than on every key.
            if (string.IsNullOrWhiteSpace(value))
                ApplySearch();
            else
                _searchTimer.Start();
        }

        /// <summary>Filters the list by the search as it is now, unless that's what it's filtered by already.</summary>
        private void ApplySearch()
        {
            _searchTimer.Stop();
            string query = SearchText?.Trim() ?? "";
            if (query == _query)
                return;

            _query = query;
            RefreshShown();
        }

        // Only with something to clear, so Escape in an empty search is left to the window.
        [RelayCommand(CanExecute = nameof(HasSearch))]
        private void ClearSearch() => SearchText = null;

        /// <summary>
        /// Shows a song the filter now matches, as when it's listed or checked again. A song already shown is left
        /// shown even when it no longer matches, so the list doesn't move under the pointer.
        /// </summary>
        private void ShowIfMatching(ValidatorSongViewModel song)
        {
            if (_shown.Contains(song) || !IsSongShown(song))
                return;

            InsertSorted(ShownSongs, song);
            _shown.Add(song);
            OnPropertyChanged(nameof(ShownSummaryText));
            OnPropertyChanged(nameof(ShowNoMatches));
        }

        /// <summary>A chip was turned on or off: every song's issues are filtered again, and the list shows the songs that now match.</summary>
        private void OnFilterChanged()
        {
            if (_clearingFilters)
                return;

            _typesOn.Clear();
            _typesOn.UnionWith(TypeChips.Where(c => c.IsOn).Select(c => c.Id));
            _keysOn.Clear();
            _keysOn.UnionWith(KeyChips.Where(c => c.IsOn).Select(c => c.Id));

            foreach (var song in Songs)
                song.ApplyFilter();

            OnPropertyChanged(nameof(ActiveFilterCount));
            OnPropertyChanged(nameof(HasActiveFilters));
            OnPropertyChanged(nameof(IsNarrowed));
            ClearFiltersCommand.NotifyCanExecuteChanged();
            RefreshShown();

            // A chip turned off once its songs no longer have its type or key goes.
            RefreshFilterChips();
        }

        /// <summary>The search or a chip changed: the list shows the songs that now match.</summary>
        private void RefreshShown()
        {
            // In selection order, as Songs is.
            var target = Songs.Where(IsSongShown).ToList();
            var shown = target.ToHashSet();

            // The picked song is let go of before it leaves the list, rather than by the list as it goes.
            if (SelectedSong is { } picked && !shown.Contains(picked))
                SelectedSong = null;

            // Each run of songs hidden, or of songs shown between the same two, is one change for the view.
            var added = target.Where(s => !_shown.Contains(s)).ToList();
            int runs = CountRuns(ShownSongs, s => !shown.Contains(s)) + CountRuns(target, s => !_shown.Contains(s));
            if (runs > ReplaceShownThreshold)
            {
                // Scattered changes over a long list: one change for the view rather than one per run.
                ReplaceShown(target);
            }
            else
            {
                // A few runs: songs still shown stay where they are, so the picked one stays picked.
                for (int end = ShownSongs.Count; end > 0;)
                {
                    if (shown.Contains(ShownSongs[end - 1]))
                    {
                        end--;
                        continue;
                    }

                    int start = end - 1;
                    while (start > 0 && !shown.Contains(ShownSongs[start - 1]))
                        start--;
                    ShownSongs.RemoveRange(start, end - start);
                    end = start;
                }

                InsertSorted(ShownSongs, added);
            }

            _shown.Clear();
            _shown.UnionWith(shown);

            OnPropertyChanged(nameof(ShownSummaryText));
            OnPropertyChanged(nameof(ShowNoMatches));
            RefreshSelection();
        }

        /// <summary>How many runs of consecutive songs in <paramref name="songs"/> match <paramref name="match"/>.</summary>
        private static int CountRuns(IEnumerable<ValidatorSongViewModel> songs, Func<ValidatorSongViewModel, bool> match)
        {
            int runs = 0;
            bool inRun = false;
            foreach (var song in songs)
            {
                bool matches = match(song);
                if (matches && !inRun)
                    runs++;
                inRun = matches;
            }

            return runs;
        }

        /// <summary>
        /// Replaces the shown songs with <paramref name="songs"/> in two changes, a reset and an add, however many there
        /// are. The view lets go of the picked song as the list is cleared, so it's given it back, without the inspector
        /// showing it again or hiding meanwhile.
        /// </summary>
        private void ReplaceShown(List<ValidatorSongViewModel> songs)
        {
            var picked = SelectedSong;
            _replacingShown = true;
            try
            {
                ShownSongs.Clear();
                ShownSongs.AddRange(songs);
                if (!ReferenceEquals(SelectedSong, picked))
                    SelectedSong = picked;
                else
                    OnPropertyChanged(nameof(SelectedSong));
            }
            finally
            {
                _replacingShown = false;
            }
        }

        private bool CanClearFilters() => IsNarrowed;

        /// <summary>Clears the search and turns every chip off, so every listed song and issue shows.</summary>
        [RelayCommand(CanExecute = nameof(CanClearFilters))]
        private void ClearFilters()
        {
            _clearingFilters = true;
            try
            {
                SearchText = null;
                foreach (var chip in FixChips.Concat(TypeChips).Concat(KeyChips))
                    chip.IsOn = false;
            }
            finally
            {
                _clearingFilters = false;
            }

            OnFilterChanged();
        }

        /// <summary>
        /// Counts the listed songs each chip matches. A type or key that fails in the songs gets a chip, types in the
        /// catalogue's order and keys alphabetically; one that no longer fails loses it, unless it's on, so a filter
        /// in use never goes out of sight.
        /// </summary>
        private void RefreshFilterChips()
        {
            int quick = 0, manual = 0;
            var songsByType = new Dictionary<string, int>(StringComparer.Ordinal);
            var songsByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var song in Songs)
            {
                if (song.HasFixes)
                    quick++;
                if (song.HasManual)
                    manual++;
                foreach (string type in song.IssueTypes)
                    songsByType[type] = songsByType.GetValueOrDefault(type) + 1;
                foreach (string key in song.IssueKeys)
                    songsByKey[key] = songsByKey.GetValueOrDefault(key) + 1;
            }

            QuickFixChip.Count = quick;
            ManualChip.Count = manual;
            SyncChips(TypeChips, songsByType, ValidatorIssueTypes.NameOf, ValidatorIssueTypes.Order);
            SyncChips(KeyChips, songsByKey, key => key, StringComparer.OrdinalIgnoreCase);
            OnPropertyChanged(nameof(HasTypeChips));
            OnPropertyChanged(nameof(HasKeyChips));
        }

        /// <summary>
        /// Brings <paramref name="chips"/> in line with <paramref name="counts"/>, which compares IDs as the chips
        /// should: a chip per ID, sorted by <paramref name="order"/>, and none for an ID no song has unless it's on.
        /// </summary>
        private void SyncChips(
            AvaloniaList<CountedFilterChipViewModel> chips,
            Dictionary<string, int> counts,
            Func<string, string> nameOf,
            IComparer<string> order)
        {
            var listed = new HashSet<string>(counts.Comparer);
            for (int i = chips.Count - 1; i >= 0; i--)
            {
                var chip = chips[i];
                chip.Count = counts.GetValueOrDefault(chip.Id);
                if (chip.Count == 0 && !chip.IsOn)
                    chips.RemoveAt(i);
                else
                    listed.Add(chip.Id);
            }

            foreach (var (id, count) in counts)
            {
                if (listed.Contains(id))
                    continue;

                int at = 0;
                while (at < chips.Count && order.Compare(chips[at].Id, id) < 0)
                    at++;
                chips.Insert(at, new CountedFilterChipViewModel(id, nameOf(id), Strings.ValidatorFilterChipToolTipFormat, OnFilterChanged) { Count = count });
            }
        }

        /// <summary>A listed song's draft changed, here or anywhere: it's checked again, from disk once the draft is gone.</summary>
        private void OnDraftsChanged(string path, DraftChange change)
        {
            if (_generating.Contains(path))
                _changedWhileGenerating.Add(path);

            if (!_byPath.TryGetValue(path, out var song))
                return;

            if (_drafts.HasDrafts(path))
            {
                song.Check(DraftOf(song.Entry));
                ShowIfMatching(song);
                ScheduleRefreshCounts();
                _ = RelintAsync(song, delay: true);
            }
            else
            {
                _ = RereadAsync(song);
            }
        }

        /// <summary>
        /// Lints <paramref name="song"/>'s chart again with its <c>song.ini</c> as drafted now, off the UI thread, then checks
        /// it again; a song not yet listed is listed if its chart has a problem. A change made meanwhile lints it again, in
        /// place of this run.
        /// </summary>
        /// <param name="delay">Whether to wait a moment first, so a burst of edits lints once.</param>
        private async Task RelintAsync(ValidatorSongViewModel song, bool delay)
        {
            song.RelintCancellation?.Cancel();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
            song.RelintCancellation = cts;
            try
            {
                if (delay)
                    await Task.Delay(RelintDelay, cts.Token);
                // The chart as drafted too: a draft that adds one, such as generated difficulties, is linted as it.
                var draft = DraftOf(song.Entry);
                var pairs = song.DraftedPairs(draft);
                var copy = draft?.Clone();
                var entry = song.Entry;
                var lint = await Task.Run(() => SongChartLint.LintAsync(entry, pairs, copy, LibraryChartLint.Cache, cts.Token), cts.Token);
                if (cts.IsCancellationRequested)
                    return;
                song.SetChartLint(lint);
                song.Check(DraftOf(song.Entry));
                if (song.IsListed)
                    List(song);
                ShowIfMatching(song);
                ScheduleRefreshCounts();
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                if (song.RelintCancellation == cts)
                    song.RelintCancellation = null;
            }
        }

        private async Task RereadAsync(ValidatorSongViewModel song)
        {
            try
            {
                var report = await SongMetadataCheck.ReadAsync(song.Entry, _closing.Token);
                song.Update(report, DraftOf(song.Entry));
                ShowIfMatching(song);
                ScheduleRefreshCounts();
                _ = RelintAsync(song, delay: false);
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

        // Missing difficulties are offered as a quick fix only when they can be generated and kept.
        private bool CanGenerateDifficulties => _generate is not null && _stash is not null;

        private MetadataDraft? DraftOf(SongEntry entry) => _drafts.HasDrafts(entry.FullPath) ? _drafts.Get(entry.FullPath) : null;

        /// <summary>
        /// Counts the songs and issues again once the songs changed so far are in: each walks the whole list, so a
        /// burst of them, such as fixing many songs, counts once rather than once per song.
        /// </summary>
        private void ScheduleRefreshCounts()
        {
            if (_refreshPending)
                return;

            _refreshPending = true;
            Dispatcher.UIThread.Post(RefreshCounts, DispatcherPriority.Background);
        }

        /// <summary>
        /// Counts the songs and issues again after a batch of songs the check read: while it runs, a moment later at
        /// most, as batch after batch comes in. What the user does meanwhile counts at once, by
        /// <see cref="ScheduleRefreshCounts"/>.
        /// </summary>
        private void ScheduleCheckRefreshCounts()
        {
            if (!IsChecking)
            {
                ScheduleRefreshCounts();
                return;
            }

            if (_checkRefreshPending)
                return;

            _checkRefreshPending = true;
            DispatcherTimer.RunOnce(() =>
            {
                _checkRefreshPending = false;
                RefreshCounts();
            }, CheckingCountsDelay, DispatcherPriority.Background);
        }

        private void RefreshCounts()
        {
            _refreshPending = false;
            _failingCount = Songs.Count(s => !s.IsFixed);
            OnPropertyChanged(nameof(FailingCount));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(AllPass));
            OnPropertyChanged(nameof(HasFailing));
            SaveAllCommand.NotifyCanExecuteChanged();
            RefreshFilterChips();
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

    /// <summary>
    /// A song in the Validator's list: what it's called and who it's by, how many of its keys fail with and without a
    /// quick fix, and, once opened, each of them and why.
    /// </summary>
    /// <param name="canGenerate">Whether the window can generate missing difficulties, so CHR087 has a quick fix.</param>
    /// <param name="selectionChanged">Called when one of the song's issues is ticked or cleared.</param>
    /// <param name="isShown">Whether the window's filter shows an issue.</param>
    public sealed partial class ValidatorSongViewModel(int index, SongMetadataReport report, bool canGenerate, Action selectionChanged, Func<ValidatorIssueViewModel, bool> isShown) : ObservableObject
    {
        private readonly bool _canGenerate = canGenerate;
        private readonly Action _selectionChanged = selectionChanged;
        private readonly Func<ValidatorIssueViewModel, bool> _isShown = isShown;
        private SongMetadataReport _report = report;
        private SongChartLintReport? _chartLint;

        // Set while the song's own box ticks or clears its issues, so the change is reported once at the end.
        private bool _settingAll;

        /// <summary>Where the song is in the selection.</summary>
        public int Index { get; } = index;

        public SongEntry Entry => _report.Entry;

        public string Path => _report.Entry.FullPath;

        /// <summary>The metadata as last read from disk.</summary>
        internal SongMetadataReport Report => _report;

        /// <summary>The name as drafted, or the file or folder name.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SelectFixesName))]
        public partial string Title { get; set; } = report.Entry.DisplayName;

        /// <summary>The artist as drafted, without rich-text tags; null when there is none.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(BylinePrefix), nameof(ShowByline), nameof(ShowDetails))]
        public partial string? Artist { get; set; }

        /// <summary>The charter as drafted, with its rich-text tags left in for the view to render; null when there is none.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(BylinePrefix), nameof(ShowByline), nameof(ShowDetails))]
        public partial string? Charter { get; set; }

        /// <summary>The charter as shown, without its rich-text tags, which the search matches.</summary>
        internal string? CharterText { get; private set; }

        partial void OnCharterChanged(string? value) => CharterText = value is null ? null : RichText.ToPlainText(value);

        /// <summary>
        /// The plain text before the charter on the byline: the artist, with the separator when a charter
        /// follows. The line is one text, so a long one is cut short with an ellipsis as a whole.
        /// </summary>
        public string? BylinePrefix =>
            Artist is null ? null
            : Charter is null ? Artist
            : Artist + Strings.ListDetailSeparator;

        /// <summary>The line under the title shows the artist and charter rather than <see cref="Details"/>.</summary>
        public bool ShowByline => Artist is not null || Charter is not null;

        public bool ShowDetails => !ShowByline;

        /// <summary>Where the song is, under the title when it has no artist or charter.</summary>
        public string Details => _report.Entry.RelativePath;

        /// <summary>The failing keys, each with why, and a note first when the song can't be edited.</summary>
        [ObservableProperty]
        public partial IReadOnlyList<ValidatorIssueViewModel> Issues { get; set; } = [];

        /// <summary>The issues the filter shows, and the note, which always shows.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowIssues))]
        public partial IReadOnlyList<ValidatorIssueViewModel> ShownIssues { get; set; } = [];

        /// <summary>The filter shows one of the song's issues, not counting the note.</summary>
        public bool HasShownIssue { get; private set; }

        /// <summary>An issue shown has a quick fix, so the song's box can tick it.</summary>
        [ObservableProperty]
        public partial bool HasShownFixes { get; set; }

        /// <summary>The types of the song's issues, each once.</summary>
        public IReadOnlyCollection<string> IssueTypes { get; private set; } = [];

        /// <summary>The failing keys, each once ignoring case. An empty key is left out; its type covers it.</summary>
        public IReadOnlyCollection<string> IssueKeys { get; private set; } = [];

        /// <summary>How many failing keys have a quick fix.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FixableCountText), nameof(HasFixes))]
        public partial int FixableCount { get; set; }

        /// <summary>How many failing keys have no quick fix, or one when the song couldn't be read.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ManualCountText), nameof(HasManual))]
        public partial int ManualCount { get; set; }

        public string FixableCountText => string.Format(CultureInfo.CurrentCulture, Strings.ValidatorFixableCountFormat, FixableCount);

        public string ManualCountText => string.Format(CultureInfo.CurrentCulture, Strings.ValidatorManualCountFormat, ManualCount);

        /// <summary>Some issue has a quick fix, shown or not.</summary>
        public bool HasFixes => FixableCount > 0;

        public bool HasManual => ManualCount > 0;

        /// <summary>The song passes now: nothing fails, though it may have a suggested fix still (<see cref="HasSuggestions"/>).</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsListed), nameof(IsDone))]
        public partial bool IsFixed { get; set; }

        /// <summary>The song has a fix for a note that doesn't fail it, such as star power to generate (CHR066).</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsListed), nameof(IsDone))]
        public partial bool HasSuggestions { get; set; }

        /// <summary>The song is listed: it fails, or has a suggested fix.</summary>
        public bool IsListed => !IsFixed || HasSuggestions;

        /// <summary>The song has nothing left to do: it passes and has no suggested fix, so it's shown as fixed.</summary>
        public bool IsDone => IsFixed && !HasSuggestions;

        /// <summary>Whether a metadata key still fails, rather than only the chart.</summary>
        public bool FailsMetadata { get; private set; }

        /// <summary>Whether the chart, as last linted, has a Warning or an Error (charter-spec C16 item 5).</summary>
        public bool HasChartProblems => _chartLint?.HasProblems == true;

        /// <summary>Cancels the lint of the song's chart going on, when a newer one replaces it.</summary>
        internal CancellationTokenSource? RelintCancellation { get; set; }

        /// <summary>Takes in what linting the song's chart found; null for not linted yet. <see cref="Check"/> lists it.</summary>
        public void SetChartLint(SongChartLintReport? report) => _chartLint = report;

        /// <summary>The song's metadata as <paramref name="draft"/> would save it.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> DraftedPairs(MetadataDraft? draft)
        {
            var item = new MetadataItem(_report.Pairs, draft ?? new MetadataDraft(), _report.DuplicateKeys);
            return item.Draft.ApplyTo(item.Original);
        }

        /// <summary>The song's list of issues is open under it.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowIssues))]
        public partial bool IsExpanded { get; set; }

        public bool ShowIssues => IsExpanded && ShownIssues.Count > 0;

        public string SelectFixesName => string.Format(CultureInfo.CurrentCulture, Strings.ValidatorSelectSongFixesFormat, Title);

        /// <summary>
        /// Whether every issue shown of the song with a quick fix is ticked: <see langword="null"/> when only some are.
        /// Setting it ticks or clears them all; clearing it while only some are ticked ticks them all, so a click on
        /// the partly ticked box selects everything first.
        /// </summary>
        public bool? AllSelected
        {
            get
            {
                int fixable = 0, selected = 0;
                foreach (var issue in ShownIssues)
                {
                    if (!issue.IsFixable)
                        continue;

                    fixable++;
                    if (issue.IsSelected)
                        selected++;
                }

                return selected == 0 ? false : selected == fixable ? true : null;
            }
            set
            {
                bool select = value ?? true;
                if (value == false && AllSelected is null)
                    select = true;

                _settingAll = true;
                try
                {
                    foreach (var issue in ShownIssues)
                    {
                        if (issue.IsFixable)
                            issue.IsSelected = select;
                    }
                }
                finally
                {
                    _settingAll = false;
                }

                OnPropertyChanged(nameof(AllSelected));
                _selectionChanged();
            }
        }

        /// <summary>The metadata was read again with a new result, e.g. after saving.</summary>
        public void Update(SongMetadataReport report, MetadataDraft? draft)
        {
            _report = report;
            OnPropertyChanged(nameof(Details));
            Check(draft);
        }

        /// <summary>
        /// Checks the metadata as <paramref name="draft"/> would save it. Issues ticked before stay ticked while they're
        /// still fixable. A song that can't be edited, such as a <c>.sng</c> with structural errors, has no fixes.
        /// </summary>
        public void Check(MetadataDraft? draft) => Apply(Evaluate(_report, _chartLint, draft, _canGenerate));

        /// <summary>
        /// Takes in what checking the song found: its headline and issues. Issues ticked before stay ticked while
        /// they're still fixable.
        /// </summary>
        public void Apply(ValidatorSongCheck check)
        {
            Title = check.Title;
            Artist = check.Artist;
            Charter = check.Charter;

            var selected = Issues.Where(i => i.IsSelected).Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var issues = check.Lines
                .Select(line => new ValidatorIssueViewModel(line.Key, line.Message, line.Types, line.Fix, line.Fix is not null && selected.Contains(line.Id), OnIssueSelectionChanged)
                {
                    Id = line.Id,
                    ChartSeverity = line.ChartSeverity,
                    ChartFix = line.ChartFix,
                })
                .ToList();

            FailsMetadata = check.FailsMetadata;
            IsFixed = check.IsFixed;
            HasSuggestions = check.Suggests;
            SetIssues(issues, check.Fixable, check.Manual);
        }

        /// <summary>
        /// Checks <paramref name="report"/>'s metadata as <paramref name="draft"/> would save it, with
        /// <paramref name="lint"/>'s chart findings. It touches nothing of the window's, so a song read without a draft
        /// is checked on the worker that read it.
        /// </summary>
        /// <param name="canGenerate">Whether missing difficulties (CHR087) can be generated, so they have a quick fix.</param>
        public static ValidatorSongCheck Evaluate(SongMetadataReport report, SongChartLintReport? lint, MetadataDraft? draft, bool canGenerate)
        {
            var item = new MetadataItem(report.Pairs, draft ?? new MetadataDraft(), report.DuplicateKeys);
            var pairs = item.Draft.ApplyTo(item.Original);
            var headline = SongHeadline.From(pairs);
            string title = headline.Name ?? report.Entry.DisplayName;
            string? charter = SongHeadline.CharterMarkup(pairs);

            if (report.ReadError is { } error)
            {
                string unreadable = string.Format(CultureInfo.CurrentCulture, Strings.ValidatorUnreadableFormat, error);
                return new ValidatorSongCheck(title, headline.Artist, charter, [new ValidatorIssueLine("", unreadable, [ValidatorIssueTypes.Unreadable], null)], 0, 1, FailsMetadata: false, IsFixed: false);
            }

            // One line per failing key, its messages joined, with what its quick fixes do.
            var byKey = MetadataValidator.Validate(pairs, item.RemainingDuplicateKeys())
                .Where(MetadataValidator.IsFailing)
                .GroupBy(f => f.Key ?? "", StringComparer.OrdinalIgnoreCase)
                .ToList();

            var lines = new List<ValidatorIssueLine>();
            // Only a .sng with structural errors is read but can't be edited; the rest that can't couldn't be read.
            // The note says why its keys have no fixes, and isn't counted as one of them.
            if (!report.CanEdit && byKey.Count > 0)
            {
                string reason = string.Format(CultureInfo.CurrentCulture, Strings.SngHasErrorsFormat, report.Entry.DisplayName);
                lines.Add(new ValidatorIssueLine("", reason, [], null));
            }

            int fixable = 0;
            foreach (var group in byKey)
            {
                List<string> fixes = !report.CanEdit ? [] : [.. group
                    .Select(f => QuickFixRegistry.Propose(f, pairs)?.Description)
                    .OfType<string>()
                    .Distinct(StringComparer.Ordinal)];
                string? fix = fixes.Count == 0 ? null : string.Join(", ", fixes);
                if (fix is not null)
                    fixable++;

                string message = string.Join(" ", group.Select(f => f.Message).Distinct(StringComparer.Ordinal));
                List<string> types = [.. group.Select(f => f.RuleId).Distinct(StringComparer.Ordinal)];
                lines.Add(new ValidatorIssueLine(group.Key, message, types, fix));
            }

            // The chart's findings, after the metadata's, one line per rule and key. An unrated part's is fixed with the
            // estimator's rating, when it has one; the rest are Chart Builder's to fix.
            int chartProblems = 0, chartFixable = 0;
            var findings = lint?.Findings ?? [];
            foreach (var group in findings.Where(static f => f.Finding.RuleId is not (ChrRules.Chr087 or ChrRules.Chr066)).GroupBy(f => (f.Finding.RuleId, Key: f.Finding.Key ?? "")))
            {
                var first = group.First();
                bool problem = group.Any(f => f.Finding.Severity != Severity.Info);
                var severity = group.Max(f => f.Finding.Severity == Severity.Error ? 2 : f.Finding.Severity == Severity.Warning ? 1 : 0) switch
                {
                    2 => Severity.Error,
                    1 => Severity.Warning,
                    _ => Severity.Info,
                };

                ValidatorChartFix? chartFix = null;
                string? fix = null;
                if (report.CanEdit && group.Key.RuleId == ChrRules.Chr083 && lint!.SuggestedRatings.TryGetValue(group.Key.Key, out int tier))
                {
                    chartFix = new ValidatorRatingFix(group.Key.Key, tier);
                    fix = string.Format(CultureInfo.CurrentCulture, Strings.ValidatorSuggestedRatingFixFormat, tier);
                }

                if (problem)
                {
                    chartProblems++;
                    if (fix is not null)
                        chartFixable++;
                }

                lines.Add(new ValidatorIssueLine(group.Key.Key, LibraryChartLint.Describe(first, group.Count()), [LibraryChartLint.TypeOf(group.Key.RuleId)], fix, severity)
                {
                    Id = ValidatorIssueLine.ChartId(group.Key.RuleId, group.Key.Key),
                    ChartFix = chartFix,
                });
            }

            // The parts' missing difficulties (CHR087), one finding each, as one line: they're generated together, from
            // the chart as drafted, unless the draft changes the chart some other way.
            var missing = findings.Where(static f => f.Finding.RuleId == ChrRules.Chr087 && f.Finding.Location is { Difficulty: not null }).ToList();
            if (missing.Count > 0)
            {
                IReadOnlyDictionary<ChartInstrument, IReadOnlyList<ChartDifficulty>> targets = missing
                    .GroupBy(static f => f.Finding.Location!.Part)
                    .OrderBy(static g => g.Key)
                    .ToDictionary(static g => g.Key, static g => (IReadOnlyList<ChartDifficulty>)[.. g.Select(static f => f.Finding.Location!.Difficulty!.Value).Distinct().OrderDescending()]);
                bool canFix = canGenerate && report.CanEdit && SongDifficultyGeneration.CanGenerate(draft);
                string message = string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.ChartFindingFormat,
                    ChrRules.Chr087,
                    string.Format(CultureInfo.CurrentCulture, Strings.ValidatorMissingDifficultiesFormat, GenerateDifficultiesSong.DescribeTargets(targets)));
                chartProblems++;
                if (canFix)
                    chartFixable++;
                lines.Add(new ValidatorIssueLine("", message, [LibraryChartLint.TypeOf(ChrRules.Chr087)], canFix ? Strings.ValidatorGenerateFix : null, Severity.Warning)
                {
                    Id = ValidatorIssueLine.ChartId(ChrRules.Chr087, ""),
                    ChartFix = canFix ? new ValidatorGenerateFix(targets) : null,
                });
            }

            // The parts without star power (CHR066), as one line: a note, which fails nothing, but whose fix lists the song
            // so Fix selected can generate it, with the missing difficulties when there are any.
            var noStarPower = findings
                .Where(static f => f.Finding.RuleId == ChrRules.Chr066 && f.Finding.Location is not null)
                .Select(static f => f.Finding.Location!.Part)
                .Distinct()
                .Order()
                .ToList();
            int suggested = 0;
            if (noStarPower.Count > 0)
            {
                bool canFix = canGenerate && report.CanEdit && SongDifficultyGeneration.CanGenerate(draft);
                string message = string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.ChartFindingFormat,
                    ChrRules.Chr066,
                    string.Format(CultureInfo.CurrentCulture, Strings.ValidatorNoStarPowerFormat, string.Join(Strings.ValidatorMissingPartSeparator, noStarPower.Select(ChartLabels.InstrumentName))));
                if (canFix)
                    suggested++;
                lines.Add(new ValidatorIssueLine("", message, [LibraryChartLint.TypeOf(ChrRules.Chr066)], canFix ? Strings.ValidatorGenerateStarPowerFix : null, Severity.Info)
                {
                    Id = ValidatorIssueLine.ChartId(ChrRules.Chr066, ""),
                    ChartFix = canFix ? new ValidatorStarPowerFix(noStarPower) : null,
                });
            }

            return new ValidatorSongCheck(
                title,
                headline.Artist,
                charter,
                lines,
                fixable + chartFixable + suggested,
                byKey.Count - fixable + chartProblems - chartFixable,
                FailsMetadata: byKey.Count > 0,
                IsFixed: byKey.Count == 0 && chartProblems == 0)
            {
                Suggests = suggested > 0,
            };
        }

        private void SetIssues(IReadOnlyList<ValidatorIssueViewModel> issues, int fixable, int manual)
        {
            Issues = issues;
            IssueTypes = issues.SelectMany(i => i.Types).ToHashSet(StringComparer.Ordinal);
            IssueKeys = issues.Where(i => i.HasKey).Select(i => i.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            FixableCount = fixable;
            ManualCount = manual;
            ApplyFilter();
        }

        /// <summary>Works out which issues the window's filter shows, as it changes or the song is checked again.</summary>
        public void ApplyFilter()
        {
            ShownIssues = [.. Issues.Where(i => i.IsNote || _isShown(i))];
            HasShownIssue = ShownIssues.Any(i => !i.IsNote);
            HasShownFixes = ShownIssues.Any(i => i.IsFixable);
            OnPropertyChanged(nameof(AllSelected));
        }

        private void OnIssueSelectionChanged()
        {
            if (_settingAll)
                return;

            OnPropertyChanged(nameof(AllSelected));
            _selectionChanged();
        }
    }

    /// <summary>What checking a song found, before it's shown: its headline and a line per issue.</summary>
    /// <param name="Fixable">How many failing keys have a quick fix.</param>
    /// <param name="Manual">How many issues, metadata or chart, don't.</param>
    /// <param name="FailsMetadata">Some of its metadata fails, which saving asks about.</param>
    /// <param name="IsFixed">Nothing fails, so it isn't listed unless it <see cref="Suggests"/> a fix.</param>
    public sealed record ValidatorSongCheck(
        string Title,
        string? Artist,
        string? Charter,
        IReadOnlyList<ValidatorIssueLine> Lines,
        int Fixable,
        int Manual,
        bool FailsMetadata,
        bool IsFixed)
    {
        /// <summary>It has a fix for a note that fails nothing, such as star power to generate (CHR066), so it's listed though it passes.</summary>
        public bool Suggests { get; init; }

        /// <summary>It's listed: it fails, or suggests a fix.</summary>
        public bool IsListed => !IsFixed || Suggests;
    }

    /// <summary>One of a song's issues as found, before it's shown and can be ticked.</summary>
    /// <param name="Key">The failing key, or empty for a note about the song as a whole.</param>
    /// <param name="Types">The rules it breaks, as <see cref="ValidatorIssueTypes"/> knows them; none for a note.</param>
    /// <param name="Fix">What the key's quick fix does, or <see langword="null"/> without one.</param>
    /// <param name="ChartSeverity">How bad a chart finding is; <see langword="null"/> for the metadata's.</param>
    public sealed record ValidatorIssueLine(string Key, string Message, IReadOnlyList<string> Types, string? Fix, Severity? ChartSeverity = null)
    {
        /// <summary>
        /// Which of the song's lines it is, which a tick follows as the song is checked again: the key for the metadata's,
        /// and the rule and key for the chart's, so a chart finding about a key isn't taken for the key's own line.
        /// </summary>
        public string Id { get; init; } = Key;

        /// <summary>What fixes a chart finding: its suggested rating, or its missing difficulties generated; null for the metadata's.</summary>
        public ValidatorChartFix? ChartFix { get; init; }

        /// <summary>The <see cref="Id"/> of a chart finding's line.</summary>
        public static string ChartId(string ruleId, string key) => ruleId + ":" + key;
    }

    /// <summary>What fixes a chart finding in the Validator.</summary>
    public abstract record ValidatorChartFix;

    /// <summary>CHR083's fix: the part's <c>diff_*</c> <paramref name="Key"/> set to the estimator's <paramref name="Tier"/>.</summary>
    public sealed record ValidatorRatingFix(string Key, int Tier) : ValidatorChartFix;

    /// <summary>CHR087's fix: each part's missing difficulties, highest first, generated from those above them.</summary>
    public sealed record ValidatorGenerateFix(IReadOnlyDictionary<ChartInstrument, IReadOnlyList<ChartDifficulty>> Targets) : ValidatorChartFix;

    /// <summary>CHR066's fix: star power generated for each of the parts without any.</summary>
    public sealed record ValidatorStarPowerFix(IReadOnlyList<ChartInstrument> Parts) : ValidatorChartFix;

    /// <summary>A failing key of a song in the Validator's list, which can be ticked for Fix selected when it has a quick fix.</summary>
    /// <param name="key">The failing key, or empty for a note about the song as a whole.</param>
    /// <param name="message">Why it fails.</param>
    /// <param name="types">The rules it breaks, as <see cref="ValidatorIssueTypes"/> knows them; none for a note.</param>
    /// <param name="fixDescription">What the key's quick fix does, or <see langword="null"/> without one.</param>
    /// <param name="isSelected">Whether it starts ticked.</param>
    /// <param name="selectionChanged">Called when it's ticked or cleared.</param>
    public sealed partial class ValidatorIssueViewModel(string key, string message, IReadOnlyList<string> types, string? fixDescription, bool isSelected, Action selectionChanged) : ObservableObject
    {
        private readonly Action _selectionChanged = selectionChanged;

        public string Key { get; } = key;

        /// <summary>Which of the song's lines it is (<see cref="ValidatorIssueLine.Id"/>).</summary>
        public string Id { get; init; } = key;

        /// <summary>What fixes a chart finding (<see cref="ValidatorIssueLine.ChartFix"/>); null for a metadata issue.</summary>
        public ValidatorChartFix? ChartFix { get; init; }

        /// <summary>The key is shown as a chip before the message; a note about the whole song has none.</summary>
        public bool HasKey => Key.Length > 0;

        public string Message { get; } = message;

        /// <summary>The rules it breaks, which the type filter matches.</summary>
        public IReadOnlyList<string> Types { get; } = types;

        /// <summary>A note about the song, such as why it can't be edited, rather than a failing key. The filter always shows it.</summary>
        public bool IsNote => Types.Count == 0;

        /// <summary>The key and why it fails, as the issue's check box is named.</summary>
        public string Text => HasKey ? string.Format(CultureInfo.CurrentCulture, Strings.ValidatorIssueLineFormat, Key, Message) : Message;

        /// <summary>What the quick fix does, e.g. "Change to True"; <see langword="null"/> without one.</summary>
        public string? FixDescription { get; } = fixDescription;

        public bool IsFixable => FixDescription is not null;

        /// <summary>For a chart finding (charter-spec C16 item 5), how bad it is; null for a metadata issue.</summary>
        public Severity? ChartSeverity { get; init; }

        public bool IsChartError => ChartSeverity == Severity.Error;

        public bool IsChartWarning => ChartSeverity == Severity.Warning;

        /// <summary>The line under a fixable issue saying what fixing it does.</summary>
        public string? FixText => FixDescription is null ? null : string.Format(CultureInfo.CurrentCulture, Strings.ValidatorFixDescriptionFormat, FixDescription);

        /// <summary>Ticked for Fix selected.</summary>
        [ObservableProperty]
        public partial bool IsSelected { get; set; } = isSelected;

        // The initializer sets the backing field, so the starting tick isn't reported.
        partial void OnIsSelectedChanged(bool value) => _selectionChanged();
    }
}
