using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using ChartBuilder.Controls;
using ChartBuilder.Resources;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Difficulty;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace ChartBuilder.ViewModels
{
    /// <summary>What the spider graph shows: the whole chart, the selection, or the moment at the playhead.</summary>
    public enum DifficultyScope
    {
        Chart,
        Selection,
        Playhead,
    }

    /// <summary>A scope the spider graph can show, as the picker lists it.</summary>
    public sealed record DifficultyScopeChoice(DifficultyScope Scope, string Label);

    /// <summary>An attribute's line on the difficulty graph, which can be shown or hidden.</summary>
    public sealed partial class DifficultyLineToggle(DifficultyAttribute attribute, string label) : ObservableObject
    {
        public DifficultyAttribute Attribute { get; } = attribute;

        public string Label { get; } = label;

        public Avalonia.Media.IBrush Brush => DifficultyPalette.Of(Attribute);

        [ObservableProperty]
        public partial bool IsShown { get; set; }
    }

    /// <summary>
    /// The difficulty analysis of a five-fret track (docs/difficulty-model.md §9): its rank, a spider graph of its five
    /// attributes for the whole chart, the selection or the playhead, and its difficulty over the song. It follows the lane
    /// in focus, or a part and difficulty picked, docked in the Editor's side panel or in a window of its own.
    /// </summary>
    /// <remarks>
    /// The track is read again a moment after an edit that touches it, off the UI thread, and only while something shows
    /// it. Drag previews wait for the drag to end. Times on the graph are in seconds of audio, the song's delay allowed for.
    /// </remarks>
    public sealed partial class DifficultyViewModel : ViewModelBase, IDisposable
    {
        private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(150);

        // The graph's points are no closer than this, and there are no more than MaxPoints.
        private const double PointSpacingMs = 50;
        private const int MaxPoints = 20_000;

        private readonly ChartEditorViewModel _editor;
        private readonly DispatcherTimer _timer;
        private readonly DispatcherTimer _playhead;
        private readonly DispatcherTimer _isolate;
        private int _shownCount;
        private int _runs;
        private int _isolations;
        private Analysis? _analysis;

        // The selection as last scored alone, and what it scored, shown until the selection or the analysis changes.
        private (Analysis Analysis, DifficultySpan Span, DifficultyAttributes Values, double Score)? _isolated;

        // The track the last run was for, so a change of focus elsewhere doesn't run it again.
        private ChartTrackKey? _ranFor;

        // What a pass read: the track, its notes and what was made of them, and how its times map to the audio.
        private sealed record Analysis(ChartTrackKey Track, IReadOnlyList<DifficultyNote> Notes, DifficultyProfile Profile, TempoMap Tempo, double DelaySeconds, IReadOnlyList<OverviewSection> Sections)
        {
            public double MsAt(double audioSeconds) => (audioSeconds - DelaySeconds) * 1000;

            public double AudioAt(double ms) => (ms / 1000) + DelaySeconds;
        }

        public DifficultyViewModel(ChartEditorViewModel editor)
        {
            _editor = editor;
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Delay };
            _timer.Tick += (_, _) => _ = RunAsync();
            _playhead = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
            _playhead.Tick += (_, _) => RefreshScope();
            _isolate = new DispatcherTimer(DispatcherPriority.Background) { Interval = Delay };
            _isolate.Tick += (_, _) => _ = IsolateAsync();
            Scopes =
            [
                new DifficultyScopeChoice(DifficultyScope.Chart, Strings.DifficultyScopeChart),
                new DifficultyScopeChoice(DifficultyScope.Selection, Strings.DifficultyScopeSelection),
                new DifficultyScopeChoice(DifficultyScope.Playhead, Strings.DifficultyScopePlayhead),
            ];
            SelectedScope = Scopes[0];
            Lines = [.. DifficultyAttributes.All.Select(a => new DifficultyLineToggle(a, LabelOf(a)))];
            foreach (var line in Lines)
                line.PropertyChanged += OnLineToggled;
            AxisLabels = [.. DifficultySpiderView.AxisOrder.Select(LabelOf)];
            Describe = DescribeAt;
            RefreshChoices();
            SelectedTrack = TrackChoices[0];
            Session.ChartChanged += OnChartChanged;
            Session.StateChanged += OnSessionState;
            Session.SongSettingsChanged += OnSongSettingsChanged;
            Session.Selection.Changed += OnSelectionChanged;
            Playback.PropertyChanged += OnPlaybackChanged;
        }

        private static ILogger Logger => Log.ForContext<DifficultyViewModel>();

        private EditorSession Session => _editor.Session;

        public PlaybackViewModel Playback => _editor.Playback;

        /// <summary>The lane in focus, then each five-fret part and difficulty the chart has.</summary>
        [ObservableProperty]
        public partial IReadOnlyList<PreviewTrackChoice> TrackChoices { get; private set; } = [];

        /// <summary>What's analyzed: the lane in focus, or a part and difficulty picked.</summary>
        [ObservableProperty]
        public partial PreviewTrackChoice? SelectedTrack { get; set; }

        /// <summary>The part and difficulty analyzed, or null when there's none.</summary>
        public ChartTrackKey? Track => SelectedTrack?.Track ?? Session.Focused;

        /// <summary>Why there's nothing to show; null when there's an analysis.</summary>
        [ObservableProperty]
        public partial string? Placeholder { get; private set; } = Strings.DifficultyNothing;

        public bool HasAnalysis => Placeholder is null;

        /// <summary>"Rank 5.4", or with "overchart" from 7; null with nothing analyzed.</summary>
        [ObservableProperty]
        public partial string? RankText { get; private set; }

        /// <summary>The rank's whole tier, for its colour.</summary>
        [ObservableProperty]
        public partial int Tier { get; private set; }

        /// <summary>What the rank was worked out from, for its tooltip.</summary>
        [ObservableProperty]
        public partial string? RankToolTip { get; private set; }

        public IReadOnlyList<DifficultyScopeChoice> Scopes { get; }

        [ObservableProperty]
        public partial DifficultyScopeChoice SelectedScope { get; set; }

        /// <summary>Whether the selection is scored as if practised alone, with nothing built up before it (§6.4).</summary>
        [ObservableProperty]
        public partial bool IsIsolated { get; set; }

        public bool IsSelectionScope => SelectedScope.Scope == DifficultyScope.Selection;

        /// <summary>The spider graph's values for the scope; null when there are none, as with no selection.</summary>
        [ObservableProperty]
        public partial DifficultyAttributes? SpiderValues { get; private set; }

        /// <summary>The whole chart's values, drawn faintly behind the selection's or the playhead's.</summary>
        [ObservableProperty]
        public partial DifficultyAttributes? SpiderComparison { get; private set; }

        /// <summary>The scope's score and rank, or why it has none.</summary>
        [ObservableProperty]
        public partial string? ScopeText { get; private set; }

        /// <summary>The spider graph's axis names, in its fixed order.</summary>
        public IReadOnlyList<string> AxisLabels { get; }

        /// <summary>The attributes' lines, to show or hide on the graph.</summary>
        public IReadOnlyList<DifficultyLineToggle> Lines { get; }

        /// <summary>The attributes whose lines are shown.</summary>
        [ObservableProperty]
        public partial IReadOnlyList<DifficultyAttribute> VisibleLines { get; private set; } = [];

        /// <summary>What the graph draws; null with nothing analyzed.</summary>
        [ObservableProperty]
        public partial DifficultyGraph? Graph { get; private set; }

        /// <summary>Where the selection starts and ends on the graph, in seconds of audio; NaN with none.</summary>
        [ObservableProperty]
        public partial double SelectionStart { get; private set; } = double.NaN;

        [ObservableProperty]
        public partial double SelectionEnd { get; private set; } = double.NaN;

        /// <summary>What the graph's tooltip says at a time, in seconds of audio.</summary>
        public Func<double, string?> Describe { get; }

        /// <summary>Whether a docked tab or a window shows it, so it's kept up to date.</summary>
        public bool IsShown => _shownCount > 0;

        /// <summary>The tab or window was shown (true) or hidden; it's analyzed only while one shows it.</summary>
        public void SetShown(bool shown)
        {
            _shownCount = Math.Max(0, _shownCount + (shown ? 1 : -1));

            // Edits made while hidden weren't read, so it's read again on being shown.
            if (shown && IsShown)
                Schedule(immediately: true);
            UpdatePlayheadTimer();
        }

        /// <summary>Opens the analysis in a window of its own, with room for the graph.</summary>
        [RelayCommand]
        private void OpenWindow() => _editor.ShowDifficultyWindow(this);

        partial void OnSelectedTrackChanged(PreviewTrackChoice? value) => Schedule(immediately: true);

        partial void OnPlaceholderChanged(string? value) => OnPropertyChanged(nameof(HasAnalysis));

        partial void OnSelectedScopeChanged(DifficultyScopeChoice value)
        {
            OnPropertyChanged(nameof(IsSelectionScope));
            RefreshScope();
            UpdatePlayheadTimer();
        }

        partial void OnIsIsolatedChanged(bool value) => RefreshScope();

        public void Dispose()
        {
            _timer.Stop();
            _playhead.Stop();
            _isolate.Stop();
            Session.ChartChanged -= OnChartChanged;
            Session.StateChanged -= OnSessionState;
            Session.SongSettingsChanged -= OnSongSettingsChanged;
            Session.Selection.Changed -= OnSelectionChanged;
            Playback.PropertyChanged -= OnPlaybackChanged;
            foreach (var line in Lines)
                line.PropertyChanged -= OnLineToggled;
        }

        private void OnChartChanged(object? sender, EditorChartChangedEventArgs e)
        {
            if (e.Affected.Song.HasFlag(ChartShared.Core.Charting.Commands.SongChanges.Parts))
                RefreshChoices();
            // The tempo map moves every note in time, and the events hold the sections and [end].
            if (Track is { } track && (e.Affected.Touches(track) || e.Affected.RebuildsAllTracks || (e.Affected.Song & ChartShared.Core.Charting.Commands.SongChanges.Events) != 0))
                Schedule();
        }

        // The lane in focus changed, which it follows unless a track is picked.
        private void OnSessionState(object? sender, EventArgs e)
        {
            if (SelectedTrack?.Track is null && Track != _ranFor)
                Schedule(immediately: true);
        }

        private void OnSongSettingsChanged(object? sender, EventArgs e) => Schedule();

        private void OnSelectionChanged(object? sender, EventArgs e)
        {
            RefreshSelection();
            if (IsSelectionScope)
                RefreshScope();
        }

        private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PlaybackViewModel.IsPlaying))
                UpdatePlayheadTimer();
            else if (e.PropertyName == nameof(PlaybackViewModel.PositionSeconds) && SelectedScope.Scope == DifficultyScope.Playhead && !Playback.IsPlaying)
                RefreshScope();
            else if (e.PropertyName == nameof(PlaybackViewModel.LengthSeconds))
                Schedule();
        }

        private void OnLineToggled(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DifficultyLineToggle.IsShown))
                VisibleLines = [.. Lines.Where(l => l.IsShown).Select(l => l.Attribute)];
        }

        // The playhead's values follow the clock while playing, ten times a second.
        private void UpdatePlayheadTimer()
        {
            if (IsShown && SelectedScope.Scope == DifficultyScope.Playhead && Playback.IsPlaying)
                _playhead.Start();
            else
                _playhead.Stop();
        }

        private void RefreshChoices()
        {
            var keep = SelectedTrack?.Track;
            var choices = new List<PreviewTrackChoice> { new(null, Strings.PreviewFollowLane) };
            foreach (var part in Session.Chart.Parts.Where(p => p.Kind == ChartInstrumentKind.FiveFret))
            {
                foreach (var difficulty in Enum.GetValues<ChartDifficulty>().OrderDescending())
                {
                    choices.Add(new PreviewTrackChoice(
                        new ChartTrackKey(part.Instrument, difficulty),
                        string.Format(CultureInfo.CurrentCulture, Strings.LaneTitleFormat, ChartLabels.InstrumentName(part.Instrument), ChartLabels.DifficultyName(difficulty))));
                }
            }

            TrackChoices = choices;
            SelectedTrack = choices.FirstOrDefault(c => c.Track == keep) ?? choices[0];
        }

        // A moment after the last change, so a burst of edits is read once; at once for a change of track.
        private void Schedule(bool immediately = false)
        {
            if (!IsShown)
                return;
            _timer.Stop();
            if (immediately)
                _ = RunAsync();
            else
                _timer.Start();
        }

        private async Task RunAsync()
        {
            _timer.Stop();
            if (!IsShown)
                return;

            // A drag's previews aren't kept, so it waits for the drag to end.
            if (Session.HasPreview)
            {
                _timer.Start();
                return;
            }

            int run = ++_runs;
            _ranFor = Track;
            if (Track is not { } key || Session.Chart.PartOf(key.Instrument) is not { } part || part[key.Difficulty].Notes.Count == 0)
            {
                Show(null, Strings.DifficultyNothing);
                return;
            }

            if (part.Kind != ChartInstrumentKind.FiveFret)
            {
                Show(null, Strings.DifficultyNotFiveFret);
                return;
            }

            // Read on the UI thread, which the session belongs to; worked out off it.
            var game = Session.GameTrack(key);
            var tempo = Session.TempoMap;
            long? end = DifficultyNotes.EndTickOf(Session.Chart);
            double delay = Session.Delay.TotalSeconds;
            double length = Playback.LengthSeconds;
            var sections = Session.Chart.Events
                .Select(e => SectionEvents.NameOf(e.Text) is { } name ? new OverviewSection(Session.AudioAt(e.Tick), name) : null)
                .OfType<OverviewSection>()
                .ToList();
            var solos = part.Phrases.Where(p => p.Kind == PhraseKind.Solo).Select(p => (Session.AudioAt(p.Tick), Session.AudioAt(p.Tick + p.Length))).ToList();
            try
            {
                var (analysis, graph) = await Task.Run(() =>
                {
                    var notes = DifficultyNotes.Of(game, tempo, end);
                    var profile = DifficultyAnalyzer.Analyze(notes);
                    var result = new Analysis(key, notes, profile, tempo, delay, sections);
                    return (result, GraphOf(result, length, solos));
                });
                if (run != _runs)
                    return;
                if (analysis.Notes.Count < 2)
                {
                    Show(null, Strings.DifficultyNothing);
                    return;
                }

                Show(analysis, null);
                Graph = graph;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "The difficulty analysis of {Track} failed", key);
                if (run == _runs)
                    Show(null, Strings.DifficultyFailed);
            }
        }

        // The graph's points, from the song's start to its end or the chart's, whichever is later.
        private static DifficultyGraph GraphOf(Analysis analysis, double lengthSeconds, IReadOnlyList<(double, double)> solos)
        {
            var profile = analysis.Profile;
            double toMs = Math.Max(profile.EndMs, analysis.MsAt(lengthSeconds));
            double fromMs = Math.Min(0, analysis.MsAt(0));
            int points = (int)Math.Clamp(Math.Ceiling((toMs - fromMs) / PointSpacingMs) + 1, 2, MaxPoints);
            var curve = profile.Curve(fromMs, toMs, points);
            var contributors = profile.Contributors().Select(c => (analysis.AudioAt(c.FromMs), analysis.AudioAt(c.ToMs))).ToList();
            var thresholds = Enumerable.Range(1, RankCurve.FirstOverchart).Select(r => RankCurve.ThresholdOf(r, profile.Parameters)).ToList();
            return new DifficultyGraph(analysis.AudioAt(fromMs), analysis.AudioAt(toMs), curve.Points, analysis.Sections, solos, contributors, thresholds);
        }

        private void Show(Analysis? analysis, string? placeholder)
        {
            _analysis = analysis;
            Placeholder = placeholder;
            if (analysis is null)
            {
                (RankText, RankToolTip, Tier, Graph) = (null, null, 0, null);
                RefreshScope();
                return;
            }

            var profile = analysis.Profile;
            Tier = profile.Tier;
            RankText = string.Format(CultureInfo.CurrentCulture, profile.Tier >= RankCurve.FirstOverchart ? Strings.DifficultyOverchartFormat : Strings.DifficultyRankFormat, profile.Rank);
            RankToolTip = string.Format(CultureInfo.CurrentCulture, Strings.DifficultyRankToolTipFormat, profile.ChartScore);
            RefreshSelection();
            RefreshScope();
        }

        private void RefreshSelection()
        {
            if (_analysis is not null && Session.Selection.Range is { } range)
                (SelectionStart, SelectionEnd) = (Session.AudioAt(range.From), Session.AudioAt(range.To));
            else
                (SelectionStart, SelectionEnd) = (double.NaN, double.NaN);
        }

        // The spider graph's values, and the score beneath it, for the scope.
        private void RefreshScope()
        {
            if (_analysis is not { } analysis)
            {
                (SpiderValues, SpiderComparison, ScopeText) = (null, null, null);
                return;
            }

            var profile = analysis.Profile;
            switch (SelectedScope.Scope)
            {
                case DifficultyScope.Chart:
                    (SpiderValues, SpiderComparison) = (profile.ChartAttributes, null);
                    ScopeText = ScoreText(profile.ChartScore, profile);
                    break;
                case DifficultyScope.Selection when Session.Selection.Range is { } range:
                    var span = SpanOf(analysis, range);
                    if (!IsIsolated)
                    {
                        (SpiderValues, SpiderComparison) = (profile.AttributeScores(span.FromMs, span.ToMs), profile.ChartAttributes);
                        ScopeText = ScoreText(profile.Score(span.FromMs, span.ToMs), profile);
                    }
                    else if (_isolated is { } isolated && ReferenceEquals(isolated.Analysis, analysis) && isolated.Span == span)
                    {
                        (SpiderValues, SpiderComparison) = (isolated.Values, profile.ChartAttributes);
                        ScopeText = ScoreText(isolated.Score, profile);
                    }
                    else
                    {
                        // Scored alone, the stretch is read again from its first note: off the UI thread, once the
                        // selection settles. What's shown stays until then.
                        _isolate.Stop();
                        _isolate.Start();
                    }

                    break;
                case DifficultyScope.Selection:
                    (SpiderValues, SpiderComparison, ScopeText) = (null, profile.ChartAttributes, Strings.DifficultyNoSelection);
                    break;
                default:
                    double seconds = Playback.IsPlaying ? Playback.Clock().TotalSeconds : Playback.PositionSeconds;
                    var moment = profile.EnvelopeAt(analysis.MsAt(seconds));
                    (SpiderValues, SpiderComparison) = (moment.Attributes, profile.ChartAttributes);
                    ScopeText = ScoreText(moment.Overall, profile);
                    break;
            }
        }

        // The selection's stretch, in milliseconds of the chart; the last note selected included.
        private static DifficultySpan SpanOf(Analysis analysis, (long From, long To) range) =>
            new(analysis.Tempo.SecondsAt(range.From) * 1000, (analysis.Tempo.SecondsAt(range.To) * 1000) + 1);

        // Scores the selection alone, off the UI thread; a newer selection or analysis replaces it.
        private async Task IsolateAsync()
        {
            _isolate.Stop();
            if (_analysis is not { } analysis || !IsIsolated || !IsSelectionScope || Session.Selection.Range is not { } range)
                return;

            int run = ++_isolations;
            var span = SpanOf(analysis, range);
            try
            {
                var (values, score) = await Task.Run(() =>
                {
                    var alone = DifficultyAnalyzer.Analyze(analysis.Notes, span, analysis.Profile.Parameters);
                    return (alone.AttributeScores(span.FromMs, span.ToMs), alone.Score(span.FromMs, span.ToMs));
                });
                if (run != _isolations || !ReferenceEquals(_analysis, analysis))
                    return;
                _isolated = (analysis, span, values, score);
                RefreshScope();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Scoring the selection of {Track} alone failed", analysis.Track);
            }
        }

        private static string ScoreText(double score, DifficultyProfile profile) =>
            string.Format(CultureInfo.CurrentCulture, Strings.DifficultyScoreFormat, score, RankCurve.Continuous(score, profile.Parameters));

        // The graph's tooltip: the time and section, the difficulty and its rank, and what's hardest then.
        private string? DescribeAt(double seconds)
        {
            if (_analysis is not { } analysis)
                return null;
            double ms = analysis.MsAt(seconds);
            var moment = analysis.Profile.EnvelopeAt(ms);
            string time = SongTime.Format(TimeSpan.FromSeconds(Math.Max(0, seconds)));
            if (analysis.Sections.LastOrDefault(s => s.Seconds <= seconds) is { } section)
                time = string.Format(CultureInfo.CurrentCulture, Strings.DifficultyAtSectionFormat, time, section.Name);
            return string.Join(
                Environment.NewLine,
                time,
                string.Format(CultureInfo.CurrentCulture, Strings.DifficultyAtFormat, moment.Overall, analysis.Profile.RankAt(ms)),
                string.Format(CultureInfo.CurrentCulture, Strings.DifficultyHardestFormat, LabelOf(analysis.Profile.DominantAt(ms))));
        }

        /// <summary>An attribute's name, as the graphs label it.</summary>
        public static string LabelOf(DifficultyAttribute attribute) => attribute switch
        {
            DifficultyAttribute.Strum => Strings.DifficultyAttributeStrum,
            DifficultyAttribute.Note => Strings.DifficultyAttributeNote,
            DifficultyAttribute.Rhythm => Strings.DifficultyAttributeRhythm,
            DifficultyAttribute.Pattern => Strings.DifficultyAttributePattern,
            _ => Strings.DifficultyAttributeEndurance,
        };
    }
}
