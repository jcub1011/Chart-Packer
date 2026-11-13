using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Threading;
using ChartPacker.Resources;
using ChartPacker.Services;
using ChartShared.Core.Jobs;
using ChartShared.Core.Optimization;
using ChartShared.Core.Scanning;
using ChartShared.Core.Settings;
using ChartShared.UI.Formatting;
using ChartShared.UI.Services;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartPacker.ViewModels
{
    /// <summary>What the Optimize window is doing.</summary>
    public enum OptimizePhase
    {
        /// <summary>Listing the charts' files.</summary>
        Reading,

        /// <summary>Measuring what each chart would save; the ones not reached yet are predicted.</summary>
        Measuring,

        /// <summary>Nothing: every chart is measured, or nothing is switched on to measure.</summary>
        Ready,

        /// <summary>Optimizing.</summary>
        Running,

        /// <summary>A run has finished, or was stopped, and its results are shown.</summary>
        Done,
    }

    /// <summary>A filter chip over the chart list: one group of charts, and how many are in it.</summary>
    public sealed partial class OptimizeFilterChip(OptimizeRowGroup group) : ObservableObject
    {
        public OptimizeRowGroup Group { get; } = group;

        [ObservableProperty]
        public partial bool IsOn { get; set; }

        [ObservableProperty]
        public partial string Label { get; set; } = string.Empty;

        /// <summary>The chip is the problems', and there are some: it's shown in amber.</summary>
        [ObservableProperty]
        public partial bool IsAlert { get; set; }
    }

    /// <summary>How the chart list is ordered.</summary>
    public enum OptimizeSort
    {
        Saving,
        Size,
        Name,
        Problems,
    }

    /// <summary>
    /// The Optimize window (issue #257): every chart with what optimizing it saves, the settings that decide that, and
    /// the run itself.
    /// </summary>
    /// <remarks>
    /// <para>Opening the window reads the charts' files, then measures them a chart at a time in the background
    /// (<see cref="LibraryEstimator"/>): a few files of each media type first, from which every other chart is predicted
    /// at once, then the rest, each chart's prediction giving way to its own measured figure. A changed setting starts
    /// the measuring again, which costs only what the estimate cache doesn't already hold.</para>
    /// <para>A measured chart's figure is the plan a run carries out, so the run doesn't measure it again; charts not yet
    /// measured are planned a batch at a time as the run reaches them.</para>
    /// </remarks>
    public sealed partial class OptimizeWindowViewModel : ViewModelBase
    {
        // How many charts not yet measured a run plans before it optimizes them, and plans the next lot.
        private const int RunBatch = 24;

        private readonly OptimizeRequest _request;
        private readonly OptimizeSettingsFile _defaults;
        private readonly OptimizeBackends _backends;
        private readonly OptimizeJobServices _services;
        private readonly IConfirmationService _confirmation;
        private readonly IFolderPickerService _folderPicker;
        private readonly IFileRevealService _reveal;
        private readonly CancellationTokenSource _closing = new();
        private readonly DispatcherTimer _refresh;
        private readonly DispatcherTimer _settle;
        private readonly EstimateCache _cache;
        private readonly SemaphoreSlim _videoGate;

        private List<OptimizeChartRow> _rows = [];
        private Dictionary<string, OptimizeChartRow> _byPath = new(OutputPaths.PathComparer);
        private OptimizerSet? _optimizers;
        private int _optimizersParallelism;

        // The background work: reading and measuring. Each change of scope or settings starts a new generation of it.
        private CancellationTokenSource? _work;
        private Task _pipeline = Task.CompletedTask;
        private int _generation;
        private LibraryEstimator? _estimator;
        private SavingsModel? _model;
        private int _modelVersion = -1;
        private OptimizeOptions? _measuredOptions;

        private CancellationTokenSource? _run;
        private Task _current = Task.CompletedTask;
        private bool _closeWhenIdle;
        private bool _dirty;
        private bool _resort;
        private bool _sampleShown;
        private bool _shutDown;

        private int _read;
        private int _measured;
        private int _toMeasure;
        private int _finished;
        private int _toRun;
        private int _applied;
        private int _skipped;
        private int _failed;
        private long _bytesSaved;
        private long _bytesEstimated;
        private int _changedInPlace;
        private int _written;
        private bool _stopped;

        /// <param name="request">What the window works on.</param>
        /// <param name="defaults">Where the window's settings are read from and kept.</param>
        /// <param name="backends">The optimizers, probes, availability and estimate cache.</param>
        /// <param name="services">What a run needs from the app: its locks, gate and drafts.</param>
        /// <param name="confirmation">Asks before charts are changed in place.</param>
        /// <param name="folderPicker">Picks the output folder.</param>
        /// <param name="reveal">Opens the output folder.</param>
        public OptimizeWindowViewModel(
            OptimizeRequest request,
            OptimizeSettingsFile defaults,
            OptimizeBackends backends,
            OptimizeJobServices services,
            IConfirmationService confirmation,
            IFolderPickerService folderPicker,
            IFileRevealService reveal)
        {
            _request = request;
            _defaults = defaults;
            _backends = backends;
            _services = services;
            _confirmation = confirmation;
            _folderPicker = folderPicker;
            _reveal = reveal;
            _cache = backends.Estimates;
            _videoGate = new SemaphoreSlim(OptimizeOptions.RecommendedParallelism);
            _useWholeLibrary = request.Selected.Count == 0;

            Settings = new OptimizeSettingsViewModel(defaults.Load(), backends.UnavailableReason);
            Settings.Changed += OnSettingsChanged;

            SortChoices =
            [
                new(OptimizeSort.Saving, Strings.OptimizeSortSaving),
                new(OptimizeSort.Size, Strings.OptimizeSortSize),
                new(OptimizeSort.Name, Strings.OptimizeSortName),
                new(OptimizeSort.Problems, Strings.OptimizeSortProblems),
            ];
            Chips = [new(OptimizeRowGroup.Shrinks), new(OptimizeRowGroup.Nothing), new(OptimizeRowGroup.Problem)];
            foreach (var chip in Chips)
                chip.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(OptimizeFilterChip.IsOn)) ShowRows(); };
            SelectedSort = SortChoices[0];

            // What's measured arrives in bursts from several threads; the list and the totals catch up a few times a second.
            _refresh = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
            _refresh.Tick += (_, _) => RefreshView();
            // A slider being dragged is many changes; measuring starts again once they stop.
            _settle = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(350) };
            _settle.Tick += (_, _) =>
            {
                _settle.Stop();
                OnSettingsSettled();
            };
        }

        /// <summary>The window should close.</summary>
        public event Action? CloseRequested;

        /// <summary>What to shrink and how.</summary>
        public OptimizeSettingsViewModel Settings { get; }

        /// <summary>The charts shown, as searched, filtered and sorted.</summary>
        public AvaloniaList<OptimizeChartRow> Rows { get; } = [];

        public IReadOnlyList<OptimizeChoice<OptimizeSort>> SortChoices { get; }

        public IReadOnlyList<OptimizeFilterChip> Chips { get; }

        // ---- Scope --------------------------------------------------------------------------------------------

        private bool _useWholeLibrary;

        /// <summary>The window works on the whole library, not the songs that were selected.</summary>
        public bool UseWholeLibrary
        {
            get => _useWholeLibrary;
            set
            {
                // The scope is what a run is working on, so it can't change under one.
                if (IsRunning || !SetProperty(ref _useWholeLibrary, value))
                    return;
                OnPropertyChanged(nameof(UseSelection));
                LoadScope();
            }
        }

        /// <summary>The window works on the songs that were selected.</summary>
        public bool UseSelection
        {
            get => !_useWholeLibrary;
            set => UseWholeLibrary = !value;
        }

        /// <summary>Songs were selected, so the user may choose between them and the whole library.</summary>
        public bool HasSelection => _request.Selected.Count > 0;

        public bool HasNoSelection => !HasSelection;

        public string SelectedScopeText => string.Format(CultureInfo.CurrentCulture, Strings.OptimizeScopeSelectedFormat, _request.Selected.Count);

        public string LibraryScopeText => string.Format(CultureInfo.CurrentCulture, Strings.OptimizeScopeLibraryCountFormat, _request.Library.Count);

        // ---- The list -----------------------------------------------------------------------------------------

        [ObservableProperty]
        public partial string SearchText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial OptimizeChoice<OptimizeSort> SelectedSort { get; set; }

        [ObservableProperty]
        public partial OptimizeChartRow? SelectedRow { get; set; }

        /// <summary>There are charts, and the search and filters hide them all.</summary>
        [ObservableProperty]
        public partial bool HasNoMatches { get; set; }

        /// <summary>There are no charts to work on at all.</summary>
        [ObservableProperty]
        public partial bool HasNoCharts { get; set; }

        // ---- The summary --------------------------------------------------------------------------------------

        /// <summary>The charts' size before and after: "41.2 GB → 27.9 GB".</summary>
        [ObservableProperty]
        public partial string SizesText { get; set; } = string.Empty;

        /// <summary>What's saved: "−13.3 GB", or "Saved 13.3 GB" once a run has.</summary>
        [ObservableProperty]
        public partial string SavingText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string PercentText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool HasSaving { get; set; }

        /// <summary>How much of the figure is measured, or how the run went.</summary>
        [ObservableProperty]
        public partial string CoverageText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial double BarKept { get; set; }

        [ObservableProperty]
        public partial double BarSaved { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasPredicted))]
        public partial double BarPredicted { get; set; }

        /// <summary>Part of the saving is still a prediction, so the bar's legend names that part.</summary>
        public bool HasPredicted => BarPredicted > 0;

        /// <summary>What went wrong, for the banner; empty when nothing has.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasError))]
        public partial string ErrorText { get; set; } = string.Empty;

        public bool HasError => ErrorText.Length > 0;

        // ---- What's going on ----------------------------------------------------------------------------------

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsRunning))]
        [NotifyPropertyChangedFor(nameof(IsNotRunning))]
        [NotifyPropertyChangedFor(nameof(IsMeasuring))]
        [NotifyPropertyChangedFor(nameof(IsWorking))]
        [NotifyPropertyChangedFor(nameof(IsDone))]
        [NotifyPropertyChangedFor(nameof(CanOpenOutput))]
        [NotifyCanExecuteChangedFor(nameof(OptimizeCommand))]
        [NotifyCanExecuteChangedFor(nameof(StopCommand))]
        [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
        [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
        [NotifyCanExecuteChangedFor(nameof(BrowseOutputCommand))]
        [NotifyCanExecuteChangedFor(nameof(TogglePauseCommand))]
        public partial OptimizePhase Phase { get; set; }

        public bool IsRunning => Phase == OptimizePhase.Running;

        // The button's label and whether it can be pressed follow the phase at once, not at the next refresh.
        partial void OnPhaseChanged(OptimizePhase value) => RefreshOptimizeState();

        public bool IsNotRunning => !IsRunning;

        public bool IsMeasuring => Phase == OptimizePhase.Measuring;

        /// <summary>Something is going on in the background: reading, measuring or a run.</summary>
        public bool IsWorking => Phase is OptimizePhase.Reading or OptimizePhase.Running || (IsMeasuring && !IsPaused);

        public bool IsDone => Phase == OptimizePhase.Done;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsWorking))]
        [NotifyPropertyChangedFor(nameof(PauseLabel))]
        public partial bool IsPaused { get; set; }

        public string PauseLabel => IsPaused ? Strings.OptimizeResumeButton : Strings.OptimizePauseButton;

        [ObservableProperty]
        public partial double ProgressValue { get; set; }

        [ObservableProperty]
        public partial bool IsProgressIndeterminate { get; set; }

        /// <summary>What's going on, for the footer.</summary>
        [ObservableProperty]
        public partial string StatusText { get; set; } = string.Empty;

        /// <summary>The Optimize button, naming how many charts it changes.</summary>
        [ObservableProperty]
        public partial string OptimizeLabel { get; set; } = Strings.OptimizeButton;

        /// <summary>Why Optimize can't be used yet; empty when it can.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(OptimizeToolTip))]
        public partial string BlockedText { get; set; } = string.Empty;

        /// <summary>Why Optimize can't be used, for its tooltip; <see langword="null"/>, and so no tooltip, when it can.</summary>
        public string? OptimizeToolTip => BlockedText.Length > 0 ? BlockedText : null;

        /// <summary>Why the output folder can't be used, or empty when it can, or hasn't been chosen yet.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasOutputFolderProblem))]
        public partial string OutputFolderProblemText { get; set; } = string.Empty;

        public bool HasOutputFolderProblem => OutputFolderProblemText.Length > 0;

        /// <summary>A run wrote charts to the output folder, which can be opened.</summary>
        public bool CanOpenOutput => IsDone && _written > 0 && !string.IsNullOrWhiteSpace(Settings.OutputFolder);

        /// <summary>What the window changed on disk, for the library to catch up with.</summary>
        public OptimizeWindowResult Result => new(_changedInPlace > 0, _written > 0);

        // ---- Opening and closing ------------------------------------------------------------------------------

        /// <summary>Lists the charts and starts reading them.</summary>
        public void Load()
        {
            _refresh.Start();
            LoadScope();
        }

        /// <summary>Stops everything going on and waits for it, once the window's closed; then keeps the settings and what was measured.</summary>
        public async Task ShutdownAsync()
        {
            // The window's gone already, so there's nothing left to close once the run ends.
            _closeWhenIdle = false;
            _shutDown = true;
            foreach (var row in _rows)
                row.ClearPreviews();
            _refresh.Stop();
            _settle.Stop();
            _closing.Cancel();
            try
            {
                await _current;
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException)
            {
            }

            _defaults.Save(Settings.ToSettings());
            await Task.Run(_cache.Save);
        }

        /// <summary>The window is closing some other way: a run still going is stopped, and the window closes once it has.</summary>
        /// <returns>Whether the window may close now.</returns>
        public bool OnClosing()
        {
            if (!IsRunning)
                return true;

            _closeWhenIdle = true;
            _run?.Cancel();
            return false;
        }

        // ---- Settings -----------------------------------------------------------------------------------------

        private void OnSettingsChanged()
        {
            if (_shutDown)
                return;

            RefreshOutputFolderProblem();
            RefreshOptimizeState();
            _settle.Stop();
            _settle.Start();
        }

        // What's measured depends on every setting but where the charts go and how many are optimized at once.
        private static OptimizeOptions Measuring(OptimizeOptions options) => options with { OutputFolder = null, Parallelism = 1 };

        private void OnSettingsSettled()
        {
            if (_shutDown || IsRunning)
                return;
            if (_measuredOptions is { } measured && measured == Measuring(Settings.ToOptions()))
                return;

            StartWork();
        }

        /// <summary>Puts every setting back to the shipped ones, keeping where the charts are written.</summary>
        [RelayCommand(CanExecute = nameof(IsNotRunning))]
        private void Reset()
        {
            var shipped = OptimizeSettings.Shipped;
            shipped.UseOutputFolder = Settings.UseOutputFolder;
            shipped.OutputFolder = Settings.OutputFolder;
            Settings.Apply(shipped);
        }

        [RelayCommand(CanExecute = nameof(IsNotRunning))]
        private async Task BrowseOutputAsync()
        {
            string? folder = await _folderPicker.PickFolderAsync(Strings.OptimizeBrowseOutputTitle, Settings.OutputFolder);
            if (folder is null)
                return;

            Settings.OutputFolder = folder;
            Settings.UseOutputFolder = true;
        }

        [RelayCommand]
        private void OpenOutput()
        {
            if (Settings.OutputFolder is { Length: > 0 } folder)
                _reveal.OpenFolder(folder);
        }

        private void RefreshOutputFolderProblem()
        {
            string problem = string.Empty;
            if (Settings.UseOutputFolder && !string.IsNullOrWhiteSpace(Settings.OutputFolder))
            {
                try
                {
                    if (!Path.IsPathRooted(Settings.OutputFolder))
                    {
                        problem = Strings.OptimizeOutputFolderNotFullPath;
                    }
                    else
                    {
                        string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Settings.OutputFolder));
                        // A chart written into itself, or over another, isn't a copy of it.
                        foreach (var row in _rows)
                        {
                            string chart = Path.TrimEndingDirectorySeparator(row.Path);
                            if (folder.Equals(chart, StringComparison.OrdinalIgnoreCase)
                                || (row.Entry.Kind != SongEntryKind.SngFile
                                    && folder.StartsWith(chart + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                            {
                                problem = Strings.OptimizeOutputFolderInsideChart;
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    problem = Strings.OptimizeOutputFolderNotFullPath;
                }
            }

            OutputFolderProblemText = problem;
        }

        // ---- The charts ---------------------------------------------------------------------------------------

        private void LoadScope()
        {
            foreach (var row in _rows)
                row.ClearPreviews();
            var scope = UseWholeLibrary ? _request.Library : _request.Selected;
            _rows = [.. scope.Select(e => new OptimizeChartRow(e))];
            _byPath = new Dictionary<string, OptimizeChartRow>(OutputPaths.PathComparer);
            foreach (var row in _rows)
                _byPath.TryAdd(row.Path, row);

            SelectedRow = null;
            HasNoCharts = _rows.Count == 0;
            ErrorText = string.Empty;
            RefreshOutputFolderProblem();
            ShowRows();
            StartWork();
        }

        partial void OnSearchTextChanged(string value) => ShowRows();

        partial void OnSelectedSortChanged(OptimizeChoice<OptimizeSort> value) => ShowRows();

        partial void OnSelectedRowChanged(OptimizeChartRow? oldValue, OptimizeChartRow? newValue)
        {
            if (oldValue is not null)
            {
                oldValue.ClearPreviews();
                oldValue.IsExpanded = false;
            }

            if (newValue is null)
                return;

            newValue.IsExpanded = true;
            ShowPreviews(newValue);
            // The chart being looked at is measured next, so its own figure replaces the prediction.
            if (newValue.Plan is null)
                _estimator?.Prioritize(newValue.Entry);
        }

        // Shows the open chart's images and video before and after, for the ones its plan changes. Not during a run, which
        // may be rewriting the very files a preview would hold open.
        private void ShowPreviews(OptimizeChartRow row)
        {
            if (_backends.Previews is not { } previewer || IsRunning || _shutDown || !row.IsExpanded
                || row.Plan is not { } plan || row.Assets is not { } assets || row.IsStale)
            {
                row.ClearPreviews();
                return;
            }

            var options = Settings.ToOptions();
            var previews = new List<OptimizeMediaPreviewViewModel>();
            IMediaPreviewSession? session = null;
            foreach (var action in plan.Work)
            {
                if (action.Kind is not (OptimizeActionKind.Recompress or OptimizeActionKind.Transcode) || action.SourceNames.Count == 0
                    || action.Strategy is not (OptimizeStrategy.Video or OptimizeStrategy.AlbumArt or OptimizeStrategy.BackgroundArt))
                    continue;
                if (assets.Assets.FirstOrDefault(a => a.StoredName.Equals(action.SourceNames[0], StringComparison.OrdinalIgnoreCase)) is not { } asset)
                    continue;

                var opened = session ??= previewer.Open(row.Entry);
                previews.Add(action.Strategy switch
                {
                    OptimizeStrategy.Video => new OptimizeMediaPreviewViewModel(
                        asset.StoredName, true,
                        (frame, ct) => opened.VideoFrameAsync(asset, options.Video, (frame + 0.5) / OptimizeMediaPreviewViewModel.VideoFrames, ct)),
                    OptimizeStrategy.AlbumArt => new OptimizeMediaPreviewViewModel(asset.StoredName, false, (_, ct) => opened.ImageAsync(asset, options.AlbumArt, ct)),
                    _ => new OptimizeMediaPreviewViewModel(asset.StoredName, false, (_, ct) => opened.ImageAsync(asset, options.BackgroundArt, ct)),
                });
            }

            row.SetPreviews(previews, session);
        }

        [RelayCommand]
        private void ClearSearch() => SearchText = string.Empty;

        [RelayCommand]
        private void ClearFilters()
        {
            SearchText = string.Empty;
            foreach (var chip in Chips)
                chip.IsOn = false;
        }

        // Shows the charts that match the search and the chips that are on, in the chosen order.
        private void ShowRows()
        {
            _resort = false;
            string search = SearchText.Trim();
            var groups = Chips.Where(c => c.IsOn).Select(c => c.Group).ToHashSet();
            IEnumerable<OptimizeChartRow> shown = _rows;
            if (search.Length > 0)
                shown = shown.Where(r => r.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase));
            if (groups.Count > 0)
                shown = shown.Where(r => groups.Contains(r.Group));

            var name = StringComparer.CurrentCultureIgnoreCase;
            IEnumerable<OptimizeChartRow> ordered = SelectedSort.Value switch
            {
                OptimizeSort.Size => shown.OrderByDescending(r => r.SizeBytes).ThenBy(r => r.Name, name),
                OptimizeSort.Name => shown.OrderBy(r => r.Name, name),
                OptimizeSort.Problems => shown.OrderBy(r => r.Group == OptimizeRowGroup.Problem ? 0 : 1).ThenByDescending(r => r.SavingBytes ?? long.MinValue).ThenBy(r => r.Name, name),
                _ => shown.OrderByDescending(r => r.SavingBytes ?? long.MinValue).ThenByDescending(r => r.SizeBytes).ThenBy(r => r.Name, name),
            };

            var selected = SelectedRow;
            var next = ordered.ToList();
            ListRows.Replace(Rows, next);
            HasNoMatches = _rows.Count > 0 && next.Count == 0;
            // Replacing the rows can drop the selection even where its row stayed.
            if (selected is not null && !ReferenceEquals(SelectedRow, selected))
                SelectedRow = next.Contains(selected) ? selected : null;
        }

        // ---- Reading and measuring ----------------------------------------------------------------------------

        // Starts the background work again from the charts and settings as they are now: what was going on is stopped first.
        private void StartWork()
        {
            if (IsRunning || _shutDown)
                return;

            int generation = ++_generation;
            _work?.Cancel();
            var cts = _work = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
            var previous = _pipeline;
            _pipeline = Track(WorkAsync(previous, cts, generation));
        }

        private async Task StopWorkAsync()
        {
            _generation++;
            _work?.Cancel();
            try
            {
                await _pipeline;
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task WorkAsync(Task previous, CancellationTokenSource cts, int generation)
        {
            try
            {
                try
                {
                    await previous;
                }
                catch (OperationCanceledException)
                {
                }

                // A later change has already asked for the work to start again; that one does it.
                if (generation != _generation)
                    return;

                // A pause is kept: the work starting again, after a setting changed, isn't the user carrying on.
                var ct = cts.Token;
                ErrorText = string.Empty;
                await ReadAsync(ct);
                await MeasureAsync(ct);
                if (generation == _generation)
                {
                    Phase = OptimizePhase.Ready;
                    IsPaused = false;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                // Whatever went wrong, it's shown: this isn't awaited by anything that would see it.
                System.Diagnostics.Trace.TraceError($"Optimize window measuring failed: {ex}");
                ErrorText = string.Format(CultureInfo.CurrentCulture, Strings.OptimizeFailedFormat, ex.Message);
                if (generation == _generation)
                {
                    Phase = OptimizePhase.Ready;
                    IsPaused = false;
                }
            }
            finally
            {
                if (ReferenceEquals(_work, cts))
                    _work = null;
                cts.Dispose();
                _dirty = true;
            }
        }

        // Lists the files of every chart that hasn't been read, or was rewritten since.
        private async Task ReadAsync(CancellationToken cancellationToken)
        {
            var unread = _rows.Where(r => r.State == OptimizeRowState.Reading || r.IsStale).ToList();
            _read = _rows.Count - unread.Count;
            if (unread.Count == 0)
                return;

            Phase = OptimizePhase.Reading;
            foreach (var row in unread)
            {
                row.Assets = null;
                row.Plan = null;
                row.Savings = null;
                row.Result = null;
                row.Error = null;
                row.State = OptimizeRowState.Reading;
                row.Refresh();
            }

            var probes = _backends.Probes;
            var queue = new UiBatchQueue<(OptimizeChartRow Row, ChartAssets? Assets, string? Error)>(TimeSpan.FromMilliseconds(100), TakeRead, TimeSpan.FromMilliseconds(30));
            queue.Start();
            try
            {
                await Task.Run(
                    () => Parallel.ForEachAsync(
                        unread,
                        new ParallelOptions { MaxDegreeOfParallelism = OptimizeOptions.RecommendedMeasuringParallelism, CancellationToken = cancellationToken },
                        async (row, token) =>
                        {
                            try
                            {
                                queue.Report((row, await AssetInspector.InspectAsync(row.Entry, probes, token).ConfigureAwait(false), null));
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                            {
                                // A chart that can't be read is left out, and says so in the list.
                                queue.Report((row, null, ex.Message));
                            }
                        }),
                    cancellationToken);
            }
            finally
            {
                queue.Stop();
                queue.TakeAll();
            }

            RefreshOutputFolderProblem();
        }

        private void TakeRead(IEnumerable<(OptimizeChartRow Row, ChartAssets? Assets, string? Error)> batch)
        {
            foreach (var (row, assets, error) in batch)
            {
                row.Assets = assets;
                row.Error = error;
                row.IsStale = false;
                row.State = assets is null ? OptimizeRowState.Unreadable : OptimizeRowState.Unknown;
                row.Refresh();
                _read++;
            }

            _dirty = true;
        }

        // Measures every chart with the settings as they are, predicting the ones not reached yet.
        private async Task MeasureAsync(CancellationToken cancellationToken)
        {
            var options = Settings.ToOptions();
            _measuredOptions = Measuring(options);
            var readable = _rows.Where(r => r.IsReadable).ToList();
            var charts = readable.Select(r => r.Assets!).ToList();

            // What each chart has for the strategies that are on: what its prediction is made from.
            var units = await Task.Run(() => charts.Select(c => OptimizationPlanner.UnitsOf(c, options)).ToList(), cancellationToken);
            var model = new SavingsModel(options);
            _model = model;
            _modelVersion = -1;
            _sampleShown = false;
            _measured = 0;
            _toMeasure = readable.Count;
            for (int i = 0; i < readable.Count; i++)
            {
                var row = readable[i];
                row.ClearPreviews();
                row.Units = units[i];
                row.Plan = null;
                row.Result = null;
                row.Error = null;
                row.Savings = null;
                row.State = OptimizeRowState.Unknown;
                row.Refresh();
            }

            ClearRunTotals();
            _resort = true;
            _dirty = true;
            if (!Settings.AnyOn || readable.Count == 0)
            {
                // Nothing is asked of the charts, so there's nothing to predict: they're listed with their sizes.
                _model = null;
                foreach (var row in readable)
                {
                    row.State = OptimizeRowState.Listed;
                    row.Refresh();
                }

                return;
            }

            Phase = OptimizePhase.Measuring;
            var optimizers = Optimizers(options);
            var services = new PlanServices(_cache, _videoGate);
            var estimator = await Task.Run(
                () => new LibraryEstimator(charts, options, optimizers, services, model, OptimizeOptions.RecommendedMeasuringParallelism), cancellationToken);
            _estimator = estimator;
            // Measuring that was paused stays paused, though it's a new estimator's to do.
            if (IsPaused)
                estimator.Pause();
            if (SelectedRow is { Plan: null } looking)
                estimator.Prioritize(looking.Entry);

            var queue = new UiBatchQueue<ChartEstimate>(TimeSpan.FromMilliseconds(100), TakeMeasured, TimeSpan.FromMilliseconds(30));
            queue.Start();
            try
            {
                await Task.Run(() => estimator.RunAsync(queue, cancellationToken), cancellationToken);
            }
            finally
            {
                queue.Stop();
                queue.TakeAll();
                if (ReferenceEquals(_estimator, estimator))
                    _estimator = null;
                // What was measured is kept even when the measuring was stopped part-way.
                _ = Track(Task.Run(_cache.Save));
            }

            _resort = true;
        }

        private void TakeMeasured(IEnumerable<ChartEstimate> batch)
        {
            foreach (var estimate in batch)
            {
                if (!_byPath.TryGetValue(estimate.Chart.Entry.FullPath, out var row) || !ReferenceEquals(row.Assets, estimate.Chart))
                    continue;

                Measured(row, estimate.Plan, estimate.Error);
                if (ReferenceEquals(row, SelectedRow))
                    ShowPreviews(row);
                _measured++;
                // The first predictions are in: the list is worth putting in order now.
                if (estimate.SampleComplete && !_sampleShown)
                {
                    _sampleShown = true;
                    _resort = true;
                }
            }

            _dirty = true;
        }

        private static void Measured(OptimizeChartRow row, ChartPlan? plan, string? error)
        {
            row.Plan = plan;
            row.Error = error;
            row.Savings = plan is null ? new ChartSavings() : SavingsModel.Measured(plan);
            row.State = OptimizeRowState.Measured;
            row.Refresh();
        }

        [RelayCommand(CanExecute = nameof(IsMeasuring))]
        private void TogglePause()
        {
            if (_estimator is not { } estimator)
                return;

            if (IsPaused)
                estimator.Resume();
            else
                estimator.Pause();
            IsPaused = estimator.IsPaused;
            _dirty = true;
        }

        // ---- Catching the window up ---------------------------------------------------------------------------

        // Runs a few times a second: predictions, totals and the order of the list, when something's changed.
        private void RefreshView()
        {
            var model = _model;
            bool learned = model is not null && model.Version != _modelVersion && !IsRunning;
            if (!_dirty && !learned)
                return;

            _dirty = false;
            if (learned)
            {
                _modelVersion = model!.Version;
                foreach (var row in _rows)
                {
                    if (row.Plan is not null || !row.IsReadable || row.State is not (OptimizeRowState.Unknown or OptimizeRowState.Predicted))
                        continue;

                    var predicted = model.Predict(row.Units);
                    row.Savings = predicted;
                    // A chart with a file of a type nothing's known about yet has no figure worth showing.
                    row.State = predicted.IsComplete ? OptimizeRowState.Predicted : OptimizeRowState.Unknown;
                    row.Refresh();
                }
            }

            RefreshSummary();
            RefreshOptimizeState();
            if (_resort)
                ShowRows();
        }

        private void RefreshSummary()
        {
            long before = 0, measured = 0, predicted = 0;
            int unreadable = 0, known = 0, shrinks = 0, nothing = 0, problems = 0;
            var byStrategy = new long[Settings.Toggles.Count];
            var predictedStrategy = new bool[Settings.Toggles.Count];
            foreach (var row in _rows)
            {
                switch (row.Group)
                {
                    case OptimizeRowGroup.Shrinks: shrinks++; break;
                    case OptimizeRowGroup.Problem: problems++; break;
                    default: nothing++; break;
                }

                if (!row.IsReadable)
                {
                    if (row.State == OptimizeRowState.Unreadable)
                        unreadable++;
                    continue;
                }

                before += row.SizeBytes;
                if (row.State == OptimizeRowState.Saved)
                {
                    measured += row.Result?.BytesSaved ?? 0;
                    continue;
                }

                if (IsDone || row.Savings is not { } savings)
                    continue;

                known++;

                if (row.Plan is null)
                    predicted += savings.TotalBytes;
                else
                    measured += savings.TotalBytes;
                for (int i = 0; i < byStrategy.Length; i++)
                {
                    long bytes = savings[Settings.Toggles[i].Strategy];
                    byStrategy[i] += bytes;
                    predictedStrategy[i] |= row.Plan is null && bytes != 0;
                }
            }

            long saving = measured + predicted;
            // Until something's been measured there's no saving to show, not a saving of nothing.
            bool any = _rows.Count > 0 && before > 0 && (IsDone || IsRunning || (Settings.AnyOn && known > 0));
            HasSaving = any;
            SizesText = before > 0
                ? any
                    ? string.Format(CultureInfo.CurrentCulture, Strings.OptimizeSummarySizesFormat, Size(before), Size(before - saving))
                    : Size(before)
                : string.Empty;
            SavingText = !any ? string.Empty
                : IsDone ? string.Format(CultureInfo.CurrentCulture, Strings.OptimizeStatusSavedFormat, Size(saving))
                : saving >= 0 ? string.Format(CultureInfo.CurrentCulture, Strings.OptimizeRowSavesFormat, Size(saving))
                : string.Format(CultureInfo.CurrentCulture, Strings.OptimizeRowGrowsFormat, Size(-saving));
            PercentText = any && saving > 0
                ? string.Format(CultureInfo.CurrentCulture, Strings.OptimizePercentFormat, Math.Max(1, (int)Math.Round(100.0 * saving / before)))
                : string.Empty;
            BarKept = before > 0 ? Math.Clamp(1 - ((double)Math.Max(0, saving) / before), 0, 1) : 0;
            BarSaved = before > 0 ? Math.Clamp((double)Math.Max(0, measured) / before, 0, 1) : 0;
            BarPredicted = before > 0 ? Math.Clamp((double)Math.Max(0, predicted) / before, 0, 1) : 0;

            for (int i = 0; i < byStrategy.Length; i++)
            {
                var toggle = Settings.Toggles[i];
                // A strategy with nothing known of it yet shows no figure, rather than a nought that isn't one.
                bool show = toggle.IsOn && !IsDone && toggle.Strategy != OptimizeStrategy.Metadata
                    && (byStrategy[i] != 0 || (_toMeasure > 0 && _measured >= _toMeasure));
                string amount = byStrategy[i] >= 0 ? Size(byStrategy[i])
                    : string.Format(CultureInfo.CurrentCulture, Strings.OptimizeRowGrowsFormat, Size(-byStrategy[i]));
                toggle.IsSavingPredicted = show && predictedStrategy[i];
                toggle.SavingText = !show ? string.Empty
                    : toggle.IsSavingPredicted ? string.Format(CultureInfo.CurrentCulture, Strings.OptimizeStrategySavingPredictedFormat, amount)
                    : amount;
            }

            Chips[0].Label = string.Format(CultureInfo.CurrentCulture, IsDone ? Strings.OptimizeChipSavedFormat : Strings.OptimizeChipShrinksFormat, shrinks);
            Chips[1].Label = string.Format(CultureInfo.CurrentCulture, IsDone ? Strings.OptimizeChipLeftFormat : Strings.OptimizeChipNothingFormat, nothing);
            Chips[2].Label = string.Format(CultureInfo.CurrentCulture, Strings.OptimizeChipProblemsFormat, problems);
            Chips[2].IsAlert = problems > 0;

            CoverageText = Coverage(unreadable);
            RefreshProgress();
        }

        private string Coverage(int unreadable)
        {
            string text;
            switch (Phase)
            {
                case OptimizePhase.Reading:
                    text = string.Format(CultureInfo.CurrentCulture, Strings.OptimizeCoverageReadingFormat, _read, _rows.Count);
                    break;

                case OptimizePhase.Running:
                    text = string.Format(CultureInfo.CurrentCulture, Strings.OptimizeResultSummaryFormat, _applied, _skipped, _failed);
                    break;

                case OptimizePhase.Done:
                    text = string.Format(CultureInfo.CurrentCulture, Strings.OptimizeResultSummaryFormat, _applied, _skipped, _failed);
                    if (_applied > 0)
                        text += Strings.OptimizeDetailSeparator + string.Format(CultureInfo.CurrentCulture, Strings.OptimizeDoneEstimateFormat, Size(_bytesEstimated));
                    if (_stopped)
                        text = Strings.OptimizeStopped + " " + text;
                    break;

                default:
                    if (_rows.Count == 0)
                        return string.Empty;
                    if (!Settings.AnyOn)
                        text = Strings.OptimizeNothingOn;
                    else if (_measured >= _toMeasure)
                        text = Strings.OptimizeCoverageDone;
                    else
                        text = string.Format(CultureInfo.CurrentCulture, Strings.OptimizeCoverageMeasuringFormat, _measured, _toMeasure);
                    break;
            }

            return unreadable == 0 ? text : text + Strings.OptimizeDetailSeparator + string.Format(CultureInfo.CurrentCulture, Strings.OptimizeUnreadableFormat, unreadable);
        }

        private void RefreshProgress()
        {
            switch (Phase)
            {
                case OptimizePhase.Reading:
                    IsProgressIndeterminate = false;
                    ProgressValue = _rows.Count == 0 ? 0 : 100.0 * _read / _rows.Count;
                    StatusText = string.Format(CultureInfo.CurrentCulture, Strings.OptimizeInspectingFormat, _read, _rows.Count);
                    break;

                case OptimizePhase.Measuring:
                    IsProgressIndeterminate = false;
                    ProgressValue = _toMeasure == 0 ? 0 : 100.0 * _measured / _toMeasure;
                    StatusText = IsPaused
                        ? string.Format(CultureInfo.CurrentCulture, Strings.OptimizeStatusPausedFormat, _measured, _toMeasure)
                        : string.Format(CultureInfo.CurrentCulture, Strings.OptimizeStatusMeasuringFormat, _measured, _toMeasure);
                    break;

                case OptimizePhase.Running:
                    IsProgressIndeterminate = _toRun == 0;
                    ProgressValue = _toRun == 0 ? 0 : 100.0 * _finished / _toRun;
                    break;

                default:
                    IsProgressIndeterminate = false;
                    ProgressValue = 0;
                    StatusText = string.Empty;
                    break;
            }
        }

        private void RefreshOptimizeState()
        {
            int charts = _rows.Count(r => r.MayChange);
            // While a run goes the button stays where it is, greyed out and saying so, so it can't be pressed twice.
            OptimizeLabel = IsRunning ? Strings.OptimizeBusy
                : _rows.Count == 0 || !Settings.AnyOn || charts == 0 ? Strings.OptimizeButton
                : charts == 1 ? Strings.OptimizeButtonOne
                : string.Format(CultureInfo.CurrentCulture, Strings.OptimizeButtonFormat, charts);

            BlockedText = IsRunning ? string.Empty
                : _rows.Count == 0 ? Strings.OptimizeNoCharts
                : !Settings.AnyOn ? Strings.OptimizeBlockedNothingOn
                : Phase == OptimizePhase.Reading ? Strings.OptimizeBlockedReading
                : Settings.UseOutputFolder && string.IsNullOrWhiteSpace(Settings.OutputFolder) ? Strings.OptimizeBlockedNoFolder
                : HasOutputFolderProblem ? OutputFolderProblemText
                : charts == 0 ? (IsDone ? Strings.OptimizeBlockedDone : Strings.OptimizeBlockedNothingToDo)
                : string.Empty;
            OptimizeCommand.NotifyCanExecuteChanged();
        }

        private static string Size(long bytes) => ByteSizeFormatter.Format((ulong)Math.Max(0, bytes));

        // ---- Optimizing ---------------------------------------------------------------------------------------

        private bool CanOptimize() => !IsRunning && BlockedText.Length == 0 && _rows.Count > 0;

        /// <summary>Optimizes every chart, in place or into the output folder, with the strategies that are on.</summary>
        [RelayCommand(CanExecute = nameof(CanOptimize))]
        private async Task OptimizeAsync()
        {
            if (Settings.IsInPlace)
            {
                string message = string.Format(CultureInfo.CurrentCulture, Strings.OptimizeConfirmMessageFormat, _rows.Count(r => r.MayChange));
                if (!await _confirmation.ConfirmAsync(Strings.OptimizeConfirmTitle, message, Strings.OptimizeConfirmButton))
                    return;
            }

            _defaults.Save(Settings.ToSettings());
            await Track(RunAsync());
        }

        private void ClearRunTotals()
        {
            _finished = _toRun = _applied = _skipped = _failed = 0;
            _bytesSaved = _bytesEstimated = 0;
            _stopped = false;
        }

        private async Task RunAsync()
        {
            _settle.Stop();
            // The window shows the run from the press: stopping what's being measured can take a moment.
            SelectedRow?.ClearPreviews();
            Phase = OptimizePhase.Running;
            IsPaused = false;
            StatusText = Strings.OptimizeRunStopping;
            await StopWorkAsync();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
            _run = cts;
            var ct = cts.Token;
            var options = Settings.ToOptions();
            // Plans made with other settings aren't this run's.
            if (_measuredOptions != Measuring(options))
            {
                foreach (var row in _rows)
                    row.Plan = null;
                _measuredOptions = Measuring(options);
            }

            ClearRunTotals();
            ErrorText = string.Empty;
            // A chart an earlier run already made smaller keeps its result, and isn't run again. The charts with the most
            // to save go first, which is the order the list starts in, so the work shows where it's being looked for.
            var running = _rows.Where(r => r.IsReadable && !r.IsStale && r.State != OptimizeRowState.Saved)
                .OrderByDescending(r => r.Savings?.TotalBytes ?? 0).ThenByDescending(r => r.SizeBytes).ToList();
            _toRun = running.Count;
            foreach (var row in running)
            {
                row.Result = null;
                row.State = OptimizeRowState.Waiting;
                row.Refresh();
            }

            _dirty = true;
            var optimizers = Optimizers(options);
            var queue = new UiBatchQueue<OptimizeProgress>(TimeSpan.FromMilliseconds(100), TakeRun, TimeSpan.FromMilliseconds(30));
            queue.Start();
            try
            {
                // The charts already measured go first, as they are: their plans are what was shown.
                var planned = running.Where(r => r.Plan is not null).ToList();
                if (planned.Count > 0)
                    await Task.Run(() => OptimizeJob.RunAsync([.. planned.Select(r => r.Plan!)], options, optimizers, _services, queue, ct), ct);

                // The rest are measured a batch at a time, and each batch optimized before the next is measured.
                var services = new PlanServices(_cache, _videoGate);
                foreach (var batch in running.Where(r => r.Plan is null).Chunk(RunBatch))
                {
                    ct.ThrowIfCancellationRequested();
                    foreach (var row in batch)
                    {
                        row.State = OptimizeRowState.Measuring;
                        row.Refresh();
                    }

                    StatusText = string.Format(CultureInfo.CurrentCulture, Strings.OptimizeRunMeasuringFormat, _finished, _toRun);
                    var plans = await Task.Run(() => PlanAsync(batch, options, optimizers, services, ct), ct);
                    var ready = new List<ChartPlan>();
                    for (int i = 0; i < batch.Length; i++)
                    {
                        if (plans[i].Plan is { } plan)
                        {
                            batch[i].Plan = plan;
                            batch[i].Savings = SavingsModel.Measured(plan);
                            batch[i].State = OptimizeRowState.Waiting;
                            batch[i].Refresh();
                            ready.Add(plan);
                        }
                        else
                        {
                            Finished(batch[i], OptimizeRowState.Failed, plans[i].Error);
                        }
                    }

                    _dirty = true;
                    if (ready.Count > 0)
                        await Task.Run(() => OptimizeJob.RunAsync(ready, options, optimizers, _services, queue, ct), ct);
                }
            }
            catch (OperationCanceledException)
            {
                _stopped = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"Optimize run failed: {ex}");
                ErrorText = string.Format(CultureInfo.CurrentCulture, Strings.OptimizeFailedFormat, ex.Message);
                _stopped = true;
            }
            finally
            {
                // What was reported before a stop or a failure still counts.
                queue.Stop();
                queue.TakeAll();
                _run = null;

                // The charts the run didn't reach go back to what was known of them.
                foreach (var row in running.Where(r => r.State is OptimizeRowState.Waiting or OptimizeRowState.Working or OptimizeRowState.Measuring))
                {
                    row.State = row.Plan is not null ? OptimizeRowState.Measured
                        : row.Savings is { IsComplete: true } ? OptimizeRowState.Predicted
                        : OptimizeRowState.Unknown;
                    row.Refresh();
                }

                Phase = OptimizePhase.Done;
                OnPropertyChanged(nameof(CanOpenOutput));
                if (SelectedRow is { } open && !_shutDown)
                    ShowPreviews(open);
                _resort = true;
                _dirty = true;
                RefreshView();
                _ = Track(Task.Run(_cache.Save));

                // The window was closed while this was going on, so it closes now that it has stopped.
                if (_closeWhenIdle)
                    CloseRequested?.Invoke();
            }
        }

        // Plans the charts of a batch, several at once. One that can't be planned has its error instead, and doesn't stop the rest.
        private static async Task<(ChartPlan? Plan, string? Error)[]> PlanAsync(
            OptimizeChartRow[] batch, OptimizeOptions options, OptimizerSet optimizers, PlanServices services, CancellationToken cancellationToken)
        {
            var plans = new (ChartPlan? Plan, string? Error)[batch.Length];
            await Parallel.ForEachAsync(
                Enumerable.Range(0, batch.Length),
                new ParallelOptions { MaxDegreeOfParallelism = OptimizeOptions.RecommendedMeasuringParallelism, CancellationToken = cancellationToken },
                async (i, token) =>
                {
                    try
                    {
                        plans[i] = (await OptimizationPlanner.PlanAsync(batch[i].Assets!, options, optimizers, services, token).ConfigureAwait(false), null);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        plans[i] = (null, ex.Message);
                    }
                }).ConfigureAwait(false);
            return plans;
        }

        private void TakeRun(IEnumerable<OptimizeProgress> batch)
        {
            foreach (var report in batch)
            {
                if (report.Current is not { } entry || !_byPath.TryGetValue(entry.FullPath, out var row))
                    continue;

                if (report.Completed is { } item)
                {
                    Record(row, item);
                    continue;
                }

                row.State = OptimizeRowState.Working;
                row.WorkingFile = report.CurrentAsset;
                row.WorkingFraction = report.FileFraction ?? 0;
                row.Refresh();
                StatusText = report.CurrentAsset is { } asset
                    ? string.Format(CultureInfo.CurrentCulture, Strings.OptimizeRunningAssetFormat, entry.DisplayName, asset)
                    : string.Format(CultureInfo.CurrentCulture, Strings.OptimizeRunningFormat, _finished, _toRun);
            }

            _dirty = true;
        }

        private void Record(OptimizeChartRow row, OptimizeItemResult item)
        {
            row.Result = item;
            switch (item.Status)
            {
                case OptimizeItemStatus.Succeeded:
                    _applied++;
                    _bytesSaved += item.BytesSaved;
                    _bytesEstimated += row.EstimatedSavingBytes ?? 0;
                    if (Settings.IsInPlace)
                    {
                        _changedInPlace++;
                        // Its files aren't what was read any more.
                        row.IsStale = true;
                    }
                    else
                    {
                        _written++;
                    }

                    Finished(row, OptimizeRowState.Saved, null);
                    break;

                case OptimizeItemStatus.Failed:
                    Finished(row, OptimizeRowState.Failed, item.ErrorMessage);
                    break;

                default:
                    _skipped++;
                    _finished++;
                    row.State = OptimizeRowState.Skipped;
                    row.Refresh();
                    break;
            }
        }

        private void Finished(OptimizeChartRow row, OptimizeRowState state, string? error)
        {
            if (state == OptimizeRowState.Failed)
                _failed++;
            _finished++;
            row.Error = error;
            row.State = state;
            row.Refresh();
        }

        /// <summary>Stops the run; charts already finished keep their changes.</summary>
        [RelayCommand(CanExecute = nameof(IsRunning))]
        private void Stop() => _run?.Cancel();

        [RelayCommand(CanExecute = nameof(IsNotRunning))]
        private void Close() => CloseRequested?.Invoke();

        // ---- Shared -------------------------------------------------------------------------------------------

        private OptimizerSet Optimizers(OptimizeOptions options)
        {
            // One set for the window, as the image optimizer keeps what it encoded to estimate, to write when it's applied.
            // The video optimizer splits the cores for the parallelism it was made for, so a set is made again if that changes.
            if (_optimizers is null || _optimizersParallelism != options.Parallelism)
            {
                _optimizers = _backends.CreateOptimizers(options.Parallelism);
                _optimizersParallelism = options.Parallelism;
            }

            return _optimizers;
        }

        // Keeps the running task, so shutting down can wait for it.
        private Task Track(Task task)
        {
            _current = Task.WhenAll(_current, task);
            return task;
        }
    }
}
