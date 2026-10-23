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
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charting.Authoring.Generation;
using ChartShared.Core.Charting.Saving;
using ChartShared.Core.Charts;
using ChartShared.Core.Metadata;
using ChartShared.Core.Scanning;
using ChartShared.Core.Settings;
using ChartShared.Core.Validation;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace ChartPacker.ViewModels
{
    /// <summary>A tab of the Generate lower difficulties window's list: the songs in one state, and how many.</summary>
    public sealed partial class LowerDifficultiesTab(LowerDifficultiesState? state, string name, Action<LowerDifficultiesTab> picked) : ObservableObject
    {
        /// <summary>The state of the songs it lists; null for every song.</summary>
        public LowerDifficultiesState? State { get; } = state;

        public string Name { get; } = name;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Label))]
        public partial int Count { get; set; }

        public string Label => string.Format(CultureInfo.CurrentCulture, Strings.LowerDifficultiesTabFormat, Name, Count);

        [ObservableProperty]
        public partial bool IsSelected { get; set; }

        /// <summary>Whether it lists <paramref name="row"/>.</summary>
        public bool Lists(LowerDifficultiesSongRow row) => State is not { } state || row.State == state;

        partial void OnIsSelectedChanged(bool value)
        {
            if (value)
                picked(this);
        }
    }

    /// <summary>A difficulty the window fills: Hard, Medium or Easy, coloured as the games colour it; it can be left out.</summary>
    public sealed partial class LowerDifficultiesTarget(ChartDifficulty difficulty, Action changed) : ObservableObject
    {
        public ChartDifficulty Difficulty { get; } = difficulty;

        public string Name { get; } = ChartLabels.DifficultyName(difficulty);

        public bool IsHard => Difficulty == ChartDifficulty.Hard;

        public bool IsMedium => Difficulty == ChartDifficulty.Medium;

        public bool IsEasy => Difficulty == ChartDifficulty.Easy;

        [ObservableProperty]
        public partial bool IsIncluded { get; set; } = true;

        partial void OnIsIncludedChanged(bool value) => changed();
    }

    /// <summary>A part the window fills, with its instrument's icon; it can be left out.</summary>
    public sealed partial class LowerDifficultiesPart(ChartInstrument part, Action changed) : ObservableObject
    {
        public ChartInstrument Part { get; } = part;

        public string Name { get; } = ChartLabels.InstrumentName(part);

        public DifficultyGlyph Glyph { get; } = PartPips.GlyphOf(part);

        [ObservableProperty]
        public partial bool IsIncluded { get; set; } = true;

        partial void OnIsIncludedChanged(bool value) => changed();
    }

    /// <summary>
    /// The Generate lower difficulties window (issue #120): the library's selected songs, each with its parts' lower
    /// difficulties as they'd be filled from the ones above them, by the reduction rules Chart Builder's Reduce uses. Which
    /// difficulties and parts are filled, and whether those already charted are made again, are the batch's to pick; each
    /// song is read once, and planned again in memory as they change (<see cref="SongDifficultySurvey"/>). The songs are
    /// listed by where they stand, a tab each, so a batch of hundreds shows only what needs looking at. Generate stages the
    /// new charts in the songs' drafts; Save writes them.
    /// </summary>
    public sealed partial class LowerDifficultiesViewModel : ViewModelBase, IDisposable
    {
        // How often the songs read are taken in, how long a batch may hold the UI thread, and how many may wait, as the
        // Validator takes in the songs it reads.
        private static readonly TimeSpan TakeReadInterval = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan TakeReadBudget = TimeSpan.FromMilliseconds(30);
        private const int ReadQueueCapacity = 1024;

        // How long the search waits for typing to stop before the list is filtered.
        private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(200);

        private readonly List<LowerDifficultiesSongRow> _rows;
        private readonly Dictionary<string, LowerDifficultiesSongRow> _byPath = new(StringComparer.Ordinal);
        private readonly MetadataDraftStore _drafts;
        private readonly GeneratedChartStash _stash;
        private readonly DraftSaver _saver;
        private readonly SongLocks? _locks;
        private readonly ReductionRulesFile _rulesFile;
        private readonly int _parallelism;
        private readonly UiBatchQueue<SurveyedSong> _read;
        private readonly DispatcherTimer _searchTimer;
        private readonly CancellationTokenSource _closing = new();
        private readonly List<Task> _running = [];
        private CancellationTokenSource? _run;
        private LowerDifficultyScope _scope;
        private string _query = "";
        private bool _refreshPending;
        private bool _loadingRules;
        private bool _disposed;

        /// <param name="entries">The songs to generate, in the library's order.</param>
        /// <param name="drafts">The drafts the main window edits, which the new charts are staged in.</param>
        /// <param name="stash">Keeps the new charts until their songs are saved.</param>
        /// <param name="rules">The default rules, which it starts from and can save.</param>
        /// <param name="player">Plays the song picked, for the preview; the window's own, which the caller disposes.</param>
        /// <param name="parallelism">How many songs are read, and generated, at once.</param>
        public LowerDifficultiesViewModel(
            IReadOnlyList<SongEntry> entries,
            MetadataDraftStore drafts,
            GeneratedChartStash stash,
            ReductionRulesFile rules,
            DraftSaver saver,
            PlayerViewModel player,
            int parallelism,
            SongLocks? locks = null)
        {
            ArgumentNullException.ThrowIfNull(entries);
            ArgumentNullException.ThrowIfNull(drafts);
            ArgumentNullException.ThrowIfNull(stash);
            ArgumentNullException.ThrowIfNull(rules);
            ArgumentNullException.ThrowIfNull(saver);
            ArgumentNullException.ThrowIfNull(player);
            ArgumentOutOfRangeException.ThrowIfLessThan(parallelism, 1);
            _drafts = drafts;
            _stash = stash;
            _saver = saver;
            _locks = locks;
            _rulesFile = rules;
            _parallelism = parallelism;

            // Each song's draft is copied here, on the UI thread the drafts live on, for its chart to be read through.
            _rows = [.. entries.DistinctBy(static e => e.FullPath, StringComparer.Ordinal)
                .Select(e => new LowerDifficultiesSongRow(e, drafts.HasDrafts(e.FullPath) ? drafts.Get(e.FullPath).Clone() : null))];
            foreach (var row in _rows)
            {
                _byPath[row.Path] = row;
                row.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(LowerDifficultiesSongRow.IsIncluded))
                        ScheduleRefresh();
                };
            }

            Targets = [.. ReductionRulesViewModel.Targets.Select(d => new LowerDifficultiesTarget(d, OnScopeChanged))];
            _scope = ScopeOf();

            Rules = new ReductionRulesViewModel(rules.Load(), ChartInstrumentKind.FiveFret, ChartDifficulty.Hard);
            Generation = new GenerationPreviewViewModel(player, () => Rules.Defaults, CurrentGenerator, Strings.LowerDifficultiesNothing, Strings.LowerDifficultiesPickSong);
            Rules.Changed += (_, _) =>
            {
                RulesSaveStatus = "";
                Generation.ScheduleRedraw();
            };
            _loadingRules = true;
            SelectedFamily = FamilyChoices.First(static c => c.Kind == ChartInstrumentKind.FiveFret);
            SelectedTarget = TargetChoices.First(static c => c.Difficulty == ChartDifficulty.Hard);
            _loadingRules = false;

            ToGenerateTab = new LowerDifficultiesTab(LowerDifficultiesState.ToGenerate, Strings.LowerDifficultiesTabToGenerate, OnTabPicked);
            SkippedTab = new LowerDifficultiesTab(LowerDifficultiesState.Skipped, Strings.LowerDifficultiesTabSkipped, OnTabPicked);
            ProblemsTab = new LowerDifficultiesTab(LowerDifficultiesState.Problem, Strings.LowerDifficultiesTabProblems, OnTabPicked);
            DoneTab = new LowerDifficultiesTab(LowerDifficultiesState.Done, Strings.LowerDifficultiesTabDone, OnTabPicked);
            AllTab = new LowerDifficultiesTab(null, Strings.LowerDifficultiesTabAll, OnTabPicked);
            Tabs = [ToGenerateTab, SkippedTab, ProblemsTab, DoneTab, AllTab];
            SelectedTab = ToGenerateTab;
            ToGenerateTab.IsSelected = true;

            _read = new UiBatchQueue<SurveyedSong>(TakeReadInterval, TakeRead, TakeReadBudget, ReadQueueCapacity);
            _searchTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = SearchDelay };
            _searchTimer.Tick += (_, _) => ApplySearch();
            Refresh();
            Generation.Show(null);
        }

        /// <summary>Raised when Close is pressed.</summary>
        public event EventHandler? Done;

        /// <summary>The difficulties to fill: Hard, Medium and Easy, each of which can be left out.</summary>
        public IReadOnlyList<LowerDifficultiesTarget> Targets { get; }

        /// <summary>The parts the songs have that can be filled, each of which can be left out, as the songs are read.</summary>
        public AvaloniaList<LowerDifficultiesPart> Parts { get; } = [];

        [ObservableProperty]
        public partial bool HasParts { get; private set; }

        /// <summary>Whether a difficulty that has notes is made again, in place of what it has; otherwise it's skipped.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsSkip), nameof(ChartedNote))]
        public partial bool IsReplace { get; set; }

        /// <summary>Whether a difficulty that has notes is left as it is.</summary>
        public bool IsSkip
        {
            get => !IsReplace;
            set => IsReplace = !value;
        }

        public string ChartedNote => IsReplace ? Strings.LowerDifficultiesReplaceNote : Strings.LowerDifficultiesSkipNote;

        /// <summary>The rules for the family and target picked, as fields; changes are this run's until saved as defaults.</summary>
        public ReductionRulesViewModel Rules { get; }

        public IReadOnlyList<FamilyChoice> FamilyChoices => ReductionRulesViewModel.FamilyChoices;

        public IReadOnlyList<DifficultyChoice> TargetChoices => ReductionRulesViewModel.TargetChoices;

        [ObservableProperty]
        public partial FamilyChoice? SelectedFamily { get; set; }

        [ObservableProperty]
        public partial DifficultyChoice? SelectedTarget { get; set; }

        /// <summary>Whether the default rules were saved, or couldn't be; empty before Save as defaults.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasRulesSaveStatus))]
        public partial string RulesSaveStatus { get; private set; } = "";

        public bool HasRulesSaveStatus => RulesSaveStatus.Length > 0;

        /// <summary>The generator drafts are made with: Rules (today's Reduce) or Patterns (count-based).</summary>
        public IReadOnlyList<string> GeneratorChoices { get; } = ["Rules", "Patterns"];

        /// <summary>Which generator drafts are made with. Patterns falls back to Rules per part when it can't run.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsPatterns))]
        public partial string SelectedGenerator { get; set; } = "Rules";

        /// <summary>Whether Patterns-specific settings show.</summary>
        public bool IsPatterns => string.Equals(SelectedGenerator, "Patterns", StringComparison.OrdinalIgnoreCase);

        /// <summary>Pack file for Patterns, as a path. Empty uses the built-in demo pack.</summary>
        [ObservableProperty]
        public partial string PackPath { get; set; } = "";

        /// <summary>What pack Patterns runs with: the loaded pack, the demo, or why it fell back.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasPackStatus))]
        public partial string PackStatus { get; private set; } = "Patterns runs the built-in demo pack until one is loaded.";

        public bool HasPackStatus => PackStatus.Length > 0;

        /// <summary>Style the Patterns draft imitates, from the loaded pack (Any first).</summary>
        [ObservableProperty]
        public partial string PatternsStyle { get; set; } = "Any";

        public IReadOnlyList<string> PatternsStyles => _pack?.Manifest.Styles.Select(static s => s.Id).Prepend("Any").Distinct().ToList() ?? ["Any"];

        /// <summary>Fewer notes left, more notes right. Centred changes nothing (spec 3.4).</summary>
        [ObservableProperty]
        public partial double PatternsDensity { get; set; }

        /// <summary>Folder of reference songs (notes.mid) to count into a pack.</summary>
        [ObservableProperty]
        public partial string CorpusPath { get; set; } = "";

        /// <summary>Where Train writes the pack (.json).</summary>
        [ObservableProperty]
        public partial string TrainOutputPath { get; set; } = "";

        /// <summary>What training did, or why it couldn't.</summary>
        [ObservableProperty]
        public partial string TrainStatus { get; private set; } = "";

        private PatternPack? _pack;

        /// <summary>The generator drafts are made with now, by the choice above.</summary>
        public IDifficultyGenerator CurrentGenerator()
        {
            if (!string.Equals(SelectedGenerator, "Patterns", StringComparison.OrdinalIgnoreCase))
                return new RulesGenerator();
            var pack = _pack ?? PatternPackWriter.Demo();
            return new PatternsGenerator(pack, new PatternsOptions(Style: string.IsNullOrWhiteSpace(PatternsStyle) ? "Any" : PatternsStyle, Density: PatternsDensity));
        }

        partial void OnSelectedGeneratorChanged(string value) => Generation.ScheduleRedraw();

        partial void OnPatternsStyleChanged(string value) => Generation.ScheduleRedraw();

        partial void OnPatternsDensityChanged(double value) => Generation.ScheduleRedraw();

        /// <summary>Loads the pack at <see cref="PackPath"/>, for Patterns to run with.</summary>
        [RelayCommand]
        private void LoadPack()
        {
            if (string.IsNullOrWhiteSpace(PackPath) || !File.Exists(PackPath))
            {
                _pack = null;
                PackStatus = "Patterns runs the built-in demo pack until one is loaded.";
                OnPropertyChanged(nameof(PatternsStyles));
                Generation.ScheduleRedraw();
                return;
            }

            try
            {
                _pack = PatternPackLoader.Load(PackPath);
                PackStatus = $"Loaded {_pack.Manifest.Name} ({_pack.Manifest.Styles.Count} styles, {string.Join(", ", _pack.Manifest.Pairs)}).";
                if (!_pack.Manifest.Styles.Any(s => s.Id == PatternsStyle))
                    PatternsStyle = "Any";
            }
            catch (Exception ex)
            {
                _pack = null;
                PackStatus = $"Pack not loaded ({ex.Message}). Patterns falls back to Rules.";
            }

            OnPropertyChanged(nameof(PatternsStyles));
            Generation.ScheduleRedraw();
        }

        /// <summary>Counts <see cref="CorpusPath"/> into a pattern pack at <see cref="TrainOutputPath"/> (spec 6.4).</summary>
        [RelayCommand]
        private async Task TrainPackAsync()
        {
            if (string.IsNullOrWhiteSpace(CorpusPath) || !Directory.Exists(CorpusPath))
            {
                TrainStatus = "Pick a corpus folder first.";
                return;
            }

            if (string.IsNullOrWhiteSpace(TrainOutputPath))
            {
                TrainStatus = "Pick an output .json path first.";
                return;
            }

            string output = TrainOutputPath.Trim();
            try
            {
                // A folder was pasted instead of a file (e.g. ...\chart-output): write a dated pack inside it.
                if (Directory.Exists(output) || !output.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    string folder = Directory.Exists(output) ? output : Path.GetDirectoryName(output) ?? output;
                    if (string.IsNullOrWhiteSpace(Path.GetExtension(output)))
                        folder = output;
                    Directory.CreateDirectory(folder);
                    output = Path.Combine(folder, $"pattern-pack-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                    TrainOutputPath = output;
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                TrainStatus = $"Can't write to {output}: {ex.Message}";
                return;
            }

            TrainStatus = "Training…";
            try
            {
                var result = await Task.Run(() => PatternPackTrainer.TrainFromFolder(CorpusPath));
                try
                {
                    PatternPackWriter.Write(output, result.Pack);
                }
                catch (UnauthorizedAccessException ex)
                {
                    TrainStatus = $"Training counted {result.Parts} parts but can't write {output}: access denied. {ex.Message}";
                    return;
                }

                string warnings = result.Warnings.Count == 0 ? "" : " Warnings: " + string.Join(" ", result.Warnings);
                TrainStatus = $"Trained {result.Parts} parts, {result.Beats} beats from {result.Songs} songs to {output}.{warnings}";
                PackPath = output;
                LoadPack();
            }
            catch (Exception ex)
            {
                TrainStatus = $"Training failed: {ex.Message}";
            }
        }

        /// <summary>The song picked, as it'd be generated: each part's ladder, and the picked part's drafts in the preview.</summary>
        public GenerationPreviewViewModel Generation { get; }

        /// <summary>The picked part's drafts against what they're made from, as highways played with the song.</summary>
        public ReductionPreviewViewModel Preview => Generation.Preview;

        public LowerDifficultiesTab ToGenerateTab { get; }

        public LowerDifficultiesTab SkippedTab { get; }

        public LowerDifficultiesTab ProblemsTab { get; }

        public LowerDifficultiesTab DoneTab { get; }

        public LowerDifficultiesTab AllTab { get; }

        public IReadOnlyList<LowerDifficultiesTab> Tabs { get; }

        /// <summary>The tab whose songs are listed.</summary>
        public LowerDifficultiesTab SelectedTab { get; private set; }

        /// <summary>The songs the tab picked lists that match the search, in the library's order.</summary>
        public AvaloniaList<LowerDifficultiesSongRow> ShownRows { get; } = [];

        /// <summary>The song whose difficulties the side shows, and the preview plays.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSelection))]
        public partial LowerDifficultiesSongRow? SelectedRow { get; set; }

        public bool HasSelection => SelectedRow is not null;

        /// <summary>The search, as typed.</summary>
        [ObservableProperty]
        public partial string SearchText { get; set; } = "";

        /// <summary>What the list says when it's empty; empty when it isn't.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsListEmpty))]
        public partial string EmptyText { get; private set; } = "";

        public bool IsListEmpty => EmptyText.Length > 0;

        /// <summary>Whether the songs' charts are being read.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsBusy), nameof(CanChange))]
        [NotifyCanExecuteChangedFor(nameof(GenerateCommand), nameof(SaveCommand), nameof(CloseCommand))]
        public partial bool IsReading { get; private set; }

        /// <summary>Whether the songs are being generated.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsBusy), nameof(CanChange), nameof(ShowGenerate), nameof(IsSavePrimary))]
        [NotifyCanExecuteChangedFor(nameof(GenerateCommand), nameof(StopCommand), nameof(SaveCommand), nameof(CloseCommand))]
        public partial bool IsGenerating { get; private set; }

        /// <summary>Whether the songs are being saved.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsBusy), nameof(CanChange))]
        [NotifyCanExecuteChangedFor(nameof(GenerateCommand), nameof(SaveCommand), nameof(CloseCommand))]
        public partial bool IsSaving { get; private set; }

        /// <summary>Whether a run is going on, whose progress the bar shows.</summary>
        public bool IsBusy => IsReading || IsGenerating || IsSaving;

        /// <summary>Whether the settings can be changed: not while songs are generated or saved by them.</summary>
        public bool CanChange => !IsGenerating && !IsSaving;

        /// <summary>How far the run going on is.</summary>
        [ObservableProperty]
        public partial int ProgressDone { get; private set; }

        [ObservableProperty]
        public partial int ProgressTotal { get; private set; }

        /// <summary>What the run going on is doing, or how the songs stand once it's done.</summary>
        [ObservableProperty]
        public partial string StatusText { get; private set; } = "";

        /// <summary>The Problems link: how many songs have one; empty for none.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasProblems))]
        public partial string ProblemsText { get; private set; } = "";

        public bool HasProblems => ProblemsText.Length > 0;

        /// <summary>Why the songs, or some, weren't saved; null when they were.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSaveError))]
        public partial string? SaveError { get; private set; }

        public bool HasSaveError => SaveError is not null;

        /// <summary>How many songs Generate generates: those with something to generate that are ticked.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(GenerateText), nameof(ShowGenerate), nameof(IsSavePrimary))]
        [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
        public partial int ToGenerateCount { get; private set; }

        /// <summary>How many songs are generated and not saved yet.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SaveText), nameof(ShowSave), nameof(ShowGenerate), nameof(IsSavePrimary))]
        [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
        public partial int UnsavedCount { get; private set; }

        /// <summary>Generate, with how many songs it generates.</summary>
        public string GenerateText => ToGenerateCount switch
        {
            0 => Strings.LowerDifficultiesGenerateNone,
            1 => Strings.LowerDifficultiesGenerateOne,
            var n => string.Format(CultureInfo.CurrentCulture, Strings.LowerDifficultiesGenerateFormat, n),
        };

        public string SaveText => UnsavedCount == 1
            ? Strings.LowerDifficultiesSaveOne
            : string.Format(CultureInfo.CurrentCulture, Strings.LowerDifficultiesSaveFormat, UnsavedCount);

        /// <summary>Whether Generate shows: not while it runs, when Stop takes its place, nor once only saving's left.</summary>
        public bool ShowGenerate => !IsGenerating && (ToGenerateCount > 0 || UnsavedCount == 0);

        public bool ShowSave => UnsavedCount > 0;

        /// <summary>Whether Save is the next thing to do, so it's the accent button.</summary>
        public bool IsSavePrimary => !IsGenerating && ToGenerateCount == 0 && UnsavedCount > 0;

        /// <summary>
        /// Whether every song listed with something to generate is ticked: null when some are and some aren't. Setting it
        /// ticks or leaves out every one of them.
        /// </summary>
        public bool? AllShownIncluded
        {
            get
            {
                var shown = ShownRows.Where(static r => r.CanInclude).ToList();
                return shown.Count == 0 || shown.All(static r => r.IsIncluded) ? true
                    : shown.Any(static r => r.IsIncluded) ? null
                    : false;
            }
            set
            {
                bool include = value != false;
                foreach (var row in ShownRows.Where(static r => r.CanInclude))
                    row.IsIncluded = include;
                OnPropertyChanged();
            }
        }

        /// <summary>Whether a song listed has something to generate, so the tick for them all applies.</summary>
        [ObservableProperty]
        public partial bool CanIncludeShown { get; private set; }

        /// <summary>Reads every song's chart, listing each as it's read.</summary>
        public Task LoadAsync() => Track(ReadAllAsync());

        /// <summary>Stops everything going on and waits for it, before the window's let go of.</summary>
        public async Task ShutdownAsync()
        {
            _closing.Cancel();
            _searchTimer.Stop();
            Generation.Stop();
            try
            {
                await Task.WhenAll(_running.ToList());
            }
            catch (Exception ex) when (ex is OperationCanceledException or AggregateException)
            {
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _closing.Cancel();
            _closing.Dispose();
            _run?.Dispose();
            _searchTimer.Stop();
            Generation.Dispose();
        }

        partial void OnIsReplaceChanged(bool value) => OnScopeChanged();

        partial void OnSelectedFamilyChanged(FamilyChoice? value) => ShowRules();

        partial void OnSelectedTargetChanged(DifficultyChoice? value) => ShowRules();

        partial void OnSelectedRowChanged(LowerDifficultiesSongRow? value) => ShowSelected();

        partial void OnSearchTextChanged(string value)
        {
            _searchTimer.Stop();
            _searchTimer.Start();
        }

        /// <summary>Generates every song with something to generate, staging the new charts in their drafts.</summary>
        [RelayCommand(CanExecute = nameof(CanGenerate))]
        private Task GenerateAsync() => Track(GenerateAllAsync());

        private bool CanGenerate() => !IsReading && !IsGenerating && !IsSaving && ToGenerateCount > 0;

        /// <summary>Stops generating; the songs done so far keep their new difficulties.</summary>
        [RelayCommand(CanExecute = nameof(IsGenerating))]
        private void Stop() => _run?.Cancel();

        /// <summary>Writes the songs generated, and not saved yet, to disk.</summary>
        [RelayCommand(CanExecute = nameof(CanSave))]
        private Task SaveAsync() => Track(SaveAllAsync());

        private bool CanSave() => !IsReading && !IsGenerating && !IsSaving && UnsavedCount > 0;

        /// <summary>Closes the window. Songs generated and not saved keep their new difficulties in their drafts.</summary>
        [RelayCommand(CanExecute = nameof(CanClose))]
        private void Close()
        {
            _closing.Cancel();
            Generation.Stop();
            Done?.Invoke(this, EventArgs.Empty);
        }

        // Saving can't be left half done; anything else stops as the window closes.
        private bool CanClose() => !IsSaving;

        /// <summary>Lists the songs with problems.</summary>
        [RelayCommand]
        private void ShowProblems() => ProblemsTab.IsSelected = true;

        /// <summary>The shipped rules for the family and target shown, in place of what's shown.</summary>
        [RelayCommand]
        private void ResetRules() => Rules.Reset();

        /// <summary>Writes the rules as shown as the defaults both apps start from.</summary>
        [RelayCommand]
        private void SaveRulesAsDefaults() =>
            RulesSaveStatus = _rulesFile.Save(Rules.Defaults) ? Strings.GenerateDifficultiesSavedDefaults : Strings.GenerateDifficultiesSaveDefaultsFailed;

        private void ShowRules()
        {
            if (_loadingRules || SelectedFamily is not { } family || SelectedTarget is not { } target)
                return;
            Rules.Show(family.Kind, target.Difficulty);
        }

        private LowerDifficultyScope ScopeOf()
        {
            var targets = Targets.Where(static t => t.IsIncluded).Select(static t => t.Difficulty).ToHashSet();
            IReadOnlySet<ChartInstrument>? parts = Parts.All(static p => p.IsIncluded)
                ? null
                : Parts.Where(static p => p.IsIncluded).Select(static p => p.Part).ToHashSet();
            return new LowerDifficultyScope(targets, parts, IsReplace);
        }

        // Every song read is planned again, in memory, by the settings as they are now.
        private void OnScopeChanged()
        {
            _scope = ScopeOf();
            foreach (var row in _rows)
                row.Replan(_scope);
            Refresh();
            ShowSelected();
        }

        private void OnTabPicked(LowerDifficultiesTab tab)
        {
            SelectedTab = tab;
            foreach (var other in Tabs)
            {
                if (other != tab)
                    other.IsSelected = false;
            }

            OnPropertyChanged(nameof(SelectedTab));
            Refresh();
        }

        private void ApplySearch()
        {
            _searchTimer.Stop();
            _query = SearchText.Trim();
            Refresh();
        }

        private void ShowSelected()
        {
            switch (SelectedRow)
            {
                case null:
                    Generation.Show(null);
                    break;
                case { State: LowerDifficultiesState.Problem, Problem: var problem }:
                    Generation.Show(null, Strings.LowerDifficultiesProblemDetail + Environment.NewLine + problem);
                    break;
                case { State: LowerDifficultiesState.Done }:
                    Generation.Show(null, Strings.LowerDifficultiesDoneDetail);
                    break;
                case { State: LowerDifficultiesState.Reading }:
                    Generation.Show(null, Strings.LowerDifficultiesReading);
                    break;
                case var row:
                    // Shown again only for another song, or other settings: the rules' changes redraw it themselves.
                    if (row.Entry != Generation.Song?.Entry || !ReferenceEquals(_scope, Generation.Song?.Scope))
                        Generation.Show(row.SongFor(_scope));
                    break;
            }
        }

        // The counts, the tabs' songs, the buttons and the status, as the songs stand now; a burst of changes is taken in once.
        private void ScheduleRefresh()
        {
            if (_refreshPending)
                return;
            _refreshPending = true;
            Dispatcher.UIThread.Post(Refresh, DispatcherPriority.Background);
        }

        private void Refresh()
        {
            _refreshPending = false;
            foreach (var tab in Tabs)
                tab.Count = _rows.Count(tab.Lists);
            ToGenerateCount = _rows.Count(static r => r.WillGenerate);
            UnsavedCount = _rows.Count(IsUnsaved);
            ProblemsText = ProblemsTab.Count == 0
                ? ""
                : string.Format(CultureInfo.CurrentCulture, Strings.LowerDifficultiesFailedCountFormat, ProblemsTab.Count);

            var selected = SelectedRow;
            ListRows.Replace(ShownRows, [.. _rows.Where(r => SelectedTab.Lists(r) && r.Matches(_query))]);

            // A row still listed is picked again, as the list lets go of it when it's removed and inserted again.
            SelectedRow = selected is not null && ShownRows.Contains(selected) ? selected : null;
            CanIncludeShown = ShownRows.Any(static r => r.CanInclude);
            OnPropertyChanged(nameof(AllShownIncluded));
            EmptyText = ShownRows.Count > 0 ? ""
                : IsReading && SelectedTab == ToGenerateTab ? Strings.LowerDifficultiesEmptyReading
                : _query.Length > 0 ? Strings.LowerDifficultiesEmptySearch
                : SelectedTab == ToGenerateTab ? Strings.LowerDifficultiesEmptyToGenerate
                : Strings.LowerDifficultiesEmptyTab;
            RefreshStatus();
        }

        private void RefreshStatus()
        {
            if (IsReading)
                StatusText = string.Format(CultureInfo.CurrentCulture, Strings.LowerDifficultiesReadingFormat, ProgressDone, ProgressTotal);
            else if (IsGenerating)
                StatusText = string.Format(CultureInfo.CurrentCulture, Strings.LowerDifficultiesGeneratingFormat, ProgressDone, ProgressTotal);
            else if (IsSaving)
                StatusText = Strings.LowerDifficultiesSaving;
            else if (UnsavedCount > 0)
                StatusText = string.Format(CultureInfo.CurrentCulture, Strings.LowerDifficultiesUnsavedFormat, UnsavedCount);
            else if (_rows.Any(static r => r.IsSaved))
                StatusText = string.Format(CultureInfo.CurrentCulture, Strings.LowerDifficultiesSavedFormat, _rows.Count(static r => r.IsSaved));
            else
                StatusText = string.Format(CultureInfo.CurrentCulture, Strings.LowerDifficultiesReadyFormat, _rows.Count(static r => r.Survey is not null));
        }

        // Keeps a run, for the window to wait on as it closes.
        private Task Track(Task run)
        {
            _running.RemoveAll(static t => t.IsCompleted);
            _running.Add(run);
            return run;
        }

        private async Task ReadAllAsync()
        {
            IsReading = true;
            ProgressDone = 0;
            ProgressTotal = _rows.Count;
            Refresh();
            var options = new ParallelOptions { MaxDegreeOfParallelism = _parallelism, CancellationToken = _closing.Token };
            _read.Start();
            try
            {
                await Parallel.ForEachAsync(_rows, options, async (row, token) =>
                {
                    var surveyed = await SurveyAsync(row, token).ConfigureAwait(false);

                    // Waits while the UI thread is behind taking in the songs read, so they don't pile up.
                    await _read.ReportAsync(surveyed, token).ConfigureAwait(false);
                });
                await _read.TakeRestAsync();
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _read.Stop();
                IsReading = false;
                if (!_closing.IsCancellationRequested)
                {
                    Refresh();
                    SelectedRow ??= ShownRows.FirstOrDefault();
                }
            }
        }

        // The song opened once, for its metadata and then its chart, which is let go of once its difficulties are counted.
        private static async Task<SurveyedSong> SurveyAsync(LowerDifficultiesSongRow row, CancellationToken token)
        {
            SongMetadataReport? report = null;
            try
            {
                var (read, files) = await SongMetadataCheck.OpenAsync(row.Entry, token).ConfigureAwait(false);
                report = read;
                using (files)
                {
                    var pairs = read.DraftedPairs(row.Draft);
                    var survey = files is null
                        ? new SongDifficultySurvey(SongSurveyStatus.Unreadable, [], read.ReadError)
                        : await SongDifficultySurvey.ReadAsync(files, pairs, row.Draft, token).ConfigureAwait(false);
                    return new SurveyedSong(row, read, pairs, survey);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                Log.ForContext<LowerDifficultiesViewModel>().Warning(ex, "Reading {Song} for its lower difficulties failed", row.Path);
                report ??= new SongMetadataReport(row.Entry, [], [], SongMetadataStatus.Unreadable, ex.Message);
                return new SurveyedSong(row, report, report.Pairs, new SongDifficultySurvey(SongSurveyStatus.Unreadable, [], ex.Message));
            }
        }

        private void TakeRead(IEnumerable<SurveyedSong> batch)
        {
            bool selectedRead = false;
            foreach (var (row, report, pairs, survey) in batch)
            {
                ProgressDone++;
                if (_closing.IsCancellationRequested)
                    continue;
                foreach (var part in survey.Parts)
                {
                    if (Parts.Any(p => p.Part == part.Part))
                        continue;
                    int at = Parts.TakeWhile(p => p.Part < part.Part).Count();
                    Parts.Insert(at, new LowerDifficultiesPart(part.Part, OnScopeChanged));
                    HasParts = true;

                    // A part first found is filled, as every part is until it's left out; a scope that names the parts,
                    // as one does once a part is left out, names it too from here on.
                    _scope = ScopeOf();
                }

                row.Read(report, pairs, survey, _scope);
                selectedRead |= row == SelectedRow;
            }

            Refresh();

            // The song picked while it was being read is shown as read.
            if (selectedRead)
                ShowSelected();
        }

        private async Task GenerateAllAsync()
        {
            var rules = Rules.Defaults;
            var generator = CurrentGenerator();
            var scope = _scope;

            // Each song as drafted now, its draft copied, as the work is done off the UI thread the drafts live on.
            var requests = _rows
                .Where(static r => r.WillGenerate)
                .Select(r => (Row: r, Draft: _drafts.HasDrafts(r.Path) ? _drafts.Get(r.Path).Clone() : null, r.SongFor(scope)!.Work))
                .ToList();
            if (requests.Count == 0)
                return;

            _run?.Dispose();
            _run = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
            var token = _run.Token;
            SaveError = null;
            ProgressDone = 0;
            ProgressTotal = requests.Count;
            IsGenerating = true;
            RefreshStatus();
            var options = new ParallelOptions { MaxDegreeOfParallelism = _parallelism, CancellationToken = token };
            try
            {
                await Parallel.ForEachAsync(requests, options, async (request, ct) =>
                {
                    GeneratedChart? chart = null;
                    string? failure = null;
                    try
                    {
                        chart = generator is RulesGenerator
                            ? await SongDifficultyGeneration.GenerateAsync(request.Row.Entry, request.Row.Pairs, request.Draft, rules, request.Work, _stash, ct).ConfigureAwait(false)
                            : await SongDifficultyGeneration.GenerateAsync(request.Row.Entry, request.Row.Pairs, request.Draft, rules, generator, request.Work, _stash, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                    {
                        Log.ForContext<LowerDifficultiesViewModel>().Warning(ex, "Generating lower difficulties of {Song} failed", request.Row.Path);
                        failure = ex.Message;
                    }

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        ProgressDone++;
                        Stage(request.Row, chart, failure);
                        RefreshStatus();
                        ScheduleRefresh();
                    });
                });
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                IsGenerating = false;
                if (!_closing.IsCancellationRequested)
                {
                    Refresh();
                    ShowSelected();
                }
            }
        }

        // The new chart, into the song's draft, unless the window's closed or the draft can't take it now.
        private void Stage(LowerDifficultiesSongRow row, GeneratedChart? chart, string? failure)
        {
            if (_closing.IsCancellationRequested)
                return;
            if (failure is not null)
            {
                row.Failed(string.Format(CultureInfo.CurrentCulture, Strings.LowerDifficultiesFailedFormat, failure));
                return;
            }

            if (chart is null)
            {
                row.Failed(Strings.LowerDifficultiesNothingGenerated);
                return;
            }

            var draft = _drafts.Get(row.Path);
            if (!SongDifficultyGeneration.CanGenerate(draft))
            {
                row.Failed(Strings.LowerDifficultiesChangedMeanwhile);
                return;
            }

            chart.ApplyTo(draft);
            _drafts.NotifyChanged(row.Path, DraftChange.Edited);

            // Another app holding the song refuses the change, and clears the draft as it does.
            if (!_drafts.HasDrafts(row.Path))
            {
                row.Failed(Strings.LowerDifficultiesRefused);
                return;
            }

            row.Generated();
        }

        // Whether the song's new difficulties are in its draft, waiting to be saved.
        private bool IsUnsaved(LowerDifficultiesSongRow row) =>
            row is { IsDone: true, IsSaved: false, Report.CanEdit: true } && _drafts.HasDrafts(row.Path);

        private async Task SaveAllAsync()
        {
            // Another app holding a song since it was generated cleared its draft, and its new difficulties with it.
            foreach (var dropped in _rows.Where(r => r is { IsDone: true, IsSaved: false } && !_drafts.HasDrafts(r.Path)))
                dropped.Failed(Strings.LowerDifficultiesRefused);

            var rows = _rows.Where(IsUnsaved).ToList();
            if (rows.Count == 0)
            {
                Refresh();
                return;
            }

            IDisposable? held = null;
            if (_locks is not null)
            {
                held = _locks.TryHold([.. rows.Select(static r => r.Entry)], out string? inUse);
                if (held is null)
                {
                    SaveError = string.Format(CultureInfo.CurrentCulture, Strings.NotSavedInUseFormat, inUse);
                    return;
                }
            }

            IsSaving = true;
            SaveError = null;
            RefreshStatus();
            IReadOnlyList<string> failures;
            try
            {
                // The preview's player holds the picked song's files open, and mustn't open them again until every song
                // is saved.
                Generation.Show(null, Strings.LowerDifficultiesSaving);
                await Generation.ReleaseFilesAsync();
                var targets = rows.Select(r => new SaveTarget(r.Entry, r.Report!.SongIniPath, _drafts.Get(r.Path))).ToList();
                failures = await _saver.SaveAsync(targets, entry =>
                {
                    _drafts.NotifyChanged(entry.FullPath, DraftChange.Saved);
                    if (_byPath.TryGetValue(entry.FullPath, out var saved))
                        saved.Saved();
                });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.ForContext<LowerDifficultiesViewModel>().Warning(ex, "Saving lower difficulties failed");
                failures = [ex.Message];
            }
            finally
            {
                held?.Dispose();
                IsSaving = false;
            }

            if (failures.Count > 0)
                SaveError = string.Format(CultureInfo.CurrentCulture, Strings.SaveFailedFormat, string.Join(Environment.NewLine, failures));
            if (!_closing.IsCancellationRequested)
            {
                Refresh();
                ShowSelected();
            }
        }

        /// <summary>A song read by a worker, waiting to be taken in on the UI thread.</summary>
        private sealed record SurveyedSong(
            LowerDifficultiesSongRow Row,
            SongMetadataReport Report,
            IReadOnlyList<KeyValuePair<string, string>> Pairs,
            SongDifficultySurvey Survey);
    }
}
