using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using ChartBuilder.Controls;
using ChartBuilder.Resources;
using ChartBuilder.Services;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using ChartShared.Core.Validation;
using ChartShared.UI.Controls.Highway;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace ChartBuilder.ViewModels
{
    /// <summary>A kind of change the Reduce dialog's preview can mark and stop at, ticked to: a filter, with how many there are.</summary>
    public sealed partial class ReduceChangeFilter(ReduceChangeCategories category, string name, string toolTip, IBrush swatch, bool isChecked) : ObservableObject
    {
        public ReduceChangeCategories Category { get; } = category;

        public string Name { get; } = name;

        /// <summary>What the kind of change is, and how the highways show it.</summary>
        public string ToolTip { get; } = toolTip;

        /// <summary>The colour of its marks on the change strip.</summary>
        public IBrush Swatch { get; } = swatch;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Label))]
        public partial int Count { get; set; }

        [ObservableProperty]
        public partial bool IsChecked { get; set; } = isChecked;

        /// <summary>Its name and how many there are, e.g. "Reshapes (12)".</summary>
        public string Label => string.Format(CultureInfo.CurrentCulture, Strings.ReduceFilterCountFormat, Name, Count);
    }

    /// <summary>
    /// The Reduce dialog's preview (charter-spec C9 item 2): the source and each draft as highways, as the games read them,
    /// played with the song; a strip marking where each draft changed and the draft's lint findings; and a way to go from
    /// change to change. Each change is shown as the filters ticked say: on the strip, as a stop, and on the highways, the
    /// notes a draft lost faded on its source and the chords it changed ringed.
    /// </summary>
    /// <remarks>
    /// The playback is the Editor's, lent for as long as the dialog is open (<see cref="ChartEditorViewModel.BorrowPlayback"/>),
    /// so playing here records no pass and playtests nothing. The diff of each draft (<see cref="ReductionDiff"/>) is worked
    /// out off the UI thread after each redraw while the preview is shown, a newer one replacing it.
    /// </remarks>
    public sealed partial class ReducePreviewViewModel : ViewModelBase, IDisposable
    {
        // How far above the strike line a change gone to is, as a share of the look-ahead, so it can be seen coming.
        private const double LeadShare = 0.3;

        // How near a stop the playhead counts as on it, in seconds, so going to the next doesn't stick on it.
        private const double StopTolerance = 0.005;

        private static readonly ReduceChangeCategories[] Categories =
            [ReduceChangeCategories.Reshapes, ReduceChangeCategories.Forcing, ReduceChangeCategories.Sustains, ReduceChangeCategories.Removals, ReduceChangeCategories.Findings];

        private readonly ChartEditorViewModel _editor;
        private readonly BuilderSettingsService _settings;
        private readonly IDisposable _borrow;

        // The run last drafted, its diffs and findings, and what they were worked out with.
        private ReduceProposals? _reduce;
        private IReadOnlyList<ReductionDiff> _diffs = [];
        private IReadOnlyList<Finding> _findings = [];
        private ChartInstrument _part;
        private (long From, long To)? _range;
        private TempoMap? _tempo;
        private TimeSpan _delay;
        private bool _fiveLane;

        // Counts the diffs started, so one that finishes after a later one began is let go of; and whether the run changed
        // while the preview was hidden, to be worked out when it's shown.
        private int _diffing;
        private bool _isStale;
        private bool _disposed;

        // Where each stop is, in seconds of audio, and what it says, in order; and the one last gone to, or -1.
        private double[] _stops = [];
        private string[] _stopTexts = [];
        private int _stop = -1;

        public ReducePreviewViewModel(ChartEditorViewModel editor, BuilderSettingsService settings)
        {
            _editor = editor;
            _settings = settings;
            _borrow = editor.BorrowPlayback();
            LookAhead = BuilderSettings.SnapReducePreviewLookAhead(settings.Settings.ReducePreviewLookAhead);
            IsExpanded = settings.Settings.ReducePreviewShown;

            var shown = settings.Settings.ReducePreviewCategories;
            Filters = [.. Categories.Select(c => new ReduceChangeFilter(c, NameOf(c), ToolTipOf(c), SwatchOf(c), shown.HasFlag(c)))];
            foreach (var filter in Filters)
                filter.PropertyChanged += OnFilterChanged;
        }

        private EditorSession Session => _editor.Session;

        public PlaybackViewModel Playback => _editor.Playback;

        /// <summary>The time to draw at: the Editor's clock, allowing for the latency and the speed.</summary>
        public Func<TimeSpan> Clock => Playback.Clock;

        /// <summary>Whether the wheel turned up moves the playhead later, as Settings says.</summary>
        public bool WheelUpIsLater => _editor.WheelUpIsLater;

        /// <summary>Whether the preview is shown; remembered for the next run.</summary>
        [ObservableProperty]
        public partial bool IsExpanded { get; set; }

        /// <summary>
        /// How many seconds ahead of the strike line the highways show, in steps of <see cref="LookAheadStep"/> from
        /// <see cref="MinLookAhead"/> to <see cref="MaxLookAhead"/>; remembered for the next run.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Lead), nameof(LookAheadText))]
        public partial double LookAhead { get; set; }

        public const double MinLookAhead = BuilderSettings.MinReducePreviewLookAhead;

        public const double MaxLookAhead = BuilderSettings.MaxReducePreviewLookAhead;

        public const double LookAheadStep = BuilderSettings.ReducePreviewLookAheadStep;

        /// <summary>The look-ahead in seconds, as the slider's readout shows it, e.g. "1.5 s".</summary>
        public string LookAheadText => string.Format(CultureInfo.CurrentCulture, Strings.ReducePreviewLookAheadFormat, LookAhead);

        /// <summary>How far before a change going to it puts the playhead, in seconds, so it's seen coming down the highway.</summary>
        public double Lead => LookAhead * LeadShare;

        /// <summary>The source and the drafts, highest difficulty first, or null when there's nothing to show.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasScene))]
        public partial HighwayScene? Scene { get; private set; }

        public bool HasScene => Scene is not null;

        /// <summary>Why there's nothing to show, or that it's being worked out; empty with a scene.</summary>
        [ObservableProperty]
        public partial string EmptyText { get; private set; } = "";

        /// <summary>The kinds of change, each ticked to be marked and stopped at.</summary>
        public IReadOnlyList<ReduceChangeFilter> Filters { get; }

        /// <summary>The change strip's marks: each draft's changes on its row, then the findings on the last.</summary>
        [ObservableProperty]
        public partial IReadOnlyList<ChangeStripMark> Marks { get; private set; } = [];

        /// <summary>The change strip's rows: each draft, then the findings.</summary>
        [ObservableProperty]
        public partial IReadOnlyList<string> RowLabels { get; private set; } = [];

        /// <summary>Where the change strip starts, in seconds of audio: a bar before the run's range, or the song's start.</summary>
        [ObservableProperty]
        public partial double StripStart { get; private set; }

        /// <summary>Where the change strip ends, in seconds of audio: a bar after the run's range, or the song's end.</summary>
        [ObservableProperty]
        public partial double StripEnd { get; private set; } = 1;

        /// <summary>Where the stop last gone to is, in seconds of audio; NaN for none.</summary>
        [ObservableProperty]
        public partial double Current { get; private set; } = double.NaN;

        /// <summary>Which stop was last gone to of how many, e.g. "3 / 42".</summary>
        [ObservableProperty]
        public partial string StopText { get; private set; } = "";

        /// <summary>What changed at the stop last gone to, and where; empty for none.</summary>
        [ObservableProperty]
        public partial string CurrentText { get; private set; } = "";

        /// <summary>Plays from where the song is, or from a bar before the run's range when it's outside it; or pauses.</summary>
        [RelayCommand]
        private void PlayPause()
        {
            if (!Playback.IsPlaying)
                SeekIntoRange();
            Playback.PlayPauseCommand.Execute(null);
        }

        /// <summary>Goes to the next change, or finding, of those ticked.</summary>
        [RelayCommand]
        private void NextChange() => GoToStop(StopAfter(_stops, Focus()));

        /// <summary>Goes to the change, or finding, before.</summary>
        [RelayCommand]
        private void PreviousChange() => GoToStop(StopBefore(_stops, Focus()));

        /// <summary>Moves the song to <paramref name="seconds"/>, as a highway is dragged.</summary>
        public void SeekTo(double seconds) => Playback.SeekTo(Math.Max(0, seconds));

        /// <summary>Shows the preview, with the song just before <paramref name="tick"/>, as a finding is clicked.</summary>
        public void GoTo(long tick)
        {
            IsExpanded = true;
            double seconds = Session.AudioAt(tick);
            int stop = Array.FindIndex(_stops, s => Math.Abs(s - seconds) <= StopTolerance);
            if (stop >= 0)
            {
                GoToStop(stop);
                return;
            }

            SeekTo(seconds - Lead);
        }

        /// <summary>The run was drafted again: its diff is worked out, now or when the preview is next shown.</summary>
        internal void OnDrafted(ReduceProposals reduce)
        {
            _reduce = reduce;
            _findings = [];
            if (!IsExpanded)
            {
                _isStale = true;
                return;
            }

            _ = DiffAsync();
        }

        /// <summary>The draft's lint findings came in, to be marked and stopped at.</summary>
        internal void OnFindings(IReadOnlyList<Finding> findings)
        {
            _findings = findings;
            if (_diffs.Count > 0)
                RefreshMarks();
        }

        /// <summary>There's no draft, for <paramref name="reason"/>: nothing is shown.</summary>
        internal void Clear(string reason)
        {
            _diffing++;
            _reduce = null;
            _isStale = false;
            _diffs = [];
            _findings = [];
            Scene = null;
            EmptyText = reason;
            RefreshMarks();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _diffing++;
            foreach (var filter in Filters)
                filter.PropertyChanged -= OnFilterChanged;
            _borrow.Dispose();
        }

        /// <summary>The index of the first of <paramref name="stops"/>, which are in order, after <paramref name="at"/>; -1 for none.</summary>
        internal static int StopAfter(IReadOnlyList<double> stops, double at)
        {
            for (int i = 0; i < stops.Count; i++)
            {
                if (stops[i] > at + StopTolerance)
                    return i;
            }

            return -1;
        }

        /// <summary>The index of the last of <paramref name="stops"/>, which are in order, before <paramref name="at"/>; -1 for none.</summary>
        internal static int StopBefore(IReadOnlyList<double> stops, double at)
        {
            for (int i = stops.Count - 1; i >= 0; i--)
            {
                if (stops[i] < at - StopTolerance)
                    return i;
            }

            return -1;
        }

        /// <summary>The kinds of change the ticked <paramref name="shown"/> stand for.</summary>
        internal static ReductionChanges MaskOf(ReduceChangeCategories shown)
        {
            var mask = ReductionChanges.None;
            if (shown.HasFlag(ReduceChangeCategories.Removals))
                mask |= ReductionChanges.Removed | ReductionChanges.DoubleKickDropped;
            if (shown.HasFlag(ReduceChangeCategories.Reshapes))
                mask |= ReductionChanges.Reshaped | ReductionChanges.Added;
            if (shown.HasFlag(ReduceChangeCategories.Sustains))
                mask |= ReductionChanges.SustainTrimmed;
            if (shown.HasFlag(ReduceChangeCategories.Forcing))
                mask |= ReductionChanges.HopoFlipped | ReductionChanges.ForcingChanged;
            return mask;
        }

        /// <summary>
        /// How each of a highway's <paramref name="count"/> notes is marked: faded where it's a draft's source and the draft
        /// lost it, ringed where it's a draft and its chord changed, as far as <paramref name="mask"/> shows; null when none is.
        /// </summary>
        internal static HighwayNoteMark[]? LaneMarks(int count, IReadOnlyList<ReductionChanges>? asSource, IReadOnlyList<ReductionChanges>? asDraft, ReductionChanges mask)
        {
            HighwayNoteMark[]? marks = null;
            for (int i = 0; i < count; i++)
            {
                var mark = HighwayNoteMark.None;
                if (asSource is not null && (asSource[i] & mask) != 0)
                    mark |= HighwayNoteMark.Faded;
                if (asDraft is not null && (asDraft[i] & mask) != 0)
                    mark |= HighwayNoteMark.Flagged;
                if (mark != HighwayNoteMark.None)
                    (marks ??= new HighwayNoteMark[count])[i] = mark;
            }

            return marks;
        }

        /// <summary>The kind of mark a change shown as <paramref name="kinds"/> gets: the one that matters most.</summary>
        internal static ChangeStripKind StripKindOf(ReductionChanges kinds) =>
            (kinds & (ReductionChanges.Reshaped | ReductionChanges.Added)) != 0 ? ChangeStripKind.Reshape
            : (kinds & (ReductionChanges.HopoFlipped | ReductionChanges.ForcingChanged)) != 0 ? ChangeStripKind.Forcing
            : (kinds & ReductionChanges.SustainTrimmed) != 0 ? ChangeStripKind.Sustain
            : ChangeStripKind.Removal;

        partial void OnIsExpandedChanged(bool value)
        {
            _settings.Settings.ReducePreviewShown = value;
            if (!value)
                return;
            if (_isStale)
                _ = DiffAsync();
            else
                SeekIntoRange();
        }

        partial void OnLookAheadChanged(double value)
        {
            double snapped = BuilderSettings.SnapReducePreviewLookAhead(value);
            if (snapped != value)
            {
                LookAhead = snapped;
                return;
            }

            _settings.Settings.ReducePreviewLookAhead = snapped;
        }

        private void OnFilterChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ReduceChangeFilter.IsChecked))
                return;
            _settings.Settings.ReducePreviewCategories = Shown;
            RefreshScene();
            RefreshMarks();
        }

        private ReduceChangeCategories Shown => Filters.Where(static f => f.IsChecked).Aggregate(ReduceChangeCategories.None, static (all, f) => all | f.Category);

        // How each draft differs from its source, worked out off the UI thread from what the run is now.
        private async Task DiffAsync()
        {
            _isStale = false;
            if (_reduce is not { Drafted: { } drafted, Part: { } part } reduce)
                return;

            int run = ++_diffing;
            var steps = reduce.Steps;
            var range = reduce.Range;
            var options = Session.ReadOptions;
            var before = range is null ? null : steps.ToDictionary(s => s.Target, s => Session.GameTrack(new ChartTrackKey(part, s.Target)));
            if (Scene is null)
                EmptyText = Strings.ReducePreviewWorking;

            IReadOnlyList<ReductionDiff> diffs;
            try
            {
                diffs = await Task.Run(() => ReductionDiff.Of(drafted, part, steps, range, options, before));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The preview is a help, not the draft: a diff that fails leaves the draft to add as it is.
                Log.ForContext<ReducePreviewViewModel>().Error(ex, "Comparing the reduced draft failed");
                if (run == _diffing && !_disposed)
                    Clear(Strings.ReducePreviewFailed);
                return;
            }

            if (run != _diffing || _disposed)
                return;

            bool moved = range != _range || _tempo is null;
            _diffs = diffs;
            _part = part;
            _range = range;
            _tempo = Session.TempoMap;
            _delay = Session.Delay;
            _fiveLane = Session.IsFiveLaneDrums;
            EmptyText = "";
            RefreshScene();
            RefreshMarks();
            if (moved)
                SeekIntoRange();
        }

        // The highways: each difficulty of the run, highest first, as a draft when it's one, else as charted.
        private void RefreshScene()
        {
            if (_diffs.Count == 0 || _tempo is not { } tempo)
            {
                Scene = null;
                return;
            }

            var mask = MaskOf(Shown);
            var kind = ChartInstruments.KindOf(_part);
            int laneCount = HighwayLane.LaneCountOf(kind, _fiveLane);
            var lanes = new List<HighwayLane>();
            foreach (var difficulty in _diffs.SelectMany(static d => new[] { d.Source, d.Target }).Distinct().OrderDescending())
            {
                var asSource = _diffs.FirstOrDefault(d => d.Source == difficulty);
                var asDraft = _diffs.FirstOrDefault(d => d.Target == difficulty);
                var track = asDraft?.DraftTrack ?? asSource!.SourceTrack;
                string name = ChartLabels.DifficultyName(difficulty);
                string label = asDraft is null ? name : string.Format(CultureInfo.CurrentCulture, Strings.ReducePreviewDraftLaneFormat, name);
                lanes.Add(new HighwayLane(kind, laneCount, label, track.Notes, tempo, _delay, LaneMarks(track.Notes.Count, asSource?.SourceMarks, asDraft?.DraftMarks, mask)));
            }

            Scene = new HighwayScene(lanes, tempo, _delay);
        }

        // The strip's marks and the stops, as the filters say, and each filter's count.
        private void RefreshMarks()
        {
            var shown = Shown;
            var mask = MaskOf(shown);
            foreach (var filter in Filters)
            {
                filter.Count = filter.Category == ReduceChangeCategories.Findings
                    ? _findings.Count
                    : _diffs.Sum(d => d.CountOf(MaskOf(filter.Category)));
            }

            var marks = new List<ChangeStripMark>();
            var stops = new List<(double Seconds, string Text)>();
            if (_tempo is { } tempo && _diffs.Count > 0)
            {
                double delay = _delay.TotalSeconds;
                for (int row = 0; row < _diffs.Count; row++)
                {
                    var diff = _diffs[row];
                    string difficulty = ChartLabels.DifficultyName(diff.Target);
                    foreach (var change in diff.Changes)
                    {
                        var kinds = change.Kinds & mask;
                        if (kinds == ReductionChanges.None)
                            continue;
                        double seconds = tempo.SecondsAt(change.Tick) + delay;
                        marks.Add(new ChangeStripMark(seconds, row, StripKindOf(kinds)));
                        stops.Add((seconds, Describe(tempo, change.Tick, difficulty, ChangeText(kinds, change.IsBoundary))));
                    }
                }

                if (shown.HasFlag(ReduceChangeCategories.Findings))
                {
                    foreach (var finding in _findings)
                    {
                        if (finding.Location is not { } at)
                            continue;
                        double seconds = tempo.SecondsAt(at.Tick) + delay;
                        marks.Add(new ChangeStripMark(seconds, _diffs.Count, finding.Severity switch
                        {
                            Severity.Error => ChangeStripKind.FindingError,
                            Severity.Warning => ChangeStripKind.FindingWarning,
                            _ => ChangeStripKind.FindingInfo,
                        }));
                        string difficulty = at.Difficulty is { } d ? ChartLabels.DifficultyName(d) : "";
                        stops.Add((seconds, Describe(tempo, at.Tick, difficulty, $"{finding.RuleId} {finding.Message}")));
                    }
                }
            }

            // Changes at one time are one stop, each said.
            var merged = stops.OrderBy(static s => s.Seconds)
                .GroupBy(static s => Math.Round(s.Seconds / StopTolerance))
                .Select(static g => (g.First().Seconds, Text: string.Join(Environment.NewLine, g.Select(static s => s.Text))))
                .ToList();
            double? current = _stop >= 0 && _stop < _stops.Length ? _stops[_stop] : null;
            _stops = [.. merged.Select(static s => s.Seconds)];
            _stopTexts = [.. merged.Select(static s => s.Text)];
            _stop = current is { } was ? Array.FindIndex(_stops, s => Math.Abs(s - was) <= StopTolerance) : -1;

            Marks = marks;
            RowLabels = _diffs.Count == 0 ? [] : [.. _diffs.Select(static d => ChartLabels.DifficultyName(d.Target)), Strings.ReduceFilterFindings];
            (StripStart, StripEnd) = Window();
            RefreshStop();
        }

        private void GoToStop(int stop)
        {
            if (stop < 0 || stop >= _stops.Length)
                return;
            _stop = stop;
            SeekTo(_stops[stop] - Lead);
            RefreshStop();
        }

        private void RefreshStop()
        {
            bool has = _stop >= 0;
            Current = has ? _stops[_stop] : double.NaN;
            CurrentText = has ? _stopTexts[_stop] : "";
            StopText = _stops.Length == 0
                ? Strings.ReducePreviewNoStops
                : string.Format(CultureInfo.CurrentCulture, Strings.ReducePreviewStopFormat, has ? (_stop + 1).ToString(CultureInfo.CurrentCulture) : "–", _stops.Length);
        }

        // Where the strike line is now, less the lead, so the change gone to last counts as where the playhead is.
        private double Focus() => (Playback.IsPlaying ? Clock().TotalSeconds : Playback.PositionSeconds) + Lead;

        // The strip's span: a bar either side of the run's range, or the whole song.
        private (double Start, double End) Window()
        {
            double length = Math.Max(1, Playback.LengthSeconds);
            if (_range is not { } range || _tempo is not { } tempo)
                return (0, length);
            double from = tempo.SecondsAt(range.From) + _delay.TotalSeconds;
            double to = tempo.SecondsAt(range.To) + _delay.TotalSeconds;
            double start = TimelineView.StepBars(Playback.Beats, from, -1) ?? Math.Max(0, from - 2);
            double end = TimelineView.StepBars(Playback.Beats, to, 1) ?? to + 2;
            return (Math.Max(0, start), Math.Max(end, start + 1));
        }

        // A run over a range plays from a bar before it when the song is outside it.
        private void SeekIntoRange()
        {
            if (_range is null || Playback.IsPlaying)
                return;
            double at = Playback.PositionSeconds;
            if (at < StripStart || at > StripEnd)
                SeekTo(StripStart);
        }

        private static string Describe(TempoMap tempo, long tick, string difficulty, string what) =>
            string.Format(CultureInfo.CurrentCulture, Strings.ReducePreviewChangeFormat, ChartLabels.PlaceText(MeasureBeat.At(tempo, tick)), difficulty, what);

        private static string ChangeText(ReductionChanges kinds, bool isBoundary)
        {
            var parts = new List<string>();
            if (kinds.HasFlag(ReductionChanges.Removed))
                parts.Add(Strings.ReduceChangeRemoved);
            if (kinds.HasFlag(ReductionChanges.DoubleKickDropped))
                parts.Add(Strings.ReduceChangeDoubleKick);
            if (kinds.HasFlag(ReductionChanges.Reshaped))
                parts.Add(Strings.ReduceChangeReshaped);
            if (kinds.HasFlag(ReductionChanges.Added))
                parts.Add(Strings.ReduceChangeAdded);
            if (kinds.HasFlag(ReductionChanges.SustainTrimmed))
                parts.Add(Strings.ReduceChangeSustain);
            if (kinds.HasFlag(ReductionChanges.HopoFlipped))
                parts.Add(Strings.ReduceChangeHopoFlip);
            if (kinds.HasFlag(ReductionChanges.ForcingChanged))
                parts.Add(Strings.ReduceChangeForcing);
            string text = string.Join(", ", parts);
            return isBoundary ? string.Format(CultureInfo.CurrentCulture, Strings.ReduceChangeBoundaryFormat, text) : text;
        }

        private static string NameOf(ReduceChangeCategories category) => category switch
        {
            ReduceChangeCategories.Removals => Strings.ReduceFilterRemovals,
            ReduceChangeCategories.Reshapes => Strings.ReduceFilterReshapes,
            ReduceChangeCategories.Sustains => Strings.ReduceFilterSustains,
            ReduceChangeCategories.Forcing => Strings.ReduceFilterForcing,
            _ => Strings.ReduceFilterFindings,
        };

        private static string ToolTipOf(ReduceChangeCategories category) => category switch
        {
            ReduceChangeCategories.Removals => Strings.ReduceFilterRemovalsToolTip,
            ReduceChangeCategories.Reshapes => Strings.ReduceFilterReshapesToolTip,
            ReduceChangeCategories.Sustains => Strings.ReduceFilterSustainsToolTip,
            ReduceChangeCategories.Forcing => Strings.ReduceFilterForcingToolTip,
            _ => Strings.ReduceFilterFindingsToolTip,
        };

        private static IBrush SwatchOf(ReduceChangeCategories category) => category switch
        {
            ReduceChangeCategories.Removals => ChangeStripPalette.Removal,
            ReduceChangeCategories.Reshapes => ChangeStripPalette.Reshape,
            ReduceChangeCategories.Sustains => ChangeStripPalette.Sustain,
            ReduceChangeCategories.Forcing => ChangeStripPalette.Forcing,
            _ => ChangeStripPalette.FindingWarning,
        };
    }
}
