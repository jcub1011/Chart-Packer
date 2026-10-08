using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using ChartBuilder.Resources;
using ChartBuilder.Services;
using ChartPacker.Audio.Synthesis;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charting.Editing;
using ChartPacker.Core.Charting.Layout;
using ChartPacker.Core.Charts;
using ChartPacker.UI.Services;
using ChartPacker.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>A snap the toolbar offers.</summary>
    public sealed record SnapChoice(int Division, string Label);

    /// <summary>A kind of phrase the toolbar's picker offers the Phrase tool.</summary>
    public sealed record PhraseKindChoice(PhraseKind Kind, string Label);

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

        // The Phrase tool's kinds, for 5-fret and GHL, and for drums.
        private static readonly IReadOnlyList<PhraseKindChoice> FretPhraseChoices = PhraseChoicesOf(ChartInstrumentKind.FiveFret);
        private static readonly IReadOnlyList<PhraseKindChoice> DrumPhraseChoices = PhraseChoicesOf(ChartInstrumentKind.Drums);

        private readonly BuilderSettingsService _settings;
        private readonly IConfirmationService _confirmations;
        private readonly DispatcherTimer _refresh;
        private readonly DispatcherTimer _tapEnd;
        private readonly TapTempo _taps = new();
        private double _firstTap;
        private bool _syncing;
        private IReadOnlyList<PhraseKindChoice>? _phraseChoices;

        /// <param name="messages">Where what the editor has to say goes, for the window's message bar.</param>
        public ChartEditorViewModel(EditorSong song, ChartUndoStack undo, PlaybackViewModel playback, BuilderSettingsService settings, IConfirmationService confirmations, EditorMessages messages, LaneDockLayout layout)
        {
            _settings = settings;
            _confirmations = confirmations;
            Song = song;
            Playback = playback;
            Messages = messages;
            var saved = settings.Settings;
            Session = new EditorSession(undo, song.Delay, song.EffectivePairs())
            {
                Snap = SnapGrid.Of(saved.DefaultSnapDivision),
                KeepTimes = saved.TempoEdits == TempoEditMode.KeepTimes,
            };
            SnapChoices = [.. SnapGrid.CommonDivisions.Select(d => new SnapChoice(d, BuilderSettingsCatalog.SnapLabel(d)))];
            Lanes = new LaneDockViewModel(layout, this);
            Properties = new PropertiesViewModel(this);

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
            var lane = Lanes.All.FirstOrDefault(l => l.Track is { } track && track == Session.Focused)
                ?? Lanes.All.FirstOrDefault(l => l.HasCanvas);
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
        /// The kinds of phrase the Phrase tool offers for the part in focus (charter-spec C8): star power, solos and big
        /// rock endings on 5-fret and GHL; star power, solos, fills, rolls and trills on drums, whose big rock ending is a
        /// fill after <c>[coda]</c>.
        /// </summary>
        public IReadOnlyList<PhraseKindChoice> PhraseKindChoices =>
            Session.Focused is { } key && ChartInstruments.KindOf(key.Instrument) == ChartInstrumentKind.Drums ? DrumPhraseChoices : FretPhraseChoices;

        /// <summary>The kind the Phrase tool draws. Picking one picks the Phrase tool too.</summary>
        public PhraseKindChoice? SelectedPhraseKind
        {
            get
            {
                var kind = Session.Focused is { } key ? PhraseRail.RowOf(ChartInstruments.KindOf(key.Instrument), Session.PhraseKind) : Session.PhraseKind;
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

        /// <summary>What the bottom timeline shows of the whole song for the lane in focus (charter-spec C3 item 2).</summary>
        [ObservableProperty]
        public partial OverviewData? Overview { get; private set; }

        /// <summary>Where the span of the song the lanes show starts, in seconds of audio, as the overview marks it.</summary>
        [ObservableProperty]
        public partial double VisibleStart { get; private set; }

        /// <summary>Where the span of the song the lanes show ends, in seconds of audio.</summary>
        [ObservableProperty]
        public partial double VisibleEnd { get; private set; }

        /// <summary>Takes up what may have changed in Settings: whether tempo edits keep times.</summary>
        public void ReloadSettings() => Session.KeepTimes = _settings.Settings.TempoEdits == TempoEditMode.KeepTimes;

        /// <summary>Follows the playhead, once a frame while it plays: the cursor moves with it, and the view with the cursor.</summary>
        public void FollowPlayback()
        {
            if (Playback.IsPlaying)
                Session.Viewport.Follow(Playback.Clock().TotalSeconds);
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
        public async Task ApplyTempoEditAsync(Func<BlockedAnchor, TempoEdit> make)
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
                    return;
            }

            if (edit.IsRefused)
                Session.Status = edit.Refusal;
            else
                Session.Execute(edit.Command!);
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
            Session.Detach();
        }

        private void SetTool(EditorTool tool, bool on)
        {
            // A tool's button pressed again stays down: one tool is always picked.
            if (on)
                Session.Tool = tool;
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

            // The picker's list changes only with the kind of part in focus, so it isn't rebuilt on every change.
            if (!ReferenceEquals(_phraseChoices, PhraseKindChoices))
            {
                _phraseChoices = PhraseKindChoices;
                OnPropertyChanged(nameof(PhraseKindChoices));
            }

            OnPropertyChanged(nameof(SelectedPhraseKind));
            RefreshVisibleSpan();
            ScheduleRefresh();
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

        private static IReadOnlyList<PhraseKindChoice> PhraseChoicesOf(ChartInstrumentKind kind) =>
            [.. PhraseRail.KindsOf(kind).Select(k => new PhraseKindChoice(k, PhraseKindName(k)))];

        // What the session says is why something was refused.
        private void OnStatusPosted(object? sender, string status) => Messages.Warning(status);

        private void OnChartChanged(object? sender, EditorChartChangedEventArgs e)
        {
            if (e.IsPreview)
                return;
            ScheduleRefresh();
            Properties.Refresh();
            foreach (var lane in Lanes.All)
                lane.RefreshDifficulties();
        }

        private void OnSelectionChanged(object? sender, EventArgs e) => Properties.Refresh();

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
                if (SectionName(e.Text) is { } name)
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

            return new OverviewData(sections, density, starPower, solos);
        }

        /// <summary>A section's name from its text event (<c>[section Verse 1]</c> or <c>[prc_verse_1]</c>), or null when it isn't one.</summary>
        internal static string? SectionName(string text)
        {
            string trimmed = text.Trim().TrimStart('[').TrimEnd(']');
            if (trimmed.StartsWith("section ", StringComparison.OrdinalIgnoreCase))
                return trimmed["section ".Length..].Trim();
            if (trimmed.StartsWith("prc_", StringComparison.OrdinalIgnoreCase))
                return trimmed["prc_".Length..].Replace('_', ' ');
            return null;
        }
    }

    /// <summary>A section, at its time in the audio, for the overview.</summary>
    public sealed record OverviewSection(double Seconds, string Name);

    /// <summary>
    /// What the bottom timeline shows of the whole song (charter-spec C3 item 2): sections, the note density of the lane in
    /// focus, from 0 to 1 in columns across the song, and its star power and solos, each as audio seconds.
    /// </summary>
    public sealed record OverviewData(
        IReadOnlyList<OverviewSection> Sections,
        IReadOnlyList<float> Density,
        IReadOnlyList<(double Start, double End)> StarPower,
        IReadOnlyList<(double Start, double End)> Solos);
}
