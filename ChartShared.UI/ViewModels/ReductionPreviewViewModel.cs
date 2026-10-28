using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using ChartShared.Core.Validation;
using ChartShared.UI.Controls;
using ChartShared.UI.Controls.Highway;
using ChartShared.UI.Resources;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace ChartShared.UI.ViewModels
{
    /// <summary>The kinds of change a reduction's preview can mark and stop at, each a filter it shows.</summary>
    [Flags]
    public enum ReductionChangeCategories
    {
        None = 0,

        /// <summary>Chords left out, and 2x kicks dropped.</summary>
        Removals = 1,

        /// <summary>Chords with other frets or pads, and chords added.</summary>
        Reshapes = 2,

        /// <summary>Notes held shorter, or no longer held.</summary>
        Sustains = 4,

        /// <summary>Chords read as a strum, HOPO or tap other than before, forced or natural.</summary>
        Forcing = 8,

        /// <summary>The draft's lint findings.</summary>
        Findings = 16,

        All = Removals | Reshapes | Sustains | Forcing | Findings,
        Default = All & ~Removals,
    }

    /// <summary>
    /// The song a reduction's preview plays (<see cref="ReductionPreviewViewModel"/>): Chart Builder's Editor's playback,
    /// lent to its Reduce dialog, or Chart Packer's player in its Generate difficulties dialog.
    /// </summary>
    public interface IReductionPreviewPlayback : INotifyPropertyChanged
    {
        /// <summary>Whether the song can be played.</summary>
        bool IsReady { get; }

        bool IsPlaying { get; }

        /// <summary>Where the song is, in seconds of audio; set, as the change strip is scrubbed, it moves the song there.</summary>
        double PositionSeconds { get; set; }

        /// <summary>How long the song is, in seconds of audio.</summary>
        double LengthSeconds { get; }

        /// <summary>Where the song is and how long it is, e.g. "0:42.0 / 3:00.0".</summary>
        string PositionText { get; }

        /// <summary>The chart's beat lines in audio time, which the change strip draws and steps along.</summary>
        IReadOnlyList<TimelineBeat> Beats { get; }

        /// <summary>The time to draw at, moving smoothly while it plays, allowing for the latency and the speed.</summary>
        Func<TimeSpan> Clock { get; }

        /// <summary>The speeds it plays at, as percents.</summary>
        IReadOnlyList<int> SpeedPercents { get; }

        int SpeedPercent { get; set; }

        /// <summary>Plays from where the song is, or pauses.</summary>
        void PlayPause();

        /// <summary>Moves the song to <paramref name="seconds"/>.</summary>
        void SeekTo(double seconds);
    }

    /// <summary>What a reduction's preview remembers from one run to the next: whether it's shown, its look-ahead, and its filters.</summary>
    public interface IReductionPreviewSettings
    {
        bool IsShown { get; set; }

        double LookAhead { get; set; }

        ReductionChangeCategories Categories { get; set; }
    }

    /// <summary>A preview's settings kept for as long as the app runs, for an app that doesn't keep them in its settings file.</summary>
    public sealed class ReductionPreviewMemory : IReductionPreviewSettings
    {
        public bool IsShown { get; set; } = true;

        public double LookAhead { get; set; } = ReductionPreviewViewModel.DefaultLookAhead;

        public ReductionChangeCategories Categories { get; set; } = ReductionChangeCategories.Default;
    }

    /// <summary>
    /// A reduction for the preview to show (<see cref="ReductionPreviewViewModel.OnDrafted"/>): the part, the ticks it covers
    /// or null for all of it, the chart's tempo map and audio delay, whether its drums are 5-lane, and how to work out how
    /// each draft differs from its source (<see cref="ReductionDiff"/>), off the calling thread.
    /// </summary>
    public sealed record ReductionPreviewRun(
        ChartInstrument Part,
        (long From, long To)? Range,
        TempoMap Tempo,
        TimeSpan Delay,
        bool FiveLaneDrums,
        Func<Task<IReadOnlyList<ReductionDiff>>> DiffAsync);

    /// <summary>A kind of change a reduction's preview can mark and stop at, ticked to: a filter, with how many there are.</summary>
    public sealed partial class ReductionChangeFilter(ReductionChangeCategories category, string name, string toolTip, IBrush swatch, bool isChecked) : ObservableObject
    {
        public ReductionChangeCategories Category { get; } = category;

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
        public string Label => string.Format(CultureInfo.CurrentCulture, SharedStrings.ReductionPreviewFilterCountFormat, Name, Count);
    }

    /// <summary>
    /// A reduction's preview (charter-spec C9 item 2): the source and each draft as highways, as the games read them, played
    /// with the song; a strip marking where each draft changed and the drafts' lint findings; and a way to go from change
    /// to change. Each change is shown as the filters ticked say: on the strip, as a stop, and on the highways, the notes a
    /// draft lost faded on its source and the chords it changed ringed. Chart Builder's Reduce dialog and Chart Packer's
    /// Generate difficulties dialog show it (<c>ReductionPreviewView</c>).
    /// </summary>
    /// <remarks>
    /// The diff of each draft (<see cref="ReductionDiff"/>) is worked out off the UI thread after each draft while the
    /// preview is shown, a newer one replacing it.
    /// </remarks>
    public sealed partial class ReductionPreviewViewModel : ViewModelBase, IDisposable
    {
        /// <summary>The look-ahead a preview has before one has been chosen, in seconds.</summary>
        public const double DefaultLookAhead = 1.5;

        // How far above the strike line a change gone to is, as a share of the look-ahead, so it can be seen coming.
        private const double LeadShare = 0.3;

        // How near a stop the playhead counts as on it, in seconds, so going to the next doesn't stick on it.
        private const double StopTolerance = 0.005;

        private static readonly ReductionChangeCategories[] Categories =
            [ReductionChangeCategories.Reshapes, ReductionChangeCategories.Forcing, ReductionChangeCategories.Sustains, ReductionChangeCategories.Removals, ReductionChangeCategories.Findings];

        private readonly IReductionPreviewSettings _settings;
        private readonly IDisposable? _lease;

        // The run last drafted; its diffs and findings, and what they were worked out with.
        private ReductionPreviewRun? _run;
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

        /// <param name="playback">The song it plays.</param>
        /// <param name="settings">What it remembers for the next run.</param>
        /// <param name="wheelUpIsLater">Whether the wheel turned up moves the playhead later.</param>
        /// <param name="lease">Let go of as it's disposed: the playback lent to it, if it was.</param>
        public ReductionPreviewViewModel(IReductionPreviewPlayback playback, IReductionPreviewSettings settings, bool wheelUpIsLater, IDisposable? lease = null)
        {
            ArgumentNullException.ThrowIfNull(playback);
            ArgumentNullException.ThrowIfNull(settings);
            Playback = playback;
            _settings = settings;
            _lease = lease;
            WheelUpIsLater = wheelUpIsLater;
            LookAhead = HighwayLookAhead.Clamp(settings.LookAhead, DefaultLookAhead);
            IsExpanded = settings.IsShown;

            var shown = settings.Categories;
            Filters = [.. Categories.Select(c => new ReductionChangeFilter(c, NameOf(c), ToolTipOf(c), SwatchOf(c), shown.HasFlag(c)))];
            foreach (var filter in Filters)
                filter.PropertyChanged += OnFilterChanged;
        }

        public IReductionPreviewPlayback Playback { get; }

        /// <summary>The time to draw at: the playback's clock, allowing for the latency and the speed.</summary>
        public Func<TimeSpan> Clock => Playback.Clock;

        /// <summary>Whether the wheel turned up moves the playhead later.</summary>
        public bool WheelUpIsLater { get; }

        /// <summary>Whether the preview is shown; remembered for the next run.</summary>
        [ObservableProperty]
        public partial bool IsExpanded { get; set; }

        /// <summary>How many seconds ahead of the strike line the highways show (<see cref="HighwayLookAhead"/>); remembered for the next run.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Lead), nameof(LookAheadText))]
        public partial double LookAhead { get; set; }

        /// <summary>The look-ahead in seconds, as the slider's readout shows it, e.g. "1.5 s".</summary>
        public string LookAheadText => string.Format(CultureInfo.CurrentCulture, SharedStrings.LookAheadFormat, LookAhead);

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
        public IReadOnlyList<ReductionChangeFilter> Filters { get; }

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
            Playback.PlayPause();
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
            if ((_tempo ?? _run?.Tempo) is not { } tempo)
                return;
            IsExpanded = true;
            double seconds = tempo.SecondsAt(tick) + (_tempo is null ? _run!.Delay : _delay).TotalSeconds;
            int stop = Array.FindIndex(_stops, s => Math.Abs(s - seconds) <= StopTolerance);
            if (stop >= 0)
            {
                GoToStop(stop);
                return;
            }

            SeekTo(seconds - Lead);
        }

        /// <summary>The run was drafted again: its diff is worked out, now or when the preview is next shown.</summary>
        public void OnDrafted(ReductionPreviewRun run)
        {
            ArgumentNullException.ThrowIfNull(run);
            _run = run;
            _findings = [];
            if (!IsExpanded)
            {
                _isStale = true;
                return;
            }

            _ = DiffAsync();
        }

        /// <summary>The drafts' lint findings came in, to be marked and stopped at.</summary>
        public void OnFindings(IReadOnlyList<Finding> findings)
        {
            ArgumentNullException.ThrowIfNull(findings);
            _findings = findings;
            if (_diffs.Count > 0)
                RefreshMarks();
        }

        /// <summary>There's no draft, for <paramref name="reason"/>: nothing is shown.</summary>
        public void Clear(string reason)
        {
            _diffing++;
            _run = null;
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
            _lease?.Dispose();
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
        internal static ReductionChanges MaskOf(ReductionChangeCategories shown)
        {
            var mask = ReductionChanges.None;
            if (shown.HasFlag(ReductionChangeCategories.Removals))
                mask |= ReductionChanges.Removed | ReductionChanges.DoubleKickDropped;
            if (shown.HasFlag(ReductionChangeCategories.Reshapes))
                mask |= ReductionChanges.Reshaped | ReductionChanges.Added;
            if (shown.HasFlag(ReductionChangeCategories.Sustains))
                mask |= ReductionChanges.SustainTrimmed;
            if (shown.HasFlag(ReductionChangeCategories.Forcing))
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
            _settings.IsShown = value;
            if (!value)
                return;
            if (_isStale)
                _ = DiffAsync();
            else
                SeekIntoRange();
        }

        // Already snapped by whatever sets it: the slider and the constructor.
        partial void OnLookAheadChanged(double value) => _settings.LookAhead = value;

        private void OnFilterChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ReductionChangeFilter.IsChecked))
                return;
            _settings.Categories = Shown;
            RefreshScene();
            RefreshMarks();
        }

        private ReductionChangeCategories Shown => Filters.Where(static f => f.IsChecked).Aggregate(ReductionChangeCategories.None, static (all, f) => all | f.Category);

        // How each draft differs from its source, worked out off the UI thread from the run as it is now.
        private async Task DiffAsync()
        {
            _isStale = false;
            if (_run is not { } run)
                return;

            int diffing = ++_diffing;
            if (Scene is null)
                EmptyText = SharedStrings.ReductionPreviewWorking;

            IReadOnlyList<ReductionDiff> diffs;
            try
            {
                diffs = await run.DiffAsync();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The preview is a help, not the draft: a diff that fails leaves the draft to add as it is.
                Log.ForContext<ReductionPreviewViewModel>().Error(ex, "Comparing the reduced draft failed");
                if (diffing == _diffing && !_disposed)
                    Clear(SharedStrings.ReductionPreviewFailed);
                return;
            }

            if (diffing != _diffing || _disposed)
                return;

            bool moved = run.Range != _range || _tempo is null;
            _diffs = diffs;
            _part = run.Part;
            _range = run.Range;
            _tempo = run.Tempo;
            _delay = run.Delay;
            _fiveLane = run.FiveLaneDrums;
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
                string label = asDraft is null ? name : string.Format(CultureInfo.CurrentCulture, SharedStrings.ReductionPreviewDraftLaneFormat, name);
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
                filter.Count = filter.Category == ReductionChangeCategories.Findings
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

                if (shown.HasFlag(ReductionChangeCategories.Findings))
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
            RowLabels = _diffs.Count == 0 ? [] : [.. _diffs.Select(static d => ChartLabels.DifficultyName(d.Target)), SharedStrings.ReductionPreviewFilterFindings];
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
                ? SharedStrings.ReductionPreviewNoStops
                : string.Format(CultureInfo.CurrentCulture, SharedStrings.ReductionPreviewStopFormat, has ? (_stop + 1).ToString(CultureInfo.CurrentCulture) : "–", _stops.Length);
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
            double start = TimelineBeats.StepBars(Playback.Beats, from, -1) ?? Math.Max(0, from - 2);
            double end = TimelineBeats.StepBars(Playback.Beats, to, 1) ?? to + 2;
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
            string.Format(CultureInfo.CurrentCulture, SharedStrings.ReductionPreviewChangeFormat, ChartLabels.PlaceText(MeasureBeat.At(tempo, tick)), difficulty, what);

        private static string ChangeText(ReductionChanges kinds, bool isBoundary)
        {
            var parts = new List<string>();
            if (kinds.HasFlag(ReductionChanges.Removed))
                parts.Add(SharedStrings.ReductionPreviewChangeRemoved);
            if (kinds.HasFlag(ReductionChanges.DoubleKickDropped))
                parts.Add(SharedStrings.ReductionPreviewChangeDoubleKick);
            if (kinds.HasFlag(ReductionChanges.Reshaped))
                parts.Add(SharedStrings.ReductionPreviewChangeReshaped);
            if (kinds.HasFlag(ReductionChanges.Added))
                parts.Add(SharedStrings.ReductionPreviewChangeAdded);
            if (kinds.HasFlag(ReductionChanges.SustainTrimmed))
                parts.Add(SharedStrings.ReductionPreviewChangeSustain);
            if (kinds.HasFlag(ReductionChanges.HopoFlipped))
                parts.Add(SharedStrings.ReductionPreviewChangeHopoFlip);
            if (kinds.HasFlag(ReductionChanges.ForcingChanged))
                parts.Add(SharedStrings.ReductionPreviewChangeForcing);
            string text = string.Join(", ", parts);
            return isBoundary ? string.Format(CultureInfo.CurrentCulture, SharedStrings.ReductionPreviewChangeBoundaryFormat, text) : text;
        }

        private static string NameOf(ReductionChangeCategories category) => category switch
        {
            ReductionChangeCategories.Removals => SharedStrings.ReductionPreviewFilterRemovals,
            ReductionChangeCategories.Reshapes => SharedStrings.ReductionPreviewFilterReshapes,
            ReductionChangeCategories.Sustains => SharedStrings.ReductionPreviewFilterSustains,
            ReductionChangeCategories.Forcing => SharedStrings.ReductionPreviewFilterForcing,
            _ => SharedStrings.ReductionPreviewFilterFindings,
        };

        private static string ToolTipOf(ReductionChangeCategories category) => category switch
        {
            ReductionChangeCategories.Removals => SharedStrings.ReductionPreviewFilterRemovalsToolTip,
            ReductionChangeCategories.Reshapes => SharedStrings.ReductionPreviewFilterReshapesToolTip,
            ReductionChangeCategories.Sustains => SharedStrings.ReductionPreviewFilterSustainsToolTip,
            ReductionChangeCategories.Forcing => SharedStrings.ReductionPreviewFilterForcingToolTip,
            _ => SharedStrings.ReductionPreviewFilterFindingsToolTip,
        };

        private static IBrush SwatchOf(ReductionChangeCategories category) => category switch
        {
            ReductionChangeCategories.Removals => ChangeStripPalette.Removal,
            ReductionChangeCategories.Reshapes => ChangeStripPalette.Reshape,
            ReductionChangeCategories.Sustains => ChangeStripPalette.Sustain,
            ReductionChangeCategories.Forcing => ChangeStripPalette.Forcing,
            _ => ChangeStripPalette.FindingWarning,
        };
    }
}
