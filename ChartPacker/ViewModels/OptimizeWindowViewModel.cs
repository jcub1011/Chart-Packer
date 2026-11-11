using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChartPacker.Resources;
using ChartPacker.Services;
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
    /// <summary>One choice in a drop-down. It's shown as its label.</summary>
    public sealed record OptimizeChoice<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>One strategy's switch: whether it's on, and whether its backend can run at all.</summary>
    public sealed partial class OptimizeStrategyToggle : ObservableObject
    {
        public OptimizeStrategyToggle(OptimizeStrategy strategy, string title, string description, string? unavailableReason)
        {
            Strategy = strategy;
            Title = title;
            Description = description;
            UnavailableReason = unavailableReason;
        }

        public OptimizeStrategy Strategy { get; }

        public string Title { get; }

        public string Description { get; }

        /// <summary>Why the strategy can't run, or <see langword="null"/> when it can.</summary>
        public string? UnavailableReason { get; }

        public bool IsAvailable => UnavailableReason is null;

        public bool IsUnavailable => UnavailableReason is not null;

        public string UnavailableText => UnavailableReason is { } reason ? string.Format(CultureInfo.CurrentCulture, Strings.OptimizeUnavailableFormat, reason) : string.Empty;

        /// <summary>Whether the strategy is switched on. Its switch is disabled while it's unavailable.</summary>
        [ObservableProperty]
        public partial bool IsOn { get; set; }
    }

    /// <summary>A codec's line in an estimate.</summary>
    public sealed record OptimizeEstimateRow(string Codec, string Assets, string Saving, string PerChart, bool IsCost, bool IsCompatibility);

    /// <summary>One strategy's estimate: its codecs, and what they come to.</summary>
    public sealed record OptimizeEstimateSection(string Title, string Sample, IReadOnlyList<OptimizeEstimateRow> Rows, string Total, string? CompatibilityNote)
    {
        public bool HasRows => Rows.Count > 0;

        public bool HasNoRows => Rows.Count == 0;

        public bool HasCompatibilityNote => CompatibilityNote is not null;
    }

    /// <summary>A chart's result in a run.</summary>
    public sealed record OptimizeResultRow(string Name, string Status, bool IsProblem);

    /// <summary>
    /// The Optimize window (issue #257): what to optimize and how, an estimate of the savings, and the run itself.
    /// </summary>
    public sealed partial class OptimizeWindowViewModel : ViewModelBase
    {
        private static readonly int[] BitratePresets = [64, 80, 96, 128, 160, 192, 256];

        private readonly OptimizeRequest _request;
        private readonly OptimizeSettingsFile _defaults;
        private readonly OptimizeBackends _backends;
        private readonly OptimizeJobServices _services;
        private readonly IConfirmationService _confirmation;
        private readonly IFolderPickerService _folderPicker;
        private readonly CancellationTokenSource _closing = new();
        private readonly Dictionary<string, ChartAssets> _inspected = new(StringComparer.Ordinal);

        private readonly OptimizeChoice<SamplingMode> _single;
        private readonly OptimizeChoice<SamplingMode> _sample;
        private readonly OptimizeChoice<SamplingMode> _all;

        private IReadOnlyList<SongEntry> _scope;
        private IReadOnlyList<SongEntry>? _library;
        private OptimizerSet? _optimizers;
        private int _optimizersParallelism;
        private int _parallelism;
        // Not offered in the window, so it's carried from the saved settings to the run, and back when they're saved.
        private VideoEncodeSpeed _videoSpeed = VideoEncodeSpeed.Balanced;
        private int _unreadable;
        private bool _closeWhenIdle;
        private CancellationTokenSource? _run;
        private Task _current = Task.CompletedTask;

        private int _applied;
        private int _skipped;
        private int _failed;
        private long _bytesSaved;
        private int _changedInPlace;
        private int _written;

        /// <param name="request">What the window works on.</param>
        /// <param name="defaults">Where the window's defaults are read from and saved to.</param>
        /// <param name="backends">The optimizers, probes and availability.</param>
        /// <param name="services">What a run needs from the app: its locks, gate and drafts.</param>
        /// <param name="confirmation">Asks before charts are changed in place.</param>
        /// <param name="folderPicker">Picks the output folder.</param>
        public OptimizeWindowViewModel(
            OptimizeRequest request,
            OptimizeSettingsFile defaults,
            OptimizeBackends backends,
            OptimizeJobServices services,
            IConfirmationService confirmation,
            IFolderPickerService folderPicker)
        {
            _request = request;
            _defaults = defaults;
            _backends = backends;
            _services = services;
            _confirmation = confirmation;
            _folderPicker = folderPicker;
            _scope = request.Selected;
            _useWholeLibrary = request.Selected.Count == 0;

            Metadata = Toggle(OptimizeStrategy.Metadata, Strings.OptimizeMetadataLabel, Strings.OptimizeMetadataDescription);
            Audio = Toggle(OptimizeStrategy.Audio, Strings.OptimizeAudioLabel, Strings.OptimizeAudioDescription);
            StemMixdown = Toggle(OptimizeStrategy.StemMixdown, Strings.OptimizeStemMixdownLabel, Strings.OptimizeStemMixdownDescription);
            Video = Toggle(OptimizeStrategy.Video, Strings.OptimizeVideoLabel, Strings.OptimizeVideoDescription);
            AlbumArt = Toggle(OptimizeStrategy.AlbumArt, Strings.OptimizeAlbumArtLabel, Strings.OptimizeImageDescription);
            BackgroundArt = Toggle(OptimizeStrategy.BackgroundArt, Strings.OptimizeBackgroundArtLabel, Strings.OptimizeImageDescription);
            AlbumArt.PropertyChanged += OnImageToggleChanged;
            BackgroundArt.PropertyChanged += OnImageToggleChanged;
            foreach (var toggle in Toggles)
                toggle.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(OptimizeStrategyToggle.IsOn)) RefreshCommands(); };

            BitrateChoices = [.. BitratePresets.Select(BitrateChoice)];
            MixdownBitrateChoices = [.. BitratePresets.Select(BitrateChoice)];
            VideoTargets =
            [
                new(VideoTarget.Fhd, Strings.OptimizeVideoFhd),
                new(VideoTarget.Uhd, Strings.OptimizeVideoUhd),
            ];
            AlbumArtLimits = [.. new (int? W, int? H)[] { (null, null), (256, 256), (512, 512), (1024, 1024) }.Select(LimitChoice)];
            BackgroundArtLimits = [.. new (int? W, int? H)[] { (null, null), (1280, 720), (1920, 1080), (2560, 1440), (3840, 2160) }.Select(LimitChoice)];

            _single = new(SamplingMode.SingleChart, Strings.OptimizeSamplingSingle);
            _sample = new(SamplingMode.RandomSample, Strings.OptimizeSamplingSample);
            _all = new(SamplingMode.AllCharts, Strings.OptimizeSamplingAll);
            SamplingChoices = [_single, _sample, _all];

            Apply(defaults.Load());
            ShowScope();
        }

        /// <summary>The window should close.</summary>
        public event Action? CloseRequested;

        public OptimizeStrategyToggle Metadata { get; }

        public OptimizeStrategyToggle Audio { get; }

        public OptimizeStrategyToggle StemMixdown { get; }

        public OptimizeStrategyToggle Video { get; }

        public OptimizeStrategyToggle AlbumArt { get; }

        public OptimizeStrategyToggle BackgroundArt { get; }

        private IEnumerable<OptimizeStrategyToggle> Toggles => [Metadata, Audio, StemMixdown, Video, AlbumArt, BackgroundArt];

        public ObservableCollection<OptimizeChoice<int>> BitrateChoices { get; }

        public ObservableCollection<OptimizeChoice<int>> MixdownBitrateChoices { get; }

        public IReadOnlyList<OptimizeChoice<VideoTarget>> VideoTargets { get; }

        public ObservableCollection<OptimizeChoice<(int? W, int? H)>> AlbumArtLimits { get; }

        public ObservableCollection<OptimizeChoice<(int? W, int? H)>> BackgroundArtLimits { get; }

        public ObservableCollection<OptimizeChoice<SamplingMode>> SamplingChoices { get; }

        public ObservableCollection<OptimizeChoice<SongEntry>> ChartChoices { get; } = [];

        public ObservableCollection<OptimizeEstimateSection> EstimateSections { get; } = [];

        public ObservableCollection<OptimizeResultRow> Results { get; } = [];

        // The metadata steps.

        [ObservableProperty]
        public partial bool PruneEmptyParts { get; set; }

        [ObservableProperty]
        public partial bool StripEditorEvents { get; set; }

        [ObservableProperty]
        public partial bool DropEmptyValues { get; set; }

        [ObservableProperty]
        public partial bool ReplaceLegacyKeys { get; set; }

        // Audio.

        [ObservableProperty]
        public partial OptimizeChoice<int>? SelectedBitrate { get; set; }

        [ObservableProperty]
        public partial OptimizeChoice<int>? SelectedMixdownBitrate { get; set; }

        // Video.

        [ObservableProperty]
        public partial OptimizeChoice<VideoTarget>? SelectedVideoTarget { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(VideoCqText))]
        public partial double VideoCq { get; set; }

        public string VideoCqText => string.Format(CultureInfo.CurrentCulture, Strings.OptimizeVideoCqFormat, (int)Math.Round(VideoCq));

        [ObservableProperty]
        public partial bool VideoConvertUnsupported { get; set; }

        // Images.

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(AlbumArtQualityText))]
        public partial double AlbumArtQuality { get; set; }

        public string AlbumArtQualityText => string.Format(CultureInfo.CurrentCulture, Strings.OptimizeImageQualityFormat, (int)Math.Round(AlbumArtQuality));

        [ObservableProperty]
        public partial OptimizeChoice<(int? W, int? H)>? SelectedAlbumArtLimit { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(BackgroundArtQualityText))]
        public partial double BackgroundArtQuality { get; set; }

        public string BackgroundArtQualityText => string.Format(CultureInfo.CurrentCulture, Strings.OptimizeImageQualityFormat, (int)Math.Round(BackgroundArtQuality));

        [ObservableProperty]
        public partial OptimizeChoice<(int? W, int? H)>? SelectedBackgroundArtLimit { get; set; }

        // The margin, as a percentage.

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(MarginText))]
        public partial double MarginPercent { get; set; }

        public string MarginText => string.Format(CultureInfo.CurrentCulture, Strings.OptimizeMarginFormat, (int)Math.Round(MarginPercent));

        // Sampling.

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsSingleChartMode))]
        public partial OptimizeChoice<SamplingMode>? SelectedSampling { get; set; }

        /// <summary>The estimate is of one chart, which the user picks.</summary>
        public bool IsSingleChartMode => SelectedSampling?.Value == SamplingMode.SingleChart;

        [ObservableProperty]
        public partial OptimizeChoice<SongEntry>? SelectedChart { get; set; }

        // Scope.

        private bool _useWholeLibrary;

        /// <summary>The window works on the whole library, not the songs that were selected.</summary>
        public bool UseWholeLibrary
        {
            get => _useWholeLibrary;
            set
            {
                // The scope is what an estimate or run is working on, so it can't change under one.
                if (IsBusy || !SetProperty(ref _useWholeLibrary, value))
                    return;
                OnPropertyChanged(nameof(UseSelection));
                _ = Track(ChangeScopeAsync());
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

        public string SelectedScopeText => string.Format(CultureInfo.CurrentCulture, Strings.OptimizeScopeSelectedFormat, _request.Selected.Count);

        [ObservableProperty]
        public partial string ScopeText { get; set; } = string.Empty;

        // Where the changed charts go.

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsInPlace))]
        [NotifyCanExecuteChangedFor(nameof(OptimizeCommand))]
        public partial bool UseOutputFolder { get; set; }

        /// <summary>Charts are changed where they are, which can't be undone.</summary>
        public bool IsInPlace => !UseOutputFolder;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(OptimizeCommand))]
        public partial string? OutputFolder { get; set; }

        // What's going on.

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsIdle))]
        [NotifyPropertyChangedFor(nameof(CloseLabel))]
        [NotifyCanExecuteChangedFor(nameof(EstimateCommand))]
        [NotifyCanExecuteChangedFor(nameof(OptimizeCommand))]
        [NotifyCanExecuteChangedFor(nameof(StopCommand))]
        [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
        [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
        [NotifyCanExecuteChangedFor(nameof(SaveDefaultsCommand))]
        [NotifyCanExecuteChangedFor(nameof(BrowseOutputCommand))]
        public partial bool IsBusy { get; set; }

        public bool IsIdle => !IsBusy;

        public string CloseLabel => Strings.CloseButton;

        [ObservableProperty]
        public partial double ProgressValue { get; set; }

        [ObservableProperty]
        public partial bool IsProgressIndeterminate { get; set; }

        [ObservableProperty]
        public partial string ProgressText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string StatusText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool StatusIsProblem { get; set; }

        [ObservableProperty]
        public partial bool HasResults { get; set; }

        [ObservableProperty]
        public partial bool HasEstimate { get; set; }

        /// <summary>What the window changed on disk, for the library to catch up with.</summary>
        public OptimizeWindowResult Result => new(_changedInPlace > 0, _written > 0);

        /// <summary>Reads the songs to work on: for the whole library, a fresh scan.</summary>
        public Task LoadAsync() => Track(ChangeScopeAsync());

        /// <summary>Stops everything going on and waits for it, once the window's closed.</summary>
        public async Task ShutdownAsync()
        {
            // The window's gone already, so there's nothing left to close once the run ends.
            _closeWhenIdle = false;
            _closing.Cancel();
            try
            {
                await _current;
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException)
            {
            }
        }

        /// <summary>The window is closing some other way: a run still going is stopped, and the window closes once it has.</summary>
        /// <returns>Whether the window may close now.</returns>
        public bool OnClosing()
        {
            if (!IsBusy)
                return true;

            _closeWhenIdle = true;
            _run?.Cancel();
            return false;
        }

        partial void OnSelectedSamplingChanged(OptimizeChoice<SamplingMode>? value) => RefreshCommands();

        partial void OnSelectedChartChanged(OptimizeChoice<SongEntry>? value) => RefreshCommands();

        private void OnImageToggleChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(OptimizeStrategyToggle.IsOn))
                return;

            // Images are estimated from a sample or every chart, not one.
            bool images = AlbumArt.IsOn || BackgroundArt.IsOn;
            if (images)
            {
                if (SelectedSampling == _single)
                    SelectedSampling = _sample;
                SamplingChoices.Remove(_single);
            }
            else if (!SamplingChoices.Contains(_single))
            {
                SamplingChoices.Insert(0, _single);
            }
        }

        // Settings.

        private void Apply(OptimizeSettings s)
        {
            _parallelism = s.Parallelism;
            _videoSpeed = s.VideoSpeed;

            Set(Metadata, s.MetadataEnabled);
            PruneEmptyParts = s.PruneEmptyParts;
            StripEditorEvents = s.StripEditorEvents;
            DropEmptyValues = s.DropEmptyValues;
            ReplaceLegacyKeys = s.ReplaceLegacyKeys;
            Set(Audio, s.AudioEnabled);
            SelectedBitrate = Pick(BitrateChoices, s.AudioBitrateKbps, BitrateChoice);
            Set(StemMixdown, s.StemMixdownEnabled);
            SelectedMixdownBitrate = Pick(MixdownBitrateChoices, s.StemMixdownBitrateKbps, BitrateChoice);
            Set(Video, s.VideoEnabled);
            SelectedVideoTarget = VideoTargets.First(c => c.Value == s.VideoTarget);
            VideoCq = s.VideoCq;
            VideoConvertUnsupported = s.VideoConvertUnsupported;
            Set(AlbumArt, s.AlbumArtEnabled);
            AlbumArtQuality = s.AlbumArtQuality;
            SelectedAlbumArtLimit = Pick(AlbumArtLimits, (s.AlbumArtMaxWidth, s.AlbumArtMaxHeight), LimitChoice);
            Set(BackgroundArt, s.BackgroundArtEnabled);
            BackgroundArtQuality = s.BackgroundArtQuality;
            SelectedBackgroundArtLimit = Pick(BackgroundArtLimits, (s.BackgroundArtMaxWidth, s.BackgroundArtMaxHeight), LimitChoice);
            MarginPercent = Math.Round(s.SavingsMargin * 100);
            SelectedSampling = SamplingChoices.FirstOrDefault(c => c.Value == s.Sampling) ?? _sample;
        }

        // A strategy whose backend is missing stays off, whatever was saved.
        private static void Set(OptimizeStrategyToggle toggle, bool on) => toggle.IsOn = on && toggle.IsAvailable;

        private OptimizeSettings ToSettings() => new()
        {
            MetadataEnabled = Metadata.IsOn,
            PruneEmptyParts = PruneEmptyParts,
            StripEditorEvents = StripEditorEvents,
            DropEmptyValues = DropEmptyValues,
            ReplaceLegacyKeys = ReplaceLegacyKeys,
            AudioEnabled = Audio.IsOn,
            AudioBitrateKbps = SelectedBitrate?.Value ?? AudioOptimizeOptions.DefaultBitrateKbps,
            StemMixdownEnabled = StemMixdown.IsOn,
            StemMixdownBitrateKbps = SelectedMixdownBitrate?.Value ?? AudioOptimizeOptions.DefaultBitrateKbps,
            VideoEnabled = Video.IsOn,
            VideoTarget = SelectedVideoTarget?.Value ?? VideoTarget.Fhd,
            VideoCq = (int)Math.Round(VideoCq),
            VideoConvertUnsupported = VideoConvertUnsupported,
            VideoSpeed = _videoSpeed,
            AlbumArtEnabled = AlbumArt.IsOn,
            AlbumArtQuality = (int)Math.Round(AlbumArtQuality),
            AlbumArtMaxWidth = SelectedAlbumArtLimit?.Value.W,
            AlbumArtMaxHeight = SelectedAlbumArtLimit?.Value.H,
            BackgroundArtEnabled = BackgroundArt.IsOn,
            BackgroundArtQuality = (int)Math.Round(BackgroundArtQuality),
            BackgroundArtMaxWidth = SelectedBackgroundArtLimit?.Value.W,
            BackgroundArtMaxHeight = SelectedBackgroundArtLimit?.Value.H,
            SavingsMargin = MarginPercent / 100,
            Sampling = SelectedSampling?.Value ?? SamplingMode.RandomSample,
            Parallelism = _parallelism,
        };

        /// <summary>Puts every setting back to the shipped defaults. The saved defaults are as they were until saved over.</summary>
        [RelayCommand(CanExecute = nameof(IsIdle))]
        private void Reset()
        {
            Apply(OptimizeSettings.Shipped);
            Say(Strings.OptimizeResetDone, problem: false);
        }

        /// <summary>Keeps the settings as they are now as the defaults the window starts from.</summary>
        [RelayCommand(CanExecute = nameof(IsIdle))]
        private void SaveDefaults()
        {
            if (_defaults.Save(ToSettings()))
                Say(Strings.OptimizeSavedDefaults, problem: false);
            else
                Say(Strings.OptimizeSaveDefaultsFailed, problem: true);
        }

        [RelayCommand(CanExecute = nameof(IsIdle))]
        private async Task BrowseOutputAsync()
        {
            string? folder = await _folderPicker.PickFolderAsync(Strings.OptimizeBrowseOutputTitle, OutputFolder);
            if (folder is null)
                return;

            OutputFolder = folder;
            UseOutputFolder = true;
        }

        // The songs to work on.

        private async Task ChangeScopeAsync()
        {
            Say(string.Empty, problem: false);
            await RunAsync(async ct =>
            {
                if (UseWholeLibrary)
                {
                    if (_library is null)
                    {
                        _library = await LoadLibraryAsync(ct);
                        // A fresh scan has what's been changed in place already.
                        _changedInPlace = 0;
                    }

                    if (_library is null)
                    {
                        // The scan stopped short, so there's no complete list to work on.
                        Say(Strings.OptimizeLibraryScanStopped, problem: true);
                        _scope = [];
                    }
                    else
                    {
                        _scope = _library;
                    }
                }
                else
                {
                    _scope = _request.Selected;
                }
            });
            ShowScope();
        }

        private async Task<IReadOnlyList<SongEntry>?> LoadLibraryAsync(CancellationToken cancellationToken)
        {
            IsProgressIndeterminate = true;
            var progress = new Progress<DedupScanProgress>(p =>
            {
                ProgressText = string.Format(CultureInfo.CurrentCulture, p.IsSearching ? Strings.ScanSearchingProgressFormat : Strings.ScanReadingProgressFormat, p.Read, p.Found);
                IsProgressIndeterminate = p.Found == 0;
                ProgressValue = p.Found == 0 ? 0 : 100.0 * p.Read / p.Found;
            });
            return await _request.LoadLibrary(progress, cancellationToken);
        }

        private void ShowScope()
        {
            ScopeText = UseWholeLibrary
                ? string.Format(CultureInfo.CurrentCulture, Strings.OptimizeScopeLibraryFormat, _scope.Count)
                : SelectedScopeText;

            string? keep = SelectedChart?.Value.FullPath;
            ChartChoices.Clear();
            foreach (var entry in _scope)
                ChartChoices.Add(new OptimizeChoice<SongEntry>(entry, entry.DisplayName));
            SelectedChart = ChartChoices.FirstOrDefault(c => c.Value.FullPath == keep) ?? ChartChoices.FirstOrDefault();
            NotifyOutputFolderProblem();
            RefreshCommands();
        }

        // Estimating.

        private bool CanEstimate() =>
            IsIdle && _scope.Count > 0 && EstimatedStrategies().Any() && (!IsSingleChartMode || SelectedChart is not null);

        private IEnumerable<OptimizeStrategy> EstimatedStrategies() =>
            Toggles.Where(t => t.IsOn && t.Strategy != OptimizeStrategy.Metadata).Select(t => t.Strategy);

        /// <summary>Estimates what each strategy that's on would save, from the sampled charts, and lists it by codec.</summary>
        [RelayCommand(CanExecute = nameof(CanEstimate))]
        private Task EstimateAsync() => Track(RunAsync(async ct =>
        {
            Say(string.Empty, problem: false);
            var options = Options();
            var charts = await InspectAsync(_scope, options.Parallelism, cached: true, ct);
            if (charts.Count == 0)
            {
                Say(Strings.OptimizeNoCharts, problem: true);
                return;
            }

            var optimizers = Optimizers(options);
            var mode = SelectedSampling?.Value ?? SamplingMode.RandomSample;
            var selected = IsSingleChartMode ? SelectedChart?.Value : null;
            var sections = new List<OptimizeEstimateSection>();
            foreach (var strategy in EstimatedStrategies().ToList())
            {
                string title = Toggles.First(t => t.Strategy == strategy).Title;
                var progress = new Progress<(int Done, int Total)>(p =>
                {
                    ProgressText = string.Format(CultureInfo.CurrentCulture, Strings.OptimizeEstimatingFormat, title, p.Done, p.Total);
                    ProgressValue = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
                });
                IsProgressIndeterminate = true;
                ProgressText = string.Format(CultureInfo.CurrentCulture, Strings.OptimizeEstimatingFormat, title, 0, 0);

                var estimate = await SavingsEstimator.EstimateAsync(strategy, charts, options, optimizers, mode, selected, progress: progress, cancellationToken: ct);
                sections.Add(Section(title, estimate, options));
            }

            EstimateSections.Clear();
            foreach (var section in sections)
                EstimateSections.Add(section);
            HasEstimate = true;
            Say(WithUnreadable(Strings.OptimizeEstimateDone), problem: _unreadable > 0);
        }));

        private OptimizeEstimateSection Section(string title, SavingsEstimate estimate, OptimizeOptions options)
        {
            bool compatibility = estimate.Strategy != OptimizeStrategy.Video || options.Video.ConvertUnsupported;
            var table = SavingsTable.From(estimate, compatibility);

            var rows = table.Rows.Select(r => new OptimizeEstimateRow(
                r.CodecKey,
                string.Format(CultureInfo.CurrentCulture, Strings.OptimizeSavingsAssetsFormat, r.AssetsSampled, r.AssetsTotal),
                r.AssetsSampled == 0 ? Strings.OptimizeNotEstimated : Signed(r.EstimatedSavingBytes),
                r.SavingPerChartBytes is { } perChart ? Signed(perChart) : Strings.OptimizeNotEstimated,
                r.EstimatedSavingBytes < 0,
                r.IsCompatibilityConversion)).ToList();

            string? note = table.CompatibilityCostBytes > 0
                ? string.Format(CultureInfo.CurrentCulture, compatibility ? Strings.OptimizeCompatibilityCostFormat : Strings.OptimizeCompatibilityWouldCostFormat, ByteSizeFormatter.Format((ulong)table.CompatibilityCostBytes))
                : null;

            return new OptimizeEstimateSection(
                title,
                string.Format(CultureInfo.CurrentCulture, Strings.OptimizeSavingsSampleFormat, estimate.ChartsSampled, estimate.ChartsTotal),
                rows,
                string.Format(CultureInfo.CurrentCulture, Strings.OptimizeSavingsTotalFormat, Signed(table.TotalSavingBytes)),
                note);
        }

        // "Saves 12 MB", or "Costs 3 MB" when it grows.
        private static string Signed(long bytes) => bytes >= 0
            ? string.Format(CultureInfo.CurrentCulture, Strings.OptimizeSavesFormat, ByteSizeFormatter.Format((ulong)bytes))
            : string.Format(CultureInfo.CurrentCulture, Strings.OptimizeCostsFormat, ByteSizeFormatter.Format((ulong)-bytes));

        // Optimizing.

        private bool CanOptimize() =>
            IsIdle && _scope.Count > 0 && Toggles.Any(t => t.IsOn)
            && (IsInPlace || (!string.IsNullOrWhiteSpace(OutputFolder) && !HasOutputFolderProblem));

        /// <summary>Why the output folder can't be used, or empty when it can, or hasn't been chosen yet.</summary>
        public string OutputFolderProblemText
        {
            get
            {
                if (!UseOutputFolder || string.IsNullOrWhiteSpace(OutputFolder))
                    return string.Empty;

                string folder;
                try
                {
                    if (!Path.IsPathRooted(OutputFolder))
                        return Strings.OptimizeOutputFolderNotFullPath;
                    folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(OutputFolder));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    return Strings.OptimizeOutputFolderNotFullPath;
                }

                // A chart written into itself, or over another, isn't a copy of it.
                foreach (var entry in _scope)
                {
                    string chart = Path.TrimEndingDirectorySeparator(entry.FullPath);
                    if (folder.Equals(chart, StringComparison.OrdinalIgnoreCase)
                        || (entry.Kind != SongEntryKind.SngFile
                            && folder.StartsWith(chart + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                        return Strings.OptimizeOutputFolderInsideChart;
                }

                return string.Empty;
            }
        }

        public bool HasOutputFolderProblem => OutputFolderProblemText.Length > 0;

        partial void OnOutputFolderChanged(string? value) => NotifyOutputFolderProblem();

        partial void OnUseOutputFolderChanged(bool value) => NotifyOutputFolderProblem();

        private void NotifyOutputFolderProblem()
        {
            OnPropertyChanged(nameof(OutputFolderProblemText));
            OnPropertyChanged(nameof(HasOutputFolderProblem));
        }

        /// <summary>Optimizes every chart, in place or into the output folder, with the strategies that are on.</summary>
        [RelayCommand(CanExecute = nameof(CanOptimize))]
        private async Task OptimizeAsync()
        {
            await Track(RunAsync(OptimizeCoreAsync));

            // The whole library is scanned again, so the next estimate or run sees the charts as they now are.
            // Not when the window is closing, as the run was stopped for that.
            if (!_closeWhenIdle && UseWholeLibrary && _library is null && _changedInPlace > 0)
                await Track(ChangeScopeAsync());
        }

        private async Task OptimizeCoreAsync(CancellationToken ct)
        {
            if (IsInPlace)
            {
                string message = string.Format(CultureInfo.CurrentCulture, Strings.OptimizeConfirmMessageFormat, _scope.Count);
                if (!await _confirmation.ConfirmAsync(Strings.OptimizeConfirmTitle, message, Strings.OptimizeConfirmButton))
                    return;
            }

            Say(string.Empty, problem: false);
            Results.Clear();
            HasResults = false;
            _applied = _skipped = _failed = 0;
            _bytesSaved = 0;

            var options = Options();
            var charts = await InspectAsync(_scope, options.Parallelism, cached: false, ct);
            _inspected.Clear();
            if (charts.Count == 0)
            {
                Say(Strings.OptimizeNoCharts, problem: true);
                return;
            }

            var optimizers = Optimizers(options);
            int changedBefore = _changedInPlace;
            IsProgressIndeterminate = false;
            var planning = new Progress<(int Done, int Total)>(p =>
            {
                ProgressText = string.Format(CultureInfo.CurrentCulture, Strings.OptimizePlanningFormat, p.Done, p.Total);
                ProgressValue = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
            });
            var plans = await Task.Run(() => OptimizationPlanner.PlanAllAsync(charts, options, optimizers, planning, ct), ct);

            var queue = new UiBatchQueue<OptimizeProgress>(TimeSpan.FromMilliseconds(100), Take, TimeSpan.FromMilliseconds(30));
            queue.Start();
            bool stopped = false;
            try
            {
                await Task.Run(() => OptimizeJob.RunAsync(plans, options, optimizers, _services, queue, ct), ct);
            }
            catch (OperationCanceledException)
            {
                stopped = true;
            }
            finally
            {
                // What was reported before a stop or a failure still counts.
                queue.Stop();
                queue.TakeAll();
                // The library as it was read is out of date only if charts were rewritten where they are; a run into the output folder leaves it as it was.
                if (_changedInPlace > changedBefore)
                    _library = null;
                _inspected.Clear();
                EstimateSections.Clear();
                HasEstimate = false;
                HasResults = Results.Count > 0;
                string summary = Summary();
                Say(WithUnreadable(stopped ? Strings.OptimizeStopped + " " + summary : summary), problem: _failed > 0 || _unreadable > 0);
            }
        }

        private void Take(IEnumerable<OptimizeProgress> batch)
        {
            foreach (var report in batch)
            {
                if (report.Total > 0)
                {
                    ProgressValue = 100.0 * report.Done / report.Total;
                    ProgressText = report.CurrentAsset is { } asset && report.Current is { } current
                        ? string.Format(CultureInfo.CurrentCulture, Strings.OptimizeRunningAssetFormat, current.DisplayName, asset)
                        : string.Format(CultureInfo.CurrentCulture, Strings.OptimizeRunningFormat, report.Done, report.Total);
                }

                if (report.Completed is { } item)
                    Record(item);
            }
        }

        private void Record(OptimizeItemResult item)
        {
            string name = item.Entry.DisplayName;
            switch (item.Status)
            {
                case OptimizeItemStatus.Succeeded:
                    _applied++;
                    _bytesSaved += item.BytesSaved;
                    if (IsInPlace)
                        _changedInPlace++;
                    else
                        _written++;
                    Results.Add(new OptimizeResultRow(name, string.Format(CultureInfo.CurrentCulture, Strings.OptimizeStatusSavedFormat, SignedSize(item.BytesSaved)), false));
                    break;

                case OptimizeItemStatus.Failed:
                    _failed++;
                    Results.Add(new OptimizeResultRow(name, string.Format(CultureInfo.CurrentCulture, Strings.OptimizeStatusFailedFormat, item.ErrorMessage ?? string.Empty), true));
                    break;

                default:
                    _skipped++;
                    // A chart with nothing to do is the usual case in a library; listing each would bury the rest.
                    if (item.SkipReason != OptimizeItemSkipReason.NothingToDo)
                        Results.Add(new OptimizeResultRow(name, SkipText(item.SkipReason), true));
                    break;
            }
        }

        private static string SignedSize(long bytes) => bytes >= 0
            ? ByteSizeFormatter.Format((ulong)bytes)
            : "-" + ByteSizeFormatter.Format((ulong)-bytes);

        private static string SkipText(OptimizeItemSkipReason reason) => reason switch
        {
            OptimizeItemSkipReason.HasUnsavedChanges => Strings.OptimizeSkipHasUnsavedChanges,
            OptimizeItemSkipReason.InUseElsewhere => Strings.OptimizeSkipInUse,
            OptimizeItemSkipReason.ChartChanged => Strings.OptimizeSkipChartChanged,
            OptimizeItemSkipReason.OutputExists => Strings.OptimizeSkipOutputExists,
            _ => Strings.OptimizeSkipNothingToDo,
        };

        // Charts that couldn't be read were left out of the estimate or run, which would otherwise look like all of them were done.
        private string WithUnreadable(string text) => _unreadable == 0
            ? text
            : text + " " + string.Format(CultureInfo.CurrentCulture, Strings.OptimizeUnreadableFormat, _unreadable);

        private string Summary() => string.Format(
            CultureInfo.CurrentCulture, Strings.OptimizeResultSummaryFormat, _applied, _skipped, _failed, SignedSize(_bytesSaved));

        /// <summary>Stops the estimate or run going on; charts already finished keep their changes.</summary>
        [RelayCommand(CanExecute = nameof(IsBusy))]
        private void Stop() => _run?.Cancel();

        [RelayCommand(CanExecute = nameof(IsIdle))]
        private void Close() => CloseRequested?.Invoke();

        // Shared by the commands.

        private OptimizeOptions Options() => ToSettings().ToOptions(UseOutputFolder ? OutputFolder : null);

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

        private async Task<IReadOnlyList<ChartAssets>> InspectAsync(IReadOnlyList<SongEntry> entries, int parallelism, bool cached, CancellationToken cancellationToken)
        {
            var found = new ChartAssets?[entries.Count];
            int done = 0;
            int failed = 0;
            var progress = new Progress<int>(n =>
            {
                ProgressText = string.Format(CultureInfo.CurrentCulture, Strings.OptimizeInspectingFormat, n, entries.Count);
                ProgressValue = entries.Count == 0 ? 0 : 100.0 * n / entries.Count;
            });
            IsProgressIndeterminate = false;

            await Parallel.ForEachAsync(
                Enumerable.Range(0, entries.Count),
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism), CancellationToken = cancellationToken },
                async (i, token) =>
                {
                    var entry = entries[i];
                    ChartAssets? assets = null;
                    if (cached)
                    {
                        lock (_inspected)
                            _inspected.TryGetValue(entry.FullPath, out assets);
                    }

                    if (assets is null)
                    {
                        try
                        {
                            assets = await AssetInspector.InspectAsync(entry, _backends.Probes, token);
                            if (cached)
                            {
                                lock (_inspected)
                                    _inspected[entry.FullPath] = assets;
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                        {
                            // A chart that can't be read is left out, and the status says how many were.
                            Interlocked.Increment(ref failed);
                        }
                    }

                    found[i] = assets;
                    ((IProgress<int>)progress).Report(Interlocked.Increment(ref done));
                });

            _unreadable = failed;
            return [.. found.OfType<ChartAssets>()];
        }

        // Runs one operation: the window is busy for as long as it takes, and Stop cancels it.
        private async Task RunAsync(Func<CancellationToken, Task> body)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
            _run = cts;
            IsBusy = true;
            ProgressText = string.Empty;
            ProgressValue = 0;
            IsProgressIndeterminate = false;
            try
            {
                await body(cts.Token);
            }
            catch (OperationCanceledException)
            {
                Say(Strings.OptimizeStopped, problem: false);
            }
            catch (Exception ex)
            {
                // Whatever went wrong, it's shown here: this runs from commands and fire-and-forget calls, where nothing else would see it.
                System.Diagnostics.Trace.TraceError($"Optimize window operation failed: {ex}");
                Say(string.Format(CultureInfo.CurrentCulture, Strings.OptimizeFailedFormat, ex.Message), problem: true);
            }
            finally
            {
                _run = null;
                IsBusy = false;
                IsProgressIndeterminate = false;
                ProgressText = string.Empty;
                ProgressValue = 0;
                RefreshCommands();

                // The window was closed while this was going on, so it closes now that it has stopped.
                if (_closeWhenIdle)
                    CloseRequested?.Invoke();
            }
        }

        // Keeps the running task, so shutting down can wait for it.
        private Task Track(Task task)
        {
            _current = Task.WhenAll(_current, task);
            return task;
        }

        private void Say(string text, bool problem)
        {
            StatusText = text;
            StatusIsProblem = problem;
        }

        private void RefreshCommands()
        {
            EstimateCommand.NotifyCanExecuteChanged();
            OptimizeCommand.NotifyCanExecuteChanged();
        }

        private OptimizeStrategyToggle Toggle(OptimizeStrategy strategy, string title, string description) =>
            new(strategy, title, description, _backends.UnavailableReason(strategy));

        private static OptimizeChoice<int> BitrateChoice(int kbps) => new(kbps, kbps switch
        {
            AudioOptimizeOptions.DefaultBitrateKbps => string.Format(CultureInfo.CurrentCulture, Strings.OptimizeBitrateDefaultFormat, kbps),
            AudioOptimizeOptions.SmallerBitrateKbps => string.Format(CultureInfo.CurrentCulture, Strings.OptimizeBitrateSmallerFormat, kbps),
            _ => string.Format(CultureInfo.CurrentCulture, Strings.OptimizeBitrateFormat, kbps),
        });

        private static OptimizeChoice<(int? W, int? H)> LimitChoice((int? W, int? H) limit) => new(limit, limit is { W: { } w, H: { } h }
            ? string.Format(CultureInfo.CurrentCulture, Strings.OptimizeSizeLimitFormat, w, h)
            : Strings.OptimizeNoLimit);

        // The choice for a value, adding one when what was saved isn't among the presets.
        private static OptimizeChoice<T> Pick<T>(ObservableCollection<OptimizeChoice<T>> choices, T value, Func<T, OptimizeChoice<T>> make)
        {
            var existing = choices.FirstOrDefault(c => EqualityComparer<T>.Default.Equals(c.Value, value));
            if (existing is not null)
                return existing;

            var added = make(value);
            choices.Add(added);
            return added;
        }
    }
}
