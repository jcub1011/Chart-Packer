using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using ChartBuilder.Resources;
using ChartBuilder.Services;
using ChartShared.Audio.Synthesis;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charting.Layout;
using ChartShared.Core.Charts;
using ChartShared.UI.Controls.Highway;
using ChartShared.UI.Services;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>A snap the toolbar offers.</summary>
    public sealed record SnapChoice(int Division, string Label);

    /// <summary>A kind of phrase the toolbar's picker offers the Phrase tool, or disco flip.</summary>
    public sealed record PhraseKindChoice(RailKind Kind, string Label);

    /// <summary>
    /// The chart's editor (charter-spec §4.5, C3): the lanes and their canvases, the tools, the snap, and the editor's
    /// <see cref="EditorSession"/>, joined to playback. The cursor is the lanes' strike line and the playhead: it follows
    /// the song while it plays (C10 item 6), and when the user moves it (scrolling a lane, a key, a click) playback seeks
    /// there, playing or not. The hit sounds and the bottom timeline's overview follow the chart as it's edited.
    /// </summary>
    public sealed partial class ChartEditorViewModel : ViewModelBase, IDisposable
    {
        // How long after an edit the hit sounds and the overview are worked out again, so a burst of edits does it once.
        private static readonly TimeSpan RefreshDelay = TimeSpan.FromMilliseconds(250);

        // Tap tempo's message, which each tap updates.
        private const string TapTempoMessageKey = "tap-tempo";

        // How long L may be held and still be a tap of the Loop tool rather than holding it: a tap turns what repeating
        // the loop region does on or off (C10 item 2), a longer press only uses the tool.
        private static readonly TimeSpan LoopTapTime = TimeSpan.FromMilliseconds(400);

        // The Phrase tool's kinds, for 5-fret and GHL, for 4-lane and 5-lane drums, and for Pro drums, which have disco flip.
        private static readonly IReadOnlyList<PhraseKindChoice> FretPhraseChoices = PhraseChoicesOf(ChartInstrumentKind.FiveFret, proDrums: false);
        private static readonly IReadOnlyList<PhraseKindChoice> DrumPhraseChoices = PhraseChoicesOf(ChartInstrumentKind.Drums, proDrums: false);
        private static readonly IReadOnlyList<PhraseKindChoice> ProDrumPhraseChoices = PhraseChoicesOf(ChartInstrumentKind.Drums, proDrums: true);

        private readonly BuilderSettingsService _settings;
        private readonly IConfirmationService _confirmations;
        private readonly IAuthoringDialogService? _dialogs;
        private readonly InputService? _input;
        private readonly DispatcherTimer _refresh;
        private readonly DispatcherTimer _tapEnd;
        private readonly TapTempo _taps = new();
        private double _firstTap;
        private bool _syncing;
        private IReadOnlyList<PhraseKindChoice>? _phraseChoices;

        // The modifier keys held (HoldModifiers), whose swaps the place tool's options show while they're held.
        private EditorModifiers _heldModifiers;

        // What the place tool's options last showed for (RefreshPlacing): the tool, the lane in focus, the drums' variant and the options.
        private (EditorTool, ChartTrackKey?, DrumsVariant?, PlaceOptions)? _placingShown;

        // The frame the playhead was last followed in.
        private TimeSpan? _followed;

        // When L was pressed, to tell a tap of it from the tool's being held.
        private long _loopHeldAt;

        /// <param name="messages">Where what the editor has to say goes, for the window's message bar.</param>
        /// <param name="audio">The song's audio as the lanes draw and analyse it.</param>
        /// <param name="dialogs">Shows the authoring tools' dialogs; null where there are none to show.</param>
        /// <param name="input">The instrument devices and their bindings, for playtest and record mode; null where there are none.</param>
        public ChartEditorViewModel(EditorSong song, ChartUndoStack undo, PlaybackViewModel playback, EditorAudio audio, BuilderSettingsService settings, IConfirmationService confirmations, EditorMessages messages, LaneDockLayout layout, IAuthoringDialogService? dialogs = null, InputService? input = null)
        {
            _settings = settings;
            _confirmations = confirmations;
            _dialogs = dialogs;
            _input = input;
            Song = song;
            Playback = playback;
            Audio = audio;
            Messages = messages;
            var saved = settings.Settings;
            Session = new EditorSession(undo, song.Delay, song.EffectivePairs())
            {
                Snap = SnapGrid.Of(saved.DefaultSnapDivision),
                KeepTimes = saved.TempoEdits == TempoEditMode.KeepTimes,
            };
            SnapChoices = [.. SnapGrid.CommonDivisions.Select(d => new SnapChoice(d, BuilderSettingsCatalog.SnapLabel(d)))];
            SelectedRecordMode = RecordModeChoices[0];
            SelectedQuantize = SnapChoices.FirstOrDefault(c => c.Division == saved.QuantizeDivision) ?? SnapChoices.First(c => c.Division == BuilderSettings.DefaultQuantizeDivision);
            IsRecordQuantized = saved.RecordQuantize;
            WheelUpIsLater = IsWheelUpLater(saved);
            Lanes = new LaneDockViewModel(layout, this);
            Properties = new PropertiesViewModel(this);
            Session.Onsets = new LaneOnsetSource(this);
            Session.VisibleTracks = () => [.. Lanes.Layout.Lanes.Select(l => l.Part.Track).OfType<ChartTrackKey>()];
            Session.Loop = Playback;
            AudioAssist = new AudioAssistViewModel(this);
            Preview = new PreviewViewModel(this, settings);
            Difficulty = new DifficultyViewModel(this);
            Playtest = new PlaytestViewModel(this, settings, input?.Settings.Settings, Devices);

            _refresh = new DispatcherTimer(DispatcherPriority.Background) { Interval = RefreshDelay };
            _refresh.Tick += (_, _) => RefreshDerived();
            _tapEnd = new DispatcherTimer { Interval = TimeSpan.FromSeconds(TapTempo.ResetSeconds) };
            _tapEnd.Tick += (_, _) => _ = ApplyTapsAsync();

            Session.StateChanged += OnSessionState;
            Session.StatusPosted += OnStatusPosted;
            Session.ChartChanged += OnChartChanged;
            Session.SongSettingsChanged += OnSongSettingsChanged;
            Session.Viewport.Changed += OnViewportChanged;
            Session.Viewport.Moved += OnCursorMoved;
            Session.Selection.Changed += OnSelectionChanged;
            Playback.PropertyChanged += OnPlaybackChanged;
            Playback.Scrubbed += OnScrubbed;
            Lanes.LayoutChanged += OnLayoutChanged;
            Session.Focused = Lanes.Layout.Lanes.Select(l => l.Part.Track).FirstOrDefault(t => t is not null);
            RefreshDerived();
        }

        public EditorSong Song { get; }

        /// <summary>The editor's state: the undo stack, the cursor and view, the selection, the snap and the tool.</summary>
        public EditorSession Session { get; }

        public PlaybackViewModel Playback { get; }

        /// <summary>The song's audio as the lanes draw and analyse it: waveforms, spectrograms and onsets.</summary>
        public EditorAudio Audio { get; }

        /// <summary>Snap to onset, Nudge to onsets, and the proposed tempo map (charter-spec C14).</summary>
        public AudioAssistViewModel AudioAssist { get; }

        /// <summary>The highway preview, docked in the side panel or in a window of its own (charter-spec C18).</summary>
        public PreviewViewModel Preview { get; }

        /// <summary>The difficulty analysis of a five-fret lane, docked in the side panel or in a window of its own.</summary>
        public DifficultyViewModel Difficulty { get; }

        /// <summary>Playtest: the focused lane played with the keyboard and the instrument devices (charter-spec C19).</summary>
        public PlaytestViewModel Playtest { get; }

        /// <summary>What a lane draws its audio as until it's switched: the setting's.</summary>
        public LaneBackdrop DefaultBackdrop => _settings.Settings.DefaultLaneBackdrop;

        /// <summary>What the editor has had to say: why an edit was refused, or what a key did.</summary>
        public EditorMessages Messages { get; }

        /// <summary>The lanes in the middle of the window: the parts' highways and the vocals.</summary>
        public LaneDockViewModel Lanes { get; }

        /// <summary>The Properties panel: what's selected.</summary>
        public PropertiesViewModel Properties { get; }

        /// <summary>The view a lane opens in.</summary>
        public LaneView DefaultView => _settings.Settings.DefaultEditorView;

        /// <summary>The look-ahead of a lane that hasn't had its own set: the highway speed setting's.</summary>
        public double DefaultLookAhead => BuilderSettings.LookAheadAtSpeedOne / _settings.Settings.EditorHighwaySpeed;

        /// <summary>
        /// Marks on the bottom timeline the span the lane in focus shows, from its look-ahead, after the cursor, the focus
        /// or the look-ahead changes.
        /// </summary>
        public void RefreshVisibleSpan()
        {
            // A loop, not a query: this runs every frame while the song plays.
            EditorLaneViewModel? focused = null, first = null, firstVocal = null;
            var key = Session.Focused;
            foreach (var each in Lanes.All)
            {
                if (each.Track is { } track && track == key)
                {
                    focused = each;
                    break;
                }

                // A highway's span before a vocal lane's, which is only the one when there's no highway.
                if (each.IsVocal)
                    firstVocal ??= each.HasCanvas ? each : null;
                else if (first is null && each.HasCanvas)
                    first = each;
            }

            var lane = focused ?? first ?? firstVocal;
            var (start, end) = Session.Viewport.SpanOf(lane?.LookAhead ?? DefaultLookAhead);
            VisibleStart = start;
            VisibleEnd = end;
        }

        public IReadOnlyList<SnapChoice> SnapChoices { get; }

        /// <summary>The snap, as the toolbar picks it.</summary>
        public SnapChoice? SelectedSnap
        {
            get => SnapChoices.FirstOrDefault(c => c.Division == Session.Snap.Division);
            set
            {
                if (value is not null)
                    Session.Snap = Session.Snap with { Division = value.Division };
            }
        }

        /// <summary>Whether notes snap to the grid.</summary>
        public bool IsSnapOn
        {
            get => Session.Snap.IsOn;
            set => Session.Snap = Session.Snap with { IsOn = value };
        }

        public bool IsSelectTool
        {
            get => Session.Tool == EditorTool.Select;
            set => SetTool(EditorTool.Select, value);
        }

        public bool IsPlaceTool
        {
            get => Session.Tool == EditorTool.Place;
            set => SetTool(EditorTool.Place, value);
        }

        public bool IsEraseTool
        {
            get => Session.Tool == EditorTool.Erase;
            set => SetTool(EditorTool.Erase, value);
        }

        public bool IsPhraseTool
        {
            get => Session.Tool == EditorTool.Phrase;
            set => SetTool(EditorTool.Phrase, value);
        }

        public bool IsTempoTool
        {
            get => Session.Tool == EditorTool.Tempo;
            set => SetTool(EditorTool.Tempo, value);
        }

        /// <summary>
        /// Whether the Sync tool's button is down: the tool is picked, or Ctrl + Shift is held, which holds it on for as
        /// long as they're held. A synchronized region drawn is kept whatever tool is picked, and only the X over it takes
        /// it away.
        /// </summary>
        public bool IsSyncTool
        {
            get => Session.Tool == EditorTool.Sync || (IsCtrlHeld && IsShiftHeld);
            set => SetTool(EditorTool.Sync, value);
        }

        /// <summary>
        /// Whether the Loop tool's button is down: the tool is picked, or L is held, which holds it on for as long as it
        /// is. A loop region drawn is kept whatever tool is picked (charter-spec C10 item 2), and only its X, or the
        /// middle button with the tool active, takes it away.
        /// </summary>
        public bool IsLoopTool
        {
            get => Session.Tool == EditorTool.Loop || Session.LoopToolHeld;
            set => SetTool(EditorTool.Loop, value);
        }

        /// <summary>Holds the Loop tool on while L is down: the toolbar's button is lit meanwhile.</summary>
        public void HoldLoopTool(bool held)
        {
            if (held)
                _loopHeldAt = Stopwatch.GetTimestamp();
            Session.LoopToolHeld = held;
        }

        /// <summary>
        /// Lets the Loop tool go as L is released. A tap of L (a press held no longer than <see cref="LoopTapTime"/>) that
        /// drew, moved, resized or cleared nothing turns what repeating the region does on or off (charter-spec C10 item
        /// 2); a longer press is the tool held, which only lets it go.
        /// </summary>
        public void ReleaseLoopTool()
        {
            bool tapped = Stopwatch.GetElapsedTime(_loopHeldAt) <= LoopTapTime;
            if (tapped && Session.LoopToolHeld && !Session.LoopTouched && Playback.HasLoop)
                Playback.IsLooping = !Playback.IsLooping;
            Session.LoopToolHeld = false;
        }

        /// <summary>
        /// The kinds of phrase the Phrase tool offers for the part in focus (charter-spec C8): star power, solos and big
        /// rock endings on 5-fret and GHL; star power, solos, fills, rolls and trills on drums, whose big rock ending is a
        /// fill after <c>[coda]</c>; and on Pro drums, disco flip too (C6 item 7), the only drums the games flip.
        /// </summary>
        public IReadOnlyList<PhraseKindChoice> PhraseKindChoices =>
            Session.Focused is { } key && ChartInstruments.KindOf(key.Instrument) == ChartInstrumentKind.Drums
                ? Session.IsProDrums ? ProDrumPhraseChoices : DrumPhraseChoices
                : FretPhraseChoices;

        /// <summary>The kind the Phrase tool draws. Picking one picks the Phrase tool too.</summary>
        public PhraseKindChoice? SelectedPhraseKind
        {
            get
            {
                var kind = Session.PhraseKind;
                if (kind.Phrase is { } phrase && Session.Focused is { } key)
                    kind = PhraseRail.RowOf(ChartInstruments.KindOf(key.Instrument), phrase);
                return PhraseKindChoices.FirstOrDefault(c => c.Kind == kind);
            }

            set
            {
                if (value is null || value.Kind == Session.PhraseKind)
                    return;
                Session.PhraseKind = value.Kind;
                Session.Tool = EditorTool.Phrase;
            }
        }

        // The kind of part in focus, which the place tool's options are offered for; null for none.
        private ChartInstrumentKind? FocusedKind => Session.Focused is { } key ? ChartInstruments.KindOf(key.Instrument) : null;

        /// <summary>Whether the place tool's options for a 5-fret or GHL lane show: the place tool is picked, and such a lane has focus.</summary>
        public bool ShowsFretPlaceOptions => IsPlaceTool && FocusedKind is ChartInstrumentKind.FiveFret or ChartInstrumentKind.SixFret;

        /// <summary>Whether the place tool's options for a drums lane show: the place tool is picked, and a drums lane has focus.</summary>
        public bool ShowsDrumPlaceOptions => IsPlaceTool && FocusedKind is ChartInstrumentKind.Drums;

        /// <summary>
        /// Whether the place tool offers cymbal or tom: on drums set to Pro, the only drums with toms, as the note menu and
        /// Properties offer them; drums read as Pro from their markers alone can't have toms set.
        /// </summary>
        public bool ShowsTomChoice => ShowsDrumPlaceOptions && Session.Chart.DrumsVariant == DrumsVariant.Pro;

        /// <summary>Whether the place tool offers the 2× kick: on Expert, the only difficulty with one.</summary>
        public bool ShowsDoubleKickChoice => ShowsDrumPlaceOptions && Session.Focused?.Difficulty == ChartDifficulty.Expert;

        /// <summary>Whether the place tool leaves the chord a note goes in as it is, a new one natural.</summary>
        public bool IsNaturalPlace
        {
            get => Session.Placing.ChordType == ChordFlags.None;
            set => SetPlacing(value, Session.Placing with { ChordType = ChordFlags.None });
        }

        /// <summary>Whether the place tool makes the chord a note goes in a forced HOPO.</summary>
        public bool IsHopoPlace
        {
            get => Session.Placing.ChordType == ChordFlags.ForceHopo;
            set => SetPlacing(value, Session.Placing with { ChordType = ChordFlags.ForceHopo });
        }

        /// <summary>Whether the place tool makes the chord a note goes in a forced strum.</summary>
        public bool IsStrumPlace
        {
            get => Session.Placing.ChordType == ChordFlags.ForceStrum;
            set => SetPlacing(value, Session.Placing with { ChordType = ChordFlags.ForceStrum });
        }

        /// <summary>Whether the place tool makes the chord a note goes in a tap.</summary>
        public bool IsTapPlace
        {
            get => Session.Placing.ChordType == ChordFlags.Tap;
            set => SetPlacing(value, Session.Placing with { ChordType = ChordFlags.Tap });
        }

        /// <summary>
        /// Takes in the modifier keys held, wherever focus is in the window: while Alt is held, Open shows the other way
        /// about, and while Ctrl is, the drag shows the other choice, as the place tool would use them.
        /// </summary>
        public void HoldModifiers(EditorModifiers modifiers)
        {
            if (modifiers == _heldModifiers)
                return;
            bool swapsOpen = IsAltHeld, swapsDrag = IsCtrlHeld, syncHeld = IsCtrlHeld && IsShiftHeld;
            _heldModifiers = modifiers;
            ModifiersHeld?.Invoke(this, modifiers);
            if ((IsCtrlHeld && IsShiftHeld) != syncHeld)
                OnPropertyChanged(nameof(IsSyncTool));
            if (IsAltHeld != swapsOpen)
                OnPropertyChanged(nameof(PlaceOpen));
            if (IsCtrlHeld != swapsDrag)
            {
                OnPropertyChanged(nameof(IsRunDrag));
                OnPropertyChanged(nameof(IsSustainDrag));
            }
        }

        /// <summary>
        /// The modifier keys held changed, wherever focus is: the lanes take them in, so the place tool's ghost changes
        /// with Alt even while a lane doesn't have the keys.
        /// </summary>
        public event EventHandler<EditorModifiers>? ModifiersHeld;

        private bool IsAltHeld => _heldModifiers.HasFlag(EditorModifiers.Alt);

        private bool IsCtrlHeld => _heldModifiers.HasFlag(EditorModifiers.Control);

        private bool IsShiftHeld => _heldModifiers.HasFlag(EditorModifiers.Shift);

        /// <summary>
        /// Whether a click of the place tool puts down an open note: <see cref="PlaceOptions.Open"/>, swapped while Alt is
        /// held. Picked while Alt is held, it's set so as to show what was picked.
        /// </summary>
        public bool PlaceOpen
        {
            get => Session.Placing.Open != IsAltHeld;
            set => Session.Placing = Session.Placing with { Open = value != IsAltHeld };
        }

        /// <summary>Whether a drag of the place tool lays a run of the note: not <see cref="PlaceOptions.DragSustains"/>, swapped while Ctrl is held.</summary>
        public bool IsRunDrag
        {
            get => Session.Placing.DragSustains == IsCtrlHeld;
            set => SetPlacing(value, Session.Placing with { DragSustains = IsCtrlHeld });
        }

        /// <summary>Whether a drag of the place tool draws out the note's sustain: <see cref="PlaceOptions.DragSustains"/>, swapped while Ctrl is held.</summary>
        public bool IsSustainDrag
        {
            get => Session.Placing.DragSustains != IsCtrlHeld;
            set => SetPlacing(value, Session.Placing with { DragSustains = !IsCtrlHeld });
        }

        /// <summary>Whether the place tool leaves a yellow, blue or green pad a cymbal, unless a tom marker covers it.</summary>
        public bool IsCymbalPlace
        {
            get => !Session.Placing.Tom;
            set => SetPlacing(value, Session.Placing with { Tom = false });
        }

        /// <summary>Whether the place tool makes a yellow, blue or green pad a tom, on every difficulty.</summary>
        public bool IsTomPlace
        {
            get => Session.Placing.Tom;
            set => SetPlacing(value, Session.Placing with { Tom = true });
        }

        /// <summary>Whether the place tool places pads as normal hits.</summary>
        public bool IsNormalPlace
        {
            get => Session.Placing.Dynamics == NoteDynamics.None;
            set => SetPlacing(value, Session.Placing with { Dynamics = NoteDynamics.None });
        }

        /// <summary>Whether the place tool places pads as accents.</summary>
        public bool IsAccentPlace
        {
            get => Session.Placing.Dynamics == NoteDynamics.Accent;
            set => SetPlacing(value, Session.Placing with { Dynamics = NoteDynamics.Accent });
        }

        /// <summary>Whether the place tool places pads as ghosts.</summary>
        public bool IsGhostPlace
        {
            get => Session.Placing.Dynamics == NoteDynamics.Ghost;
            set => SetPlacing(value, Session.Placing with { Dynamics = NoteDynamics.Ghost });
        }

        /// <summary>Whether the place tool places kicks as 2× kicks, on Expert.</summary>
        public bool PlaceDoubleKick
        {
            get => Session.Placing.DoubleKick;
            set => Session.Placing = Session.Placing with { DoubleKick = value };
        }

        /// <summary>What the bottom timeline shows of the whole song for the lane in focus (charter-spec C3 item 2).</summary>
        [ObservableProperty]
        public partial OverviewData? Overview { get; private set; }

        /// <summary>The chart tick the playhead is on, for beside its time, e.g. "Tick 15,360"; Tick 0 before the chart starts.</summary>
        public string PlayheadTickText => string.Format(
            CultureInfo.CurrentCulture, Strings.TimelineTickFormat, Math.Max(0, (long)Math.Floor(Session.TickAt(Playback.PositionSeconds))));

        /// <summary>Where the span of the song the lanes show starts, in seconds of audio, as the overview marks it.</summary>
        [ObservableProperty]
        public partial double VisibleStart { get; private set; }

        /// <summary>Where the span of the song the lanes show ends, in seconds of audio.</summary>
        [ObservableProperty]
        public partial double VisibleEnd { get; private set; }

        /// <summary>Whether the mouse wheel turned up moves later, along the lanes in either view and the timeline alike: the setting's.</summary>
        [ObservableProperty]
        public partial bool WheelUpIsLater { get; private set; }

        // Whether the wheel turned up is later, as the settings say.
        private static bool IsWheelUpLater(BuilderSettings settings) => settings.WheelScrollDirection == WheelScrollDirection.UpIsLater;

        /// <summary>Takes up what may have changed in Settings: whether tempo edits keep times, what lanes draw their audio as, and which way the wheel scrolls.</summary>
        public void ReloadSettings()
        {
            Session.KeepTimes = _settings.Settings.TempoEdits == TempoEditMode.KeepTimes;
            WheelUpIsLater = IsWheelUpLater(_settings.Settings);
            Audio.ReloadSettings(_settings.Settings);
            foreach (var lane in Lanes.All)
                lane.Audio.Refresh();
            Playtest.ReloadSettings();
        }

        /// <summary>Follows the playhead, once a frame while it plays: the cursor moves with it, and the view with the cursor.</summary>
        /// <param name="frame">The frame's time, as the screen gives it. Every lane asks each frame; the first to ask moves
        /// the cursor for all of them, which redraws them all, so the rest don't move it again.</param>
        public void FollowPlayback(TimeSpan frame)
        {
            if (_followed == frame)
                return;
            _followed = frame;
            if (Playback.IsPlaying)
                Session.Viewport.Follow(Playback.Clock().TotalSeconds);
            _devices?.Sample();
            Playtest.Frame();
        }

        /// <summary>Plays or pauses from the cursor (Space).</summary>
        public void PlayPause()
        {
            if (Playback.PlayPauseCommand.CanExecute(null))
                Playback.PlayPauseCommand.Execute(null);
        }

        /// <summary>
        /// Sets the loop region to the selection's span: what preview practice starts from when there's no region yet
        /// (charter-spec C18 item 2). The Loop tool and L are how a region is drawn by hand (C10 item 2).
        /// </summary>
        public void SetLoopFromSelection()
        {
            if (Session.Selection.Span is not { } span)
            {
                Session.Status = Strings.NothingToLoopStatus;
                return;
            }

            Playback.SetLoop(Session.AudioAt(span.From), Session.AudioAt(span.To));
        }

        /// <summary>
        /// A tap of tap tempo (B, charter-spec C4 item 5), in time with the song as it's heard. Once the taps stop, the
        /// segment the first was in is set to their tempo.
        /// </summary>
        public void Tap()
        {
            if (!Playback.IsPlaying)
            {
                Session.Status = Strings.TapTempoNotPlayingStatus;
                return;
            }

            double now = Playback.Clock().TotalSeconds;
            if (_taps.Tap(now))
                _firstTap = now;
            // One message, updated with each tap, rather than one a tap.
            Messages.Info(
                _taps.Bpm is { } bpm
                    ? string.Format(CultureInfo.CurrentCulture, Strings.TapTempoStatusFormat, bpm, _taps.Count)
                    : string.Format(CultureInfo.CurrentCulture, Strings.TapTempoStartedStatusFormat, _taps.Count),
                TapTempoMessageKey);
            _tapEnd.Stop();
            _tapEnd.Start();
        }

        /// <summary>
        /// Makes a tempo-map edit, asking what to do when it can't keep an anchor where it is (charter-spec C4 item 2): unanchor
        /// the marker, or add one a beat before it.
        /// </summary>
        /// <param name="make">Works out the edit for each answer.</param>
        /// <returns>Whether the edit was made.</returns>
        public async Task<bool> ApplyTempoEditAsync(Func<BlockedAnchor, TempoEdit> make)
        {
            var edit = make(BlockedAnchor.Refuse);
            if (edit.IsRefused && edit.Blocking is not null)
            {
                string message = string.Format(CultureInfo.CurrentCulture, Strings.AnchorBlockedMessageFormat, edit.Refusal);
                if (edit.OneBeatBefore is not null)
                {
                    var choice = await _confirmations.ChooseAsync(Strings.AnchorBlockedTitle, message, Strings.AnchorUnanchorButton, Strings.AnchorAddMarkerButton);
                    edit = choice switch
                    {
                        ConfirmChoice.First => make(BlockedAnchor.Unanchor),
                        ConfirmChoice.Second => make(BlockedAnchor.AddMarkerBefore),
                        _ => null!,
                    };
                }
                else
                {
                    edit = await _confirmations.ConfirmAsync(Strings.AnchorBlockedTitle, message, Strings.AnchorUnanchorButton)
                        ? make(BlockedAnchor.Unanchor)
                        : null!;
                }

                if (edit is null)
                    return false;
            }

            if (!edit.IsRefused)
                return Session.Execute(edit.Command!);
            Session.Status = edit.Refusal;
            return false;
        }

        [RelayCommand]
        private void CoarserSnap() => Session.Snap = Session.Snap.Coarser();

        [RelayCommand]
        private void FinerSnap() => Session.Snap = Session.Snap.Finer();

        public void Dispose()
        {
            _refresh.Stop();
            _tapEnd.Stop();
            Session.StateChanged -= OnSessionState;
            Session.StatusPosted -= OnStatusPosted;
            Session.ChartChanged -= OnChartChanged;
            Session.SongSettingsChanged -= OnSongSettingsChanged;
            Session.Viewport.Changed -= OnViewportChanged;
            Session.Viewport.Moved -= OnCursorMoved;
            Session.Selection.Changed -= OnSelectionChanged;
            Playback.PropertyChanged -= OnPlaybackChanged;
            Playback.Scrubbed -= OnScrubbed;
            Lanes.LayoutChanged -= OnLayoutChanged;
            Preview.Dispose();
            Difficulty.Dispose();
            Playtest.Dispose();
            _devices?.Dispose();
            _devices = null;
            Session.Detach();
        }

        private void SetTool(EditorTool tool, bool on)
        {
            // A tool's button pressed again lets it go for the select tool, whose own button stays down: one tool is always
            // picked.
            if (on)
                Session.Tool = tool;
            else if (tool != EditorTool.Select && Session.Tool == tool)
                Session.Tool = EditorTool.Select;
            else
                OnSessionState(this, EventArgs.Empty);
        }

        /// <summary>
        /// Picks <paramref name="picked"/> as the place tool's options, from a button of a row of them, one of which is always
        /// down: pressed again, the button stays down.
        /// </summary>
        private void SetPlacing(bool on, PlaceOptions picked)
        {
            if (on)
                Session.Placing = picked;
            else
                RefreshPlacing();
        }

        private void OnSessionState(object? sender, EventArgs e)
        {
            OnPropertyChanged(nameof(SelectedSnap));
            OnPropertyChanged(nameof(IsSnapOn));
            OnPropertyChanged(nameof(IsSelectTool));
            OnPropertyChanged(nameof(IsPlaceTool));
            OnPropertyChanged(nameof(IsEraseTool));
            OnPropertyChanged(nameof(IsPhraseTool));
            OnPropertyChanged(nameof(IsTempoTool));
            OnPropertyChanged(nameof(IsSyncTool));
            OnPropertyChanged(nameof(IsLoopTool));
            OnPropertyChanged(nameof(IsRecording));
            OnPropertyChanged(nameof(RecordToolTip));

            // The place tool's options go by these alone, so a state change of something else, such as each note record
            // mode takes in, leaves them be.
            var placing = (Session.Tool, Session.Focused, Session.Chart.DrumsVariant, Session.Placing);
            if (placing != _placingShown)
            {
                _placingShown = placing;
                RefreshPlacing();
            }

            RefreshPhraseChoices();
            RefreshVisibleSpan();
            AudioAssist.Refresh();
            ScheduleRefresh();
        }

        /// <summary>The place tool's options, and which of them show, after the tool, the focus or the options change.</summary>
        private void RefreshPlacing()
        {
            OnPropertyChanged(nameof(ShowsFretPlaceOptions));
            OnPropertyChanged(nameof(ShowsDrumPlaceOptions));
            OnPropertyChanged(nameof(ShowsTomChoice));
            OnPropertyChanged(nameof(ShowsDoubleKickChoice));
            OnPropertyChanged(nameof(IsNaturalPlace));
            OnPropertyChanged(nameof(IsHopoPlace));
            OnPropertyChanged(nameof(IsStrumPlace));
            OnPropertyChanged(nameof(IsTapPlace));
            OnPropertyChanged(nameof(PlaceOpen));
            OnPropertyChanged(nameof(IsRunDrag));
            OnPropertyChanged(nameof(IsSustainDrag));
            OnPropertyChanged(nameof(IsCymbalPlace));
            OnPropertyChanged(nameof(IsTomPlace));
            OnPropertyChanged(nameof(IsNormalPlace));
            OnPropertyChanged(nameof(IsAccentPlace));
            OnPropertyChanged(nameof(IsGhostPlace));
            OnPropertyChanged(nameof(PlaceDoubleKick));
        }

        /// <summary>
        /// The picker's list and pick, after the focus or the drums' variant changes. The list changes only with the kind of
        /// part in focus and whether drums are Pro, so it isn't rebuilt on every change.
        /// </summary>
        private void RefreshPhraseChoices()
        {
            if (!ReferenceEquals(_phraseChoices, PhraseKindChoices))
            {
                _phraseChoices = PhraseKindChoices;
                OnPropertyChanged(nameof(PhraseKindChoices));
            }

            OnPropertyChanged(nameof(SelectedPhraseKind));
        }

        /// <summary>A phrase's kind, in words: Star power, Solo, Drum fill…</summary>
        internal static string PhraseKindName(PhraseKind kind) => kind switch
        {
            PhraseKind.StarPower => Strings.PhraseKindStarPower,
            PhraseKind.Solo => Strings.PhraseKindSolo,
            PhraseKind.DrumFill => Strings.PhraseKindDrumFill,
            PhraseKind.Roll => Strings.PhraseKindRoll,
            PhraseKind.Trill => Strings.PhraseKindTrill,
            _ => Strings.PhraseKindBigRockEnding,
        };

        /// <summary>A kind the Phrase tool draws, in words: a phrase's kind, or Disco flip.</summary>
        internal static string PhraseKindName(RailKind kind) => kind.Phrase is { } phrase ? PhraseKindName(phrase) : Strings.PhraseKindDiscoFlip;

        private static IReadOnlyList<PhraseKindChoice> PhraseChoicesOf(ChartInstrumentKind kind, bool proDrums) =>
            [.. PhraseRail.ToolKindsOf(kind, proDrums).Select(k => new PhraseKindChoice(k, PhraseKindTitle(k)))];

        /// <summary>A kind the Phrase tool draws, in title case, as its picker names it: Star Power, Drum Fill, Disco Flip…</summary>
        private static string PhraseKindTitle(RailKind kind) => kind.Phrase switch
        {
            null => Strings.PhraseKindTitleDiscoFlip,
            PhraseKind.StarPower => Strings.PhraseKindTitleStarPower,
            PhraseKind.Solo => Strings.PhraseKindTitleSolo,
            PhraseKind.DrumFill => Strings.PhraseKindTitleDrumFill,
            PhraseKind.Roll => Strings.PhraseKindTitleRoll,
            PhraseKind.Trill => Strings.PhraseKindTitleTrill,
            _ => Strings.PhraseKindTitleBigRockEnding,
        };

        // What the session says is why something was refused.
        private void OnStatusPosted(object? sender, string status) => Messages.Warning(status);

        private void OnChartChanged(object? sender, EditorChartChangedEventArgs e)
        {
            OnPropertyChanged(nameof(PlayheadTickText)); // A tempo change moves the ticks against the audio.
            if (e.IsPreview)
                return;
            ScheduleRefresh();
            Properties.Refresh();
            RefreshPhraseChoices();
            foreach (var lane in Lanes.All)
                lane.RefreshDifficulties();
        }

        private void OnSelectionChanged(object? sender, EventArgs e)
        {
            Properties.Refresh();
            AudioAssist.Refresh();
        }

        // The delay or the song's settings moved every note against the audio, or changed how they're read.
        private void OnSongSettingsChanged(object? sender, EventArgs e)
        {
            ScheduleRefresh();
            Properties.Refresh();
            OnPropertyChanged(nameof(PlayheadTickText));
        }

        private void OnLayoutChanged(object? sender, EventArgs e) => ScheduleRefresh();

        private void OnViewportChanged(object? sender, EventArgs e) => RefreshVisibleSpan();

        /// <summary>The user moved the cursor: playback seeks there, playing or not, so the strike line is where the song is.</summary>
        private void OnCursorMoved(object? sender, EventArgs e)
        {
            if (_syncing || Math.Abs(Session.Viewport.Cursor - Playback.PositionSeconds) < 1e-6)
                return;
            _syncing = true;
            try
            {
                Playback.SeekTo(Session.Viewport.Cursor);
            }
            finally
            {
                _syncing = false;
            }
        }

        /// <summary>The playhead moved on the timeline: the cursor, and the lanes with it, go there.</summary>
        private void OnScrubbed(object? sender, EventArgs e) => Session.Viewport.Follow(Playback.PositionSeconds);

        private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PlaybackViewModel.PositionSeconds))
                OnPropertyChanged(nameof(PlayheadTickText));

            switch (e.PropertyName)
            {
                case nameof(PlaybackViewModel.PositionSeconds) when !Playback.IsPlaying && !_syncing:
                    // Where playback stopped: the cursor stays there.
                    Session.Viewport.Follow(Playback.PositionSeconds);
                    break;
                case nameof(PlaybackViewModel.LengthSeconds):
                    ScheduleRefresh();
                    break;

                // Playing records a pass with record mode on, and playtests the focused lane with it off; stopping ends
                // either (charter-spec C15 item 5, C19 item 4).
                case nameof(PlaybackViewModel.IsPlaying):
                    _devices?.Sample();
                    if (Playback.IsPlaying)
                        StartPlaying();
                    else
                        StopPlaying();
                    break;
            }
        }

        private async Task ApplyTapsAsync()
        {
            _tapEnd.Stop();
            if (_taps.Bpm is not { } bpm)
                return;
            _taps.Reset();

            long tick = TempoMapEdits.SegmentAt(Session.Chart.TempoMap, (long)Math.Max(0, Math.Round(Session.TickAt(_firstTap))));
            long tempo;
            try
            {
                tempo = EditableTempoMap.MicrosecondsPerQuarterOf(bpm);
            }
            catch (ArgumentOutOfRangeException)
            {
                Session.Status = Strings.TapTempoOutOfRangeStatus;
                return;
            }

            await ApplyTempoEditAsync(blocked => TempoMapEdits.SetBpm(Session.Chart, tick, tempo, Session.Delay, Session.KeepTimes, blocked));
        }

        private void ScheduleRefresh()
        {
            _refresh.Stop();
            _refresh.Start();
        }

        /// <summary>Works out what follows the chart: the hit sounds of the lanes shown, and the overview of the lane in focus.</summary>
        private void RefreshDerived()
        {
            _refresh.Stop();
            var shown = Lanes.Layout.Lanes.Select(l => l.Part.Track).OfType<ChartTrackKey>().Where(k => Session.Chart.PartOf(k.Instrument) is not null).ToList();
            // Lent to a dialog, the hit sounds stay quiet until it's given back.
            Playback.SetHits(_playbackBorrows > 0 ? [] : [.. shown.SelectMany(HitsOf)]);
            Overview = OverviewOf(Session.Focused is { } focused && Session.Chart.PartOf(focused.Instrument) is not null ? focused : shown.Cast<ChartTrackKey?>().FirstOrDefault());
        }

        private IEnumerable<NoteHit> HitsOf(ChartTrackKey key)
        {
            bool drums = ChartInstruments.KindOf(key.Instrument) == ChartInstrumentKind.Drums;
            foreach (var note in Session.GameTrack(key).Notes)
            {
                var kind = !drums ? SynthHit.Fret
                    : note.Lane == ChartNote.OpenLane ? SynthHit.Kick
                    : note.Lane == 0 ? SynthHit.Snare
                    : (note.Flags & NoteFlags.Cymbal) != 0 ? SynthHit.Cymbal
                    : SynthHit.Tom;
                yield return new NoteHit(Session.AudioAt(note.Tick), kind);
            }
        }

        private OverviewData? OverviewOf(ChartTrackKey? key) => OverviewData.Of(Session, key, Playback.LengthSeconds);
    }

    /// <summary>A section, at its time in the audio, for the overview.</summary>
    public sealed record OverviewSection(double Seconds, string Name);

    /// <summary>A phrase of the lane in focus, in the audio, and the phrase rail's row it's drawn in, for the overview.</summary>
    /// <param name="Replaced">Whether it's star power the star power window is replacing, drawn muted.</param>
    public readonly record struct OverviewPhrase(double Start, double End, RailKind Row, bool Replaced = false);

    /// <summary>A proposal of the generation window, in the audio, for the overview.</summary>
    public readonly record struct OverviewProposal(double Start, double End, bool Kept);

    /// <summary>
    /// A note of the lane in focus, for the overview: when it starts and its sustain ends in the audio (its start, with
    /// none), the row of its fret, pad or column from the top, or -1 for one across them all, as a kick or an open note,
    /// its colour on the highway, and whether it's drawn in the top half of its row, as a six-fret black fret played with a
    /// white one covers its top half on the highway.
    /// </summary>
    public readonly record struct OverviewNote(double Start, double End, int Row, Color Color, bool Top = false);

    /// <summary>
    /// What the bottom timeline shows of the whole song (charter-spec C3 item 2): sections, the chart's tempo and time
    /// signature changes, the notes of the lane in focus, every one of its phrases, a row for each of the phrase rail's
    /// rows, a generation window's proposals, and the synchronized region and the passages synced with it, each as audio
    /// seconds.
    /// </summary>
    /// <param name="TempoChanges">The chart's tempo and time signature changes, in audio seconds, each earliest first.</param>
    /// <param name="NoteRows">How many rows the notes are drawn in: a fret, pad or column each; none with no lane.</param>
    /// <param name="Notes">The notes of the lane in focus, as the games read them, in time order.</param>
    /// <param name="Rows">The rows of the phrase rail of the lane in focus, in order; none with no lane.</param>
    /// <param name="Phrases">
    /// Its phrases: star power as the games read it, overlapping phrases as one; disco flips of the difficulty shown; and
    /// every other kind as charted.
    /// </param>
    /// <param name="Proposals">The generation window's proposals, whichever lane is in focus, so they can always be found; none without one.</param>
    /// <param name="Synced">
    /// The synchronized region and the passages synced with it: each band's start and end in audio seconds, whether it's
    /// the region itself, and whether the region is one nothing matched, which is drawn hashed in yellow.
    /// </param>
    public sealed record OverviewData(
        IReadOnlyList<OverviewSection> Sections,
        IReadOnlyList<double> TempoChanges,
        int NoteRows,
        IReadOnlyList<OverviewNote> Notes,
        IReadOnlyList<RailKind> Rows,
        IReadOnlyList<OverviewPhrase> Phrases,
        IReadOnlyList<OverviewProposal> Proposals,
        IReadOnlyList<(double Start, double End, bool IsRegion, bool Unmatched)> Synced)
    {
        /// <summary>
        /// The overview of <paramref name="session"/>'s song, <paramref name="length"/> seconds long, for the lane
        /// <paramref name="key"/>; null while the length isn't known. <paramref name="proposals"/> are the ghosts to draw,
        /// or null for none; <paramref name="replaceStarPower"/> shows the chart's star power as being replaced, and
        /// <paramref name="replacedPhrases"/> marks the phrases a generator's plan replaces, whatever their kind.
        /// </summary>
        internal static OverviewData? Of(
            EditorSession session,
            ChartTrackKey? key,
            double length,
            IReadOnlyList<OverviewProposal>? proposals = null,
            bool replaceStarPower = false,
            IReadOnlyList<Phrase>? replacedPhrases = null)
        {
            if (!(length > 0))
                return null;

            var chart = session.Chart;
            var sections = new List<OverviewSection>();
            foreach (var e in chart.Events)
            {
                if (SectionEvents.NameOf(e.Text) is { } name)
                    sections.Add(new OverviewSection(session.AudioAt(e.Tick), name));
            }

            var tempoChanges = TempoMapRows.Of(chart.TempoMap).Select(row => session.AudioAt(row.Tick)).ToList();

            int noteRows = 0;
            var notes = new List<OverviewNote>();
            IReadOnlyList<RailKind> rows = [];
            var phrases = new List<OverviewPhrase>();
            if (key is { } track && chart.PartOf(track.Instrument) is { } part)
            {
                // In the highway's rows and colours: six-fret's black and white frets share a column, a black fret played
                // with a white one in its top half, and a black fret is drawn in its outline's colour, as faded black
                // wouldn't show on the timeline.
                var game = session.GameTrack(track);
                var kind = part.Kind;
                int laneCount = HighwayLane.LaneCountOf(kind, session.IsFiveLaneDrums);
                noteRows = laneCount;
                var whites = kind == ChartInstrumentKind.SixFret
                    ? game.Notes.Where(n => n.Lane is >= 0 and < 3).Select(n => (n.Tick, n.Lane)).ToHashSet()
                    : [];
                foreach (var note in game.Notes)
                {
                    double start = session.AudioAt(note.Tick);
                    double end = note.Length > 0 ? session.AudioAt(note.Tick + note.Length) : start;
                    int row = note.Lane == ChartNote.OpenLane ? -1 : HighwayPalette.Column(kind, laneCount, note.Lane);
                    var color = replaceStarPower
                        && (replacedPhrases is null
                            || replacedPhrases.Any(p => p.Kind == PhraseKind.StarPower && p.Covers(note.Tick)))
                        ? HighwayPalette.NoteColor(kind, laneCount, note with { Flags = note.Flags & ~NoteFlags.StarPower })
                        : HighwayPalette.NoteColor(kind, laneCount, note);
                    bool top = HighwayPalette.IsBlackFret(kind, note.Lane) && whites.Contains((note.Tick, note.Lane - 3));
                    notes.Add(new OverviewNote(start, end, row, color == HighwayPalette.BlackFret ? HighwayPalette.BlackFretOutline : color, top));
                }

                rows = PhraseRail.RowsOf(part.Kind);
                var starPower = new RailKind(PhraseKind.StarPower);
                foreach (var phrase in game.StarPower)
                {
                    bool replaced = replacedPhrases is not null
                        && replacedPhrases.Any(p => p.Kind == PhraseKind.StarPower && p.Tick == phrase.Tick && p.Length == phrase.Length);
                    phrases.Add(new OverviewPhrase(session.AudioAt(phrase.Tick), session.AudioAt(phrase.Tick + phrase.Length), starPower, replaced));
                }
                foreach (var phrase in part.Phrases)
                {
                    RailKind row = PhraseRail.RowOf(part.Kind, phrase.Kind);
                    if (phrase.Kind != PhraseKind.StarPower && rows.Contains(row))
                    {
                        bool replaced = replacedPhrases is not null
                            && replacedPhrases.Any(p => p.Kind == phrase.Kind && p.Tick == phrase.Tick && p.Length == phrase.Length);
                        phrases.Add(new OverviewPhrase(session.AudioAt(phrase.Tick), session.AudioAt(phrase.Tick + phrase.Length), row, replaced));
                    }
                }

                if (rows.Contains(RailKind.DiscoFlip))
                {
                    foreach (var range in session.DiscoFlipRangesOf(track))
                        phrases.Add(new OverviewPhrase(session.AudioAt(range.Tick), range.EndTick is { } end ? session.AudioAt(end) : length, RailKind.DiscoFlip));
                }
            }

            var ghosts = proposals ?? [];

            var synced = new List<(double, double, bool, bool)>();
            if (session.Sync is { Span: var (from, to) } sync)
            {
                synced.Add((session.AudioAt(from), session.AudioAt(to), true, sync.IsUnmatched));
                foreach (long start in sync.Matches)
                    synced.Add((session.AudioAt(start), session.AudioAt(start + to - from), false, false));
            }

            return new OverviewData(sections, tempoChanges, noteRows, notes, rows, phrases, ghosts, synced);
        }
    }
}
