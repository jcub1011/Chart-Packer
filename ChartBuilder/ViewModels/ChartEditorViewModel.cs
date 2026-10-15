using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using ChartBuilder.Resources;
using ChartBuilder.Services;
using ChartShared.Audio.Synthesis;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charting.Layout;
using ChartShared.Core.Charts;
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

    /// <summary>What a generated fill must end on, as Generate fills' options offer it.</summary>
    public sealed record FillEndChoice(FillEnd End, string Label);

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

        // How many columns the overview's note density has across the song.
        private const int DensityColumns = 600;

        // Tap tempo's message, which each tap updates.
        private const string TapTempoMessageKey = "tap-tempo";

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

        // The frame the playhead was last followed in.
        private TimeSpan? _followed;

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
            FillEndChoices = [new FillEndChoice(FillEnd.Crash, Strings.GenerateFillsEndCrash), new FillEndChoice(FillEnd.AnyNote, Strings.GenerateFillsEndAnyNote)];
            SelectedFillEnd = FillEndChoices[0];
            SelectedRecordMode = RecordModeChoices[0];
            SelectedQuantize = SnapChoices.FirstOrDefault(c => c.Division == saved.QuantizeDivision) ?? SnapChoices.First(c => c.Division == SnapGrid.Default.Division);
            IsRecordQuantized = saved.RecordQuantize;
            Lanes = new LaneDockViewModel(layout, this);
            Properties = new PropertiesViewModel(this);
            Session.Onsets = new LaneOnsetSource(this);
            Session.VisibleTracks = () => [.. Lanes.Layout.Lanes.Select(l => l.Part.Track).OfType<ChartTrackKey>()];
            AudioAssist = new AudioAssistViewModel(this);
            Preview = new PreviewViewModel(this, settings);
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

        public bool IsSustainTool
        {
            get => Session.Tool == EditorTool.Sustain;
            set => SetTool(EditorTool.Sustain, value);
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
        /// Whether the Sync tool's button is down: the tool is picked, or a synchronized region is drawn. Pressed while a
        /// region is drawn, it takes the region away rather than picking the tool.
        /// </summary>
        public bool IsSyncTool
        {
            get => Session.Tool == EditorTool.Sync || Session.Sync.IsActive;
            set
            {
                if (Session.Sync.IsActive)
                {
                    Session.Sync.Clear();
                    if (Session.Tool == EditorTool.Sync)
                        Session.Tool = EditorTool.Select;
                    OnSessionState(this, EventArgs.Empty);
                }
                else
                {
                    SetTool(EditorTool.Sync, value);
                }
            }
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

        /// <summary>Whether Generate fills can run (charter-spec C6 item 5): the lane in focus is a drums part with notes.</summary>
        public bool CanGenerateFills =>
            Session.Focused is { } key && ChartInstruments.KindOf(key.Instrument) == ChartInstrumentKind.Drums && Session.Chart.PartOf(key.Instrument) is { HasNotes: true };

        /// <summary>
        /// How many measures apart generated fills start, at the least. Generate fills' options are kept while the editor is
        /// open, so the next run starts from the last one's.
        /// </summary>
        [ObservableProperty]
        public partial decimal? FillSpacing { get; set; } = FillOptions.Default.SpacingMeasures;

        /// <summary>How many measures a generated fill lasts.</summary>
        [ObservableProperty]
        public partial decimal? FillLength { get; set; } = FillOptions.Default.LengthMeasures;

        public IReadOnlyList<FillEndChoice> FillEndChoices { get; }

        /// <summary>What a generated fill must end on.</summary>
        [ObservableProperty]
        public partial FillEndChoice? SelectedFillEnd { get; set; }

        /// <summary>Where Generate fills proposes: over the selection's span when there's one, else the whole part.</summary>
        public string FillScopeText => Session.Selection.Span is null ? Strings.GenerateFillsScopeWholePart : Strings.GenerateFillsScopeSelection;

        /// <summary>Whether a run of Generate fills is going on, with its proposals shown as ghosts to keep or drop.</summary>
        public bool IsFillRunActive => Session.Fills.IsActive;

        /// <summary>How many of the run's proposals are kept, of how many.</summary>
        public string FillRunText => string.Format(CultureInfo.CurrentCulture, Strings.FillRunFormat, Session.Fills.KeptCount, Session.Fills.Proposals.Count);

        /// <summary>What the bottom timeline shows of the whole song for the lane in focus (charter-spec C3 item 2).</summary>
        [ObservableProperty]
        public partial OverviewData? Overview { get; private set; }

        /// <summary>Where the span of the song the lanes show starts, in seconds of audio, as the overview marks it.</summary>
        [ObservableProperty]
        public partial double VisibleStart { get; private set; }

        /// <summary>Where the span of the song the lanes show ends, in seconds of audio.</summary>
        [ObservableProperty]
        public partial double VisibleEnd { get; private set; }

        /// <summary>Takes up what may have changed in Settings: whether tempo edits keep times, and what lanes draw their audio as.</summary>
        public void ReloadSettings()
        {
            Session.KeepTimes = _settings.Settings.TempoEdits == TempoEditMode.KeepTimes;
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

        /// <summary>Plays the selection's span, then pauses (Shift+Space).</summary>
        public void PlaySelection()
        {
            if (Session.Selection.Span is not { } span)
            {
                Session.Status = Strings.NothingToPlayStatus;
                return;
            }

            Playback.PlayRegion(Session.AudioAt(span.From), Session.AudioAt(span.To));
        }

        /// <summary>Sets the loop region to the selection's span (L).</summary>
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

        /// <summary>Proposes fills with the options picked, over the selection's span or the whole part.</summary>
        [RelayCommand]
        private void GenerateFills()
        {
            var options = new FillOptions(
                (int)Math.Max(1, FillSpacing ?? FillOptions.Default.SpacingMeasures),
                (int)Math.Max(1, FillLength ?? FillOptions.Default.LengthMeasures),
                SelectedFillEnd?.End ?? FillEnd.Crash);
            Session.Fills.Start(options, Session.Selection.Span);
        }

        [RelayCommand(CanExecute = nameof(IsFillRunActive))]
        private void KeepAllFills() => Session.Fills.KeepAll();

        [RelayCommand(CanExecute = nameof(IsFillRunActive))]
        private void DropAllFills() => Session.Fills.DropAll();

        /// <summary>Adds the kept fills as one undoable step, ending the run.</summary>
        [RelayCommand(CanExecute = nameof(CanAddFills))]
        private void AddFills() => Session.Fills.Accept();

        private bool CanAddFills() => Session.Fills.KeptCount > 0;

        /// <summary>Ends the run, adding nothing (Esc).</summary>
        [RelayCommand(CanExecute = nameof(IsFillRunActive))]
        private void CancelFills() => Session.Fills.Cancel();

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

        private void OnSessionState(object? sender, EventArgs e)
        {
            OnPropertyChanged(nameof(SelectedSnap));
            OnPropertyChanged(nameof(IsSnapOn));
            OnPropertyChanged(nameof(IsSelectTool));
            OnPropertyChanged(nameof(IsPlaceTool));
            OnPropertyChanged(nameof(IsEraseTool));
            OnPropertyChanged(nameof(IsSustainTool));
            OnPropertyChanged(nameof(IsPhraseTool));
            OnPropertyChanged(nameof(IsTempoTool));
            OnPropertyChanged(nameof(IsSyncTool));
            OnPropertyChanged(nameof(IsRecording));
            OnPropertyChanged(nameof(RecordToolTip));
            RefreshFills();
            RefreshPhraseChoices();
            RefreshVisibleSpan();
            AudioAssist.Refresh();
            ScheduleRefresh();
        }

        /// <summary>Generate fills' button, and the bar over the lanes while a run goes on, after the focus, the chart or the run changes.</summary>
        private void RefreshFills()
        {
            OnPropertyChanged(nameof(CanGenerateFills));
            OnPropertyChanged(nameof(IsFillRunActive));
            OnPropertyChanged(nameof(FillRunText));
            KeepAllFillsCommand.NotifyCanExecuteChanged();
            DropAllFillsCommand.NotifyCanExecuteChanged();
            AddFillsCommand.NotifyCanExecuteChanged();
            CancelFillsCommand.NotifyCanExecuteChanged();
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
            [.. PhraseRail.ToolKindsOf(kind, proDrums).Select(k => new PhraseKindChoice(k, PhraseKindName(k)))];

        // What the session says is why something was refused.
        private void OnStatusPosted(object? sender, string status) => Messages.Warning(status);

        private void OnChartChanged(object? sender, EditorChartChangedEventArgs e)
        {
            if (e.IsPreview)
                return;
            ScheduleRefresh();
            Properties.Refresh();
            RefreshPhraseChoices();
            OnPropertyChanged(nameof(CanGenerateFills));
            foreach (var lane in Lanes.All)
                lane.RefreshDifficulties();
        }

        private void OnSelectionChanged(object? sender, EventArgs e)
        {
            Properties.Refresh();
            OnPropertyChanged(nameof(FillScopeText));
            AudioAssist.Refresh();
        }

        // The delay or the song's settings moved every note against the audio, or changed how they're read.
        private void OnSongSettingsChanged(object? sender, EventArgs e)
        {
            ScheduleRefresh();
            Properties.Refresh();
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
            Playback.SetHits([.. shown.SelectMany(HitsOf)]);
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

        private OverviewData? OverviewOf(ChartTrackKey? key)
        {
            double length = Playback.LengthSeconds;
            if (!(length > 0))
                return null;

            var chart = Session.Chart;
            var sections = new List<OverviewSection>();
            foreach (var e in chart.Events)
            {
                if (SectionEvents.NameOf(e.Text) is { } name)
                    sections.Add(new OverviewSection(Session.AudioAt(e.Tick), name));
            }

            var density = new float[DensityColumns];
            var starPower = new List<(double, double)>();
            var solos = new List<(double, double)>();
            if (key is { } track && chart.PartOf(track.Instrument) is { } part)
            {
                var game = Session.GameTrack(track);
                foreach (var note in game.Notes)
                {
                    int column = (int)(Session.AudioAt(note.Tick) / length * DensityColumns);
                    if (column >= 0 && column < DensityColumns)
                        density[column]++;
                }

                float most = density.Max();
                if (most > 0)
                {
                    for (int i = 0; i < density.Length; i++)
                        density[i] /= most;
                }

                foreach (var phrase in game.StarPower)
                    starPower.Add((Session.AudioAt(phrase.Tick), Session.AudioAt(phrase.Tick + phrase.Length)));
                foreach (var phrase in part.Phrases.Where(p => p.Kind == PhraseKind.Solo))
                    solos.Add((Session.AudioAt(phrase.Tick), Session.AudioAt(phrase.Tick + phrase.Length)));
            }

            var synced = new List<(double, double, bool)>();
            if (Session.Sync is { Span: var (from, to) } sync)
            {
                synced.Add((Session.AudioAt(from), Session.AudioAt(to), true));
                foreach (long start in sync.Matches)
                    synced.Add((Session.AudioAt(start), Session.AudioAt(start + to - from), false));
            }

            return new OverviewData(sections, density, starPower, solos, synced);
        }
    }

    /// <summary>A section, at its time in the audio, for the overview.</summary>
    public sealed record OverviewSection(double Seconds, string Name);

    /// <summary>
    /// What the bottom timeline shows of the whole song (charter-spec C3 item 2): sections, the note density of the lane in
    /// focus, from 0 to 1 in columns across the song, its star power and solos, and the synchronized region and the
    /// passages synced with it, each as audio seconds.
    /// </summary>
    public sealed record OverviewData(
        IReadOnlyList<OverviewSection> Sections,
        IReadOnlyList<float> Density,
        IReadOnlyList<(double Start, double End)> StarPower,
        IReadOnlyList<(double Start, double End)> Solos,
        IReadOnlyList<(double Start, double End, bool IsRegion)> Synced);
}
