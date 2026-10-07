using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ChartBuilder.Resources;
using ChartPacker.Core.Charting.Editing;
using ChartPacker.Core.Charting.Layout;
using ChartPacker.Core.Charts;
using ChartPacker.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// One of the Editor's lanes: a part's highway at one difficulty, or a vocal part. Its header picks the part, the
    /// difficulty and a ghost difficulty, and closes the lane; its body is the part's editor canvas, as a highway or a
    /// timeline (charter-spec C3). Vocal parts are a placeholder so far.
    /// </summary>
    public sealed partial class EditorLaneViewModel : ViewModelBase
    {
        private readonly LaneDockViewModel _dock;

        // Until the lane is made, so the default it starts on isn't kept as its own look-ahead.
        private bool _isMade;

        internal EditorLaneViewModel(LaneDockViewModel dock, DockLane lane)
        {
            _dock = dock;
            Lane = lane;
            View = dock.Editor?.DefaultView ?? LaneView.Highway;
            LookAhead = EditorViewport.ClampLookAhead(lane.LookAhead ?? dock.Editor?.DefaultLookAhead ?? 2);
            Refresh();
            _isMade = true;
        }

        /// <summary>The lane in the dock's layout.</summary>
        public DockLane Lane { get; }

        /// <summary>The editor the lane's canvas edits; null for a lane without one.</summary>
        public ChartEditorViewModel? Editor => _dock.Editor;

        [ObservableProperty]
        public partial string Title { get; private set; } = "";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Placeholder), nameof(HasCanvas), nameof(HasPlaceholder), nameof(ShowsHighway), nameof(ShowsTimeline))]
        public partial bool IsVocal { get; private set; }

        /// <summary>The part and difficulty the lane shows; null for a vocal part.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Placeholder), nameof(HasCanvas), nameof(HasPlaceholder), nameof(ShowsHighway), nameof(ShowsTimeline))]
        public partial ChartTrackKey? Track { get; private set; }

        /// <summary>How the lane draws the song: a vertical highway or a horizontal timeline. <c>V</c> switches it.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsTimeline), nameof(ViewToolTip), nameof(ShowsHighway), nameof(ShowsTimeline))]
        public partial LaneView View { get; set; }

        public bool IsTimeline => View == LaneView.Timeline;

        public string ViewToolTip => IsTimeline ? Strings.LaneShowHighwayToolTip : Strings.LaneShowTimelineToolTip;

        /// <summary>
        /// How many seconds of the song the lane shows beyond its strike line: the further, the slower the notes go by.
        /// The header's control sets it, as do Ctrl+wheel, + and − over the lane. It's kept with the lanes' layout.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LookAheadText), nameof(LookAheadScale))]
        public partial double LookAhead { get; set; }

        /// <summary>
        /// The look-ahead for the header's slider: its base-2 logarithm, so a short one is set as finely as a long one,
        /// from <see cref="MinLookAheadScale"/> to <see cref="MaxLookAheadScale"/>.
        /// </summary>
        public double LookAheadScale
        {
            get => Math.Log2(LookAhead);
            set => LookAhead = Math.Pow(2, value);
        }

        public static double MinLookAheadScale => Math.Log2(EditorViewport.MinLookAhead);

        public static double MaxLookAheadScale => Math.Log2(EditorViewport.MaxLookAhead);

        /// <summary>The look-ahead as the header shows it, e.g. "2.0 s".</summary>
        public string LookAheadText => string.Format(CultureInfo.CurrentCulture, Strings.LaneLookAheadFormat, LookAhead);

        /// <summary>Looks further ahead by <paramref name="factor"/>, or nearer when it's less than 1.</summary>
        public void ZoomBy(double factor) => LookAhead *= factor;

        /// <summary>Another difficulty of the part, drawn faintly behind the lane's own; null for none.</summary>
        [ObservableProperty]
        public partial ChartDifficulty? Ghost { get; private set; }

        /// <summary>Whether the lane has a canvas: a part the chart has.</summary>
        public bool HasCanvas => Editor is not null && Track is { } track && Editor.Session.Chart.PartOf(track.Instrument) is not null;

        public bool HasPlaceholder => !HasCanvas;

        /// <summary>Whether the highway canvas is shown.</summary>
        public bool ShowsHighway => HasCanvas && !IsTimeline;

        /// <summary>Whether the timeline canvas is shown.</summary>
        public bool ShowsTimeline => HasCanvas && IsTimeline;

        /// <summary>Stands in for the lane's editor: the vocals, or a part the chart hasn't.</summary>
        public string Placeholder => IsVocal ? Strings.LaneVocalsPlaceholder
            : Track is { } track ? string.Format(CultureInfo.CurrentCulture, Strings.LaneNoPartFormat, ChartLabels.InstrumentName(track.Instrument))
            : Strings.LaneHighwayPlaceholder;

        /// <summary>The part's four difficulties, as the header's letters (charter-spec C3 item 3).</summary>
        [ObservableProperty]
        public partial IReadOnlyList<DifficultyLetter> Difficulties { get; private set; } = [];

        /// <summary>The difficulties a ghost can be, and none.</summary>
        [ObservableProperty]
        public partial IReadOnlyList<LaneMenuItem> GhostMenu { get; private set; } = [];

        /// <summary>The parts this lane can show instead: each part not shown in another lane.</summary>
        [ObservableProperty]
        public partial IReadOnlyList<LaneMenuItem> PartMenu { get; private set; } = [];

        /// <summary>The parts a new lane can show, as <see cref="LaneDockViewModel.AddMenu"/>.</summary>
        public IReadOnlyList<LaneMenuItem> AddMenu => _dock.AddMenu;

        [RelayCommand]
        private void Close() => _dock.Layout.Close(Lane);

        /// <summary>Switches the lane between highway and timeline (<c>V</c>).</summary>
        [RelayCommand]
        public void ToggleView() => View = IsTimeline ? LaneView.Highway : LaneView.Timeline;

        /// <summary>Shows <paramref name="difficulty"/> of the lane's part (the header's letters, Ctrl+1–4).</summary>
        public void ShowDifficulty(ChartDifficulty difficulty)
        {
            if (Track is not { } track || track.Difficulty == difficulty)
                return;
            if (!_dock.Layout.SetPart(Lane, LanePart.Highway(track.Instrument, difficulty)) && Editor is { } editor)
                editor.Messages.Info(string.Format(CultureInfo.CurrentCulture, Strings.LaneDifficultyShownFormat, ChartLabels.DifficultyName(difficulty)));
        }

        /// <summary>Takes in the lane's part, and what the other lanes show, after the layout changes.</summary>
        internal void Refresh()
        {
            var layout = _dock.Layout;
            Title = LaneMenuItem.TitleOf(Lane.Part);
            IsVocal = Lane.Part.IsVocal;
            Track = Lane.Part.Track;
            if (Ghost is { } ghost && Track is { } track && ghost == track.Difficulty)
                Ghost = null;
            PartMenu = LaneMenuItem.Build(
                canPick: p => p == Lane.Part || !layout.Contains(p),
                isCurrent: p => p == Lane.Part,
                pick: p => layout.SetPart(Lane, p));
            OnPropertyChanged(nameof(AddMenu));
            RefreshDifficulties();
        }

        /// <summary>Takes in which difficulties have notes, after an edit.</summary>
        internal void RefreshDifficulties()
        {
            OnPropertyChanged(nameof(HasCanvas));
            OnPropertyChanged(nameof(HasPlaceholder));
            OnPropertyChanged(nameof(ShowsHighway));
            OnPropertyChanged(nameof(ShowsTimeline));
            OnPropertyChanged(nameof(Placeholder));
            if (Track is not { } track)
            {
                Difficulties = [];
                GhostMenu = [];
                return;
            }

            var part = Editor?.Session.Chart.PartOf(track.Instrument);
            Difficulties = [.. Enum.GetValues<ChartDifficulty>().Select(d => new DifficultyLetter(
                d,
                ChartLabels.DifficultyName(d),
                part is not null && part[d].Notes.Count > 0,
                d == track.Difficulty,
                new RelayCommand(() => ShowDifficulty(d))))];
            GhostMenu =
            [
                LaneMenuItem.Choice(Strings.LaneGhostNone, Ghost is null, () => Ghost = null),
                .. Enum.GetValues<ChartDifficulty>().Where(d => d != track.Difficulty)
                    .Select(d => LaneMenuItem.Choice(ChartLabels.DifficultyName(d), Ghost == d, () => Ghost = d)),
            ];
        }

        partial void OnGhostChanged(ChartDifficulty? value) => RefreshDifficulties();

        partial void OnLookAheadChanged(double value)
        {
            double clamped = EditorViewport.ClampLookAhead(value);
            if (clamped != value)
            {
                LookAhead = clamped;
                return;
            }

            if (!_isMade)
                return;
            if (_dock.Layout.Find(Lane.Id) is not null)
                _dock.Layout.SetLookAhead(Lane, clamped);
            Editor?.RefreshVisibleSpan();
        }
    }

    /// <summary>One of a lane's difficulty letters: E, M, H or X, coloured when it has notes (charter-spec C3 item 3).</summary>
    public sealed record DifficultyLetter(ChartDifficulty Difficulty, string Name, bool HasNotes, bool IsCurrent, IRelayCommand Show)
    {
        /// <summary>The letter: E, M, H or X.</summary>
        public string Letter => Difficulty switch
        {
            ChartDifficulty.Easy => "E",
            ChartDifficulty.Medium => "M",
            ChartDifficulty.Hard => "H",
            _ => "X",
        };

        public bool IsEasy => Difficulty == ChartDifficulty.Easy;

        public bool IsMedium => Difficulty == ChartDifficulty.Medium;

        public bool IsHard => Difficulty == ChartDifficulty.Hard;

        public bool IsExpert => Difficulty == ChartDifficulty.Expert;
    }
}
