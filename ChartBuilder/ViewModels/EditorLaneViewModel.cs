using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ChartBuilder.Resources;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charting.Layout;
using ChartShared.Core.Charts;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// One of the Editor's lanes: a part's highway at one difficulty, or a vocal part. Its header picks the part, the
    /// difficulty and a ghost difficulty, and closes the lane; its body is the part's editor canvas, as a highway or a
    /// timeline (charter-spec C3). A vocal part is always drawn along a timeline (C3 item 7).
    /// </summary>
    public sealed partial class EditorLaneViewModel : ViewModelBase
    {
        private readonly LaneDockViewModel _dock;

        // Until the lane is made, so the default it starts on isn't kept as its own look-ahead.
        private bool _isMade;

        // What the header's letters and menus were made from last, so an edit that changes none of it doesn't make them
        // again: the track, the ghost, the part, which of its difficulties have notes, and a vocal part as read.
        private (ChartTrackKey? Track, ChartDifficulty? Ghost, EditablePart? Part, int WithNotes, VocalLane? Vocals)? _difficultiesOf;

        internal EditorLaneViewModel(LaneDockViewModel dock, DockLane lane)
        {
            _dock = dock;
            Lane = lane;
            View = dock.Editor?.DefaultView ?? LaneView.Highway;
            LookAhead = EditorViewport.ClampLookAhead(lane.LookAhead ?? dock.Editor?.DefaultLookAhead ?? 2);
            Audio = new LaneAudioViewModel(this, lane.Audio, options =>
            {
                if (_dock.Layout.Find(Lane.Id) is not null)
                    _dock.Layout.SetAudio(Lane, options);
            });
            Audio.Changed += (_, _) => OnPropertyChanged(nameof(Audio));
            Refresh();
            _isMade = true;
        }

        /// <summary>The lane in the dock's layout.</summary>
        public DockLane Lane { get; }

        /// <summary>The editor the lane's canvas edits; null for a lane without one.</summary>
        public ChartEditorViewModel? Editor => _dock.Editor;

        /// <summary>How the lane shows its audio: the header's audio button (charter-spec C12–C14).</summary>
        public LaneAudioViewModel Audio { get; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(AddVocalPartText))]
        public partial string Title { get; private set; } = "";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Placeholder), nameof(HasCanvas), nameof(HasPlaceholder), nameof(ShowsHighway), nameof(ShowsTimeline), nameof(CanAddVocalPart))]
        public partial bool IsVocal { get; private set; }

        /// <summary>The vocal part the lane shows; null for a highway.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Placeholder), nameof(HasCanvas), nameof(HasPlaceholder), nameof(ShowsHighway), nameof(ShowsTimeline), nameof(ShowsEditMenu), nameof(CanAddVocalPart), nameof(AddVocalPartText))]
        public partial VocalTrack? Vocal { get; private set; }

        /// <summary>The part and difficulty the lane shows; null for a vocal part.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Placeholder), nameof(HasCanvas), nameof(HasPlaceholder), nameof(ShowsHighway), nameof(ShowsTimeline), nameof(SelectedDifficultyIndex))]
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
        /// The look-ahead on a base-2 logarithmic scale, from <see cref="MinLookAheadScale"/> to
        /// <see cref="MaxLookAheadScale"/>. The header's slider binds <see cref="LookAhead"/> linearly instead.
        /// </summary>
        public double LookAheadScale
        {
            get => Math.Log2(LookAhead);
            set => LookAhead = Math.Pow(2, value);
        }

        public static double MinLookAheadScale => Math.Log2(EditorViewport.MinLookAhead);

        public static double MaxLookAheadScale => Math.Log2(EditorViewport.MaxLookAhead);

        /// <summary>The shortest look-ahead the header's slider offers, in seconds.</summary>
        public static double MinLookAhead => EditorViewport.MinLookAhead;

        /// <summary>The longest look-ahead the header's slider offers, in seconds.</summary>
        public static double MaxLookAhead => EditorViewport.MaxLookAhead;

        /// <summary>The look-ahead as the header shows it, e.g. "2.0 s".</summary>
        public string LookAheadText => string.Format(CultureInfo.CurrentCulture, Strings.LaneLookAheadFormat, LookAhead);

        /// <summary>Looks further ahead by <paramref name="factor"/>, or nearer when it's less than 1.</summary>
        public void ZoomBy(double factor) => LookAhead = EditorViewport.ClampLookAhead(LookAhead * factor);

        /// <summary>Looks further ahead by half a second per <paramref name="steps"/>, or nearer when negative.</summary>
        public void StepLookAhead(double steps) => LookAhead = EditorViewport.ClampLookAhead(LookAhead + steps * EditorViewport.LookAheadStep);

        /// <summary>Another difficulty of the part, drawn faintly behind the lane's own; null for none.</summary>
        [ObservableProperty]
        public partial ChartDifficulty? Ghost { get; private set; }

        /// <summary>Whether the lane has a canvas: a part, or a vocal track, the chart has.</summary>
        public bool HasCanvas => Editor is { } editor
            && (Track is { } track ? editor.Session.Chart.PartOf(track.Instrument) is not null
                : Vocal is { } vocal && editor.Session.VocalsOf(vocal) is not null);

        public bool HasPlaceholder => !HasCanvas;

        /// <summary>Whether the highway canvas is shown: never for a vocal part, which is drawn along a timeline.</summary>
        public bool ShowsHighway => HasCanvas && !IsTimeline && !IsVocal;

        /// <summary>Whether the timeline canvas is shown.</summary>
        public bool ShowsTimeline => HasCanvas && (IsTimeline || IsVocal);

        /// <summary>Whether the header offers the authoring tools, which edit a part's notes.</summary>
        public bool ShowsEditMenu => HasCanvas && !IsVocal;

        /// <summary>Whether the lane offers to add its vocal part, which the chart hasn't (charter-spec C3 item 7).</summary>
        public bool CanAddVocalPart => IsVocal && !HasCanvas && Editor is not null;

        /// <summary>What the button that adds the lane's vocal part says, e.g. "Add Harmony 2".</summary>
        public string AddVocalPartText => string.Format(CultureInfo.CurrentCulture, Strings.LaneAddVocalPartFormat, Title);

        /// <summary>Stands in for the lane's editor: a part or a vocal track the chart hasn't.</summary>
        public string Placeholder => IsVocal ? string.Format(CultureInfo.CurrentCulture, Strings.LaneNoVocalsFormat, Title)
            : Track is { } track ? string.Format(CultureInfo.CurrentCulture, Strings.LaneNoPartFormat, ChartLabels.InstrumentName(track.Instrument))
            : Strings.LaneHighwayPlaceholder;

        /// <summary>The part's four difficulties, as the header's letters (charter-spec C3 item 3).</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SelectedDifficultyIndex))]
        public partial IReadOnlyList<DifficultyLetter> Difficulties { get; private set; } = [];

        /// <summary>Index into <see cref="Difficulties"/> of the difficulty shown, as the header's drop-down picks it
        /// when the lane is too narrow for the letters.</summary>
        /// <remarks>Bound by index, rather than by item or value: the letters are remade after every edit, with new
        /// commands, so neither item equality nor a value re-match survives the list being replaced.</remarks>
        public int SelectedDifficultyIndex
        {
            get => Track is { } track ? (int)track.Difficulty : -1;
            set
            {
                if (value >= 0 && value < Difficulties.Count)
                    ShowDifficulty(Difficulties[value].Difficulty);
                OnPropertyChanged();
            }
        }

        /// <summary>The difficulties a ghost can be, and none.</summary>
        [ObservableProperty]
        public partial IReadOnlyList<LaneMenuItem> GhostMenu { get; private set; } = [];

        /// <summary>The lane's authoring tools: Copy difficulty and the rest (charter-spec C9, C15).</summary>
        [ObservableProperty]
        public partial IReadOnlyList<LaneMenuItem> EditMenu { get; private set; } = [];

        /// <summary>The parts this lane can show instead: each part not shown in another lane.</summary>
        [ObservableProperty]
        public partial IReadOnlyList<LaneMenuItem> PartMenu { get; private set; } = [];

        /// <summary>The parts a new lane can show, as <see cref="LaneDockViewModel.AddMenu"/>.</summary>
        public IReadOnlyList<LaneMenuItem> AddMenu => _dock.AddMenu;

        [RelayCommand]
        private void Close() => _dock.Layout.Close(Lane);

        /// <summary>Adds the lane's vocal part to the chart, empty, as one undoable step (charter-spec C3 item 7).</summary>
        [RelayCommand]
        private void AddVocalPart()
        {
            if (Editor is { } editor && Vocal is { } vocal)
                editor.Session.Execute(new AddVocalPart(vocal));
        }

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
            Vocal = Lane.Part.Vocal;
            Track = Lane.Part.Track;
            if (Ghost is { } ghost && Track is { } track && ghost == track.Difficulty)
                Ghost = null;
            PartMenu = LaneMenuItem.Build(
                canPick: p => p == Lane.Part || !layout.Contains(p),
                isCurrent: p => p == Lane.Part,
                pick: p => layout.SetPart(Lane, p));
            OnPropertyChanged(nameof(AddMenu));
            RefreshDifficulties();
            Audio?.Refresh();
        }

        /// <summary>Takes in which difficulties have notes, after an edit.</summary>
        internal void RefreshDifficulties()
        {
            var part = Track is { } shown ? Editor?.Session.Chart.PartOf(shown.Instrument) : null;
            int withNotes = 0;
            if (part is not null)
            {
                foreach (var difficulty in Enum.GetValues<ChartDifficulty>())
                {
                    if (part[difficulty].Notes.Count > 0)
                        withNotes |= 1 << (int)difficulty;
                }
            }

            // A vocal track re-ticked or put back by an undo is read again, so it's another part as read.
            var vocals = Vocal is { } vocal ? Editor?.Session.VocalsOf(vocal) : null;
            var of = (Track, Ghost, part, withNotes, vocals);
            if (_difficultiesOf == of)
                return;
            _difficultiesOf = of;

            OnPropertyChanged(nameof(HasCanvas));
            OnPropertyChanged(nameof(HasPlaceholder));
            OnPropertyChanged(nameof(ShowsHighway));
            OnPropertyChanged(nameof(ShowsTimeline));
            OnPropertyChanged(nameof(ShowsEditMenu));
            OnPropertyChanged(nameof(Placeholder));
            OnPropertyChanged(nameof(CanAddVocalPart));
            if (Track is not { } track)
            {
                Difficulties = [];
                GhostMenu = [];
                EditMenu = [];
                return;
            }

            var values = Enum.GetValues<ChartDifficulty>();
            if (Difficulties.Count != values.Length)
            {
                Difficulties = [.. values.Select(d => new DifficultyLetter(
                    d,
                    ChartLabels.DifficultyName(d),
                    part is not null && part[d].Notes.Count > 0,
                    d == track.Difficulty,
                    new RelayCommand(() => ShowDifficulty(d))))];
            }
            else
            {
                // In place, keeping the same objects: replacing the list would clear the header drop-down's
                // selection, leaving its box blank until the difficulty is picked again. Made in the same order,
                // so each letter is its difficulty's.
                for (int i = 0; i < values.Length; i++)
                {
                    var letter = Difficulties[i];
                    letter.HasNotes = part is not null && part[values[i]].Notes.Count > 0;
                    letter.IsCurrent = values[i] == track.Difficulty;
                }
            }
            GhostMenu =
            [
                LaneMenuItem.Choice(Strings.LaneGhostNone, Ghost is null, () => Ghost = null),
                .. Enum.GetValues<ChartDifficulty>().Where(d => d != track.Difficulty)
                    .Select(d => LaneMenuItem.Choice(ChartLabels.DifficultyName(d), Ghost == d, () => Ghost = d)),
            ];
            EditMenu = BuildEditMenu(track, part);
        }

        private IReadOnlyList<LaneMenuItem> BuildEditMenu(ChartTrackKey track, EditablePart? part)
        {
            if (Editor is not { } editor || part is null)
                return [];
            bool hasNotes = part[track.Difficulty].Notes.Count > 0;
            var others = Enum.GetValues<ChartDifficulty>().Where(d => d != track.Difficulty).Reverse();

            // The generators work on the whole part, so on whichever lane of it the menu is opened from, and only on the
            // parts they can: star power on 5-fret and keys, fills on drums.
            var generate = new List<LaneMenuItem>();
            if (StarPowerGenerator.CanGenerate(part.Kind))
                generate.Add(LaneMenuItem.Action(Strings.LaneGenerateStarPower, part.HasNotes, () => _ = editor.GenerateStarPowerAsync(track.Instrument), "GenerateStroke"));
            if (part.Kind == ChartInstrumentKind.Drums)
                generate.Add(LaneMenuItem.Action(Strings.LaneGenerateFills, part.HasNotes, () => _ = editor.GenerateFillsAsync(track.Instrument),"GenerateStroke"));
            return
            [
                LaneMenuItem.Submenu(
                    string.Format(CultureInfo.CurrentCulture, Strings.LaneCopyToFormat, ChartLabels.DifficultyName(track.Difficulty)),
                    [.. others.Select(d => LaneMenuItem.Action(ChartLabels.DifficultyName(d), hasNotes, () => _ = editor.CopyDifficultyAsync(track, d)))],
                    "CopyStroke"),
                LaneMenuItem.Action(Strings.LaneReduce, part.HasNotes, () => _ = editor.ReduceAsync(track), "SortDescendingStroke"),
                .. generate,
                LaneMenuItem.Action(Strings.LaneSelectLane, hasNotes, () => editor.Session.Tools.SelectLane(track), "SelectStroke"),
                LaneMenuItem.Action(Strings.LaneSelectSection, true, () => editor.Session.Tools.SelectSectionAtCursor(), "SelectStroke"),
                LaneMenuItem.Action(Strings.LaneCut, true, editor.Cut, "CutStroke"),
                LaneMenuItem.Action(Strings.LaneCopy, true, editor.Copy, "CopyStroke"),
                LaneMenuItem.Action(Strings.LanePaste, true, () => _ = editor.PasteAsync(PasteMode.Overwrite), "PasteStroke"),
                LaneMenuItem.Action(Strings.LanePasteMerge, true, () => _ = editor.PasteAsync(PasteMode.Merge), "PasteMergeStroke"),
                LaneMenuItem.Submenu(Strings.LaneTransform,
                [
                    LaneMenuItem.Action(Strings.TransformLeftyFlip, true, editor.Session.Tools.LeftyFlip, "FlipStroke"),
                    LaneMenuItem.Action(Strings.TransformShiftUp, true, () => editor.Session.Tools.ShiftLanes(1), "ArrowRightStroke"),
                    LaneMenuItem.Action(Strings.TransformShiftDown, true, () => editor.Session.Tools.ShiftLanes(-1), "ArrowLeftStroke"),
                    LaneMenuItem.Action(Strings.TransformClearSustains, true, () => editor.Session.Tools.SetSustains(SustainChange.Clear), "ClearStroke"),
                    LaneMenuItem.Action(Strings.TransformGridSustains, true, () => editor.Session.Tools.SetSustains(SustainChange.GridStep), "SustainStroke"),
                    LaneMenuItem.Action(Strings.TransformSustainsUntilNext, true, () => editor.Session.Tools.SetSustains(SustainChange.UntilNextNote), "SustainToNextStroke"),
                ], "TransformStroke"),
                LaneMenuItem.Submenu(Strings.LaneStretch,
                [
                    LaneMenuItem.Action(Strings.StretchDouble, true, () => editor.Session.Tools.StretchSelection(2), "StretchStroke"),
                    LaneMenuItem.Action(Strings.StretchThreeHalves, true, () => editor.Session.Tools.StretchSelection(1.5), "StretchStroke"),
                    LaneMenuItem.Action(Strings.StretchTwoThirds, true, () => editor.Session.Tools.StretchSelection(2.0 / 3), "SqueezeStroke"),
                    LaneMenuItem.Action(Strings.StretchHalf, true, () => editor.Session.Tools.StretchSelection(0.5), "SqueezeStroke"),
                    LaneMenuItem.Action(Strings.StretchToCursor, true, () => editor.Session.Tools.StretchSelection(null), "StretchToCursorStroke"),
                ], "StretchStroke"),
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
    /// <remarks>One stable instance per difficulty, updated in place: the header's drop-down keeps its selection
    /// across refreshes only while its items stay the same objects.</remarks>
    public sealed partial class DifficultyLetter : ObservableObject
    {
        public DifficultyLetter(ChartDifficulty difficulty, string name, bool hasNotes, bool isCurrent, IRelayCommand show)
        {
            Difficulty = difficulty;
            Name = name;
            HasNotes = hasNotes;
            IsCurrent = isCurrent;
            Show = show;
        }

        public ChartDifficulty Difficulty { get; }

        public string Name { get; }

        [ObservableProperty]
        public partial bool HasNotes { get; set; }

        [ObservableProperty]
        public partial bool IsCurrent { get; set; }

        public IRelayCommand Show { get; }

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
