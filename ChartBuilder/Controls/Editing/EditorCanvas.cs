using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ChartBuilder.Resources;
using ChartBuilder.ViewModels;
using ChartShared.Audio.Analysis;
using ChartShared.Core.Charting.Analysis;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charting.Layout;
using ChartShared.Core.Charts;
using ChartShared.UI.Controls;
using ChartShared.UI.Controls.Highway;

namespace ChartBuilder.Controls.Editing
{
    /// <summary>
    /// A lane's editor canvas (charter-spec §4.5, C3): the part's notes at one difficulty over the beat lines and the
    /// audio, drawn along a time axis that runs up a highway or across a timeline. Both views draw the same things through
    /// a <see cref="LaneProjection"/>, and pass the pointer and the keys to the shared <see cref="EditorTools"/>, so every
    /// tool behaves the same in both (AC C3.2). Its data context is the lane's <see cref="EditorLaneViewModel"/>.
    /// </summary>
    /// <remarks>
    /// <para>The strike line stays put, a quarter of the way from the strike end, on the cursor: scrolling moves the
    /// song past it a grid step a notch, and so moves the cursor through the song, as does playing. How far the lane shows beyond it is the
    /// lane's look-ahead, which Ctrl+wheel changes.</para>
    /// <para>Only what's near the screen is drawn: the notes between the ticks at a page's ends, found by binary search
    /// (§11). The canvas is drawn in layers (<see cref="Layer"/>). What moves with the song, the notes and the beats, audio,
    /// labels and phrase rail under them, is drawn as a page reaching past the screen's edges (<see cref="LanePage"/>), and
    /// as the cursor moves, as it does every frame the song plays, the page is only moved, until the view nears its end.
    /// The strike line's glow, and what follows the pointer, are drawn again as they change.</para>
    /// </remarks>
    public abstract partial class EditorCanvas : Control
    {
        public static readonly StyledProperty<IBrush?> AccentBrushProperty =
            AvaloniaProperty.Register<EditorCanvas, IBrush?>(nameof(AccentBrush));

        public static readonly StyledProperty<IBrush?> LabelBrushProperty =
            AvaloniaProperty.Register<EditorCanvas, IBrush?>(nameof(LabelBrush));

        public static readonly StyledProperty<bool> WheelUpIsLaterProperty =
            AvaloniaProperty.Register<EditorCanvas, bool>(nameof(WheelUpIsLater), true);

        // How near a note's start, or a phrase bar's end in the rail, the pointer hits it, in pixels.
        private const double HitPixels = 7;

        // How far the pointer can move between a right button's press and release, in pixels, and still be a click.
        private const double ClickSlop = 4;

        // A short sustain's dashed outline's dashes, and the gaps between them, in pixels.
        private const double ShortTailDash = 2;

        // The phrase rail's rows, across the view, in pixels: each one's thickness, the gap between them, and the margin
        // either side of the rail.
        private const double PhraseRowSize = 6;
        private const double PhraseRowGap = 2;
        private const double PhraseRailMargin = 3;

        private static readonly IBrush LanesBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xF0, 0x08, 0x08, 0x0F));
        private static readonly IBrush RailBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xFF, 0x12, 0x13, 0x1B));
        private static readonly IBrush AlternateRowBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF));
        // Solid and fainter rather than dashed: drawn once a grid step across every lane, every frame, and a dashed line is
        // dashed afresh each time it's drawn.
        private static readonly IPen GridPen = new ImmutablePen(Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF).ToUInt32(), 1);
        private static readonly IPen TempoPen = new ImmutablePen(Color.FromArgb(0xB0, 0xFF, 0xB0, 0x40).ToUInt32(), 1.5);
        private static readonly IBrush TempoBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x40));
        private static readonly IBrush SpanBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x26, 0x6E, 0x9C, 0xFF));
        // The loop region (charter-spec C10 item 2): a light green box across the lanes, whose ends a drag resizes and whose
        // body a drag moves with the Loop tool; and where a press of the tool would start drawing one.
        private static readonly IBrush LoopBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x22, 0x3C, 0xD2, 0x78));
        private static readonly IPen LoopPen = new ImmutablePen(Color.FromArgb(0xD0, 0x3C, 0xD2, 0x78).ToUInt32(), 1.5);
        private static readonly IBrush LoopLineBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xD0, 0x3C, 0xD2, 0x78));
        private static readonly IBrush BoxBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x30, 0x6E, 0x9C, 0xFF));
        private static readonly IPen BoxPen = new ImmutablePen(Color.FromArgb(0xC0, 0x6E, 0x9C, 0xFF).ToUInt32(), 1);

        // The synchronized region, outlined, and the passages synced with it, in an orange apart from the tempo markers'.
        private static readonly IBrush SyncedBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x38, 0xFF, 0x7A, 0x1A));
        private static readonly IBrush SyncRegionBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x20, 0xFF, 0x7A, 0x1A));
        private static readonly IPen SyncRegionPen = new ImmutablePen(Color.FromArgb(0xD0, 0xFF, 0x7A, 0x1A).ToUInt32(), 1.5);

        // Where a press of the Sync tool would start drawing a region, on the cursor: the solid orange of its outline.
        private static readonly IBrush SyncLineBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0x7A, 0x1A));

        // A region nothing matched, hashed in yellow instead, and the small X at its top right that clears it.
        private static readonly IBrush SyncUnmatchedBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x14, 0xE6, 0xC8, 0x00));
        private static readonly IPen SyncUnmatchedPen = new ImmutablePen(Color.FromArgb(0xD0, 0xE6, 0xC8, 0x00).ToUInt32(), 1.5);
        private static readonly IPen SyncHashPen = new ImmutablePen(Color.FromArgb(0x8C, 0xE6, 0xC8, 0x00).ToUInt32(), 1.5);
        private static readonly IBrush SyncCloseBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xE0, 0x20, 0x22, 0x2E));
        private static readonly IPen SyncClosePen = new ImmutablePen(Color.FromRgb(0xF8, 0x71, 0x71).ToUInt32(), 1.5);

        // How far apart a region nothing matched's hash lines are drawn, in pixels, and how long a side of its X is.
        private const double SyncHashGap = 7;
        private const double CloseSize = 13;
        private static readonly IBrush WaveformPeakBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x24, 0xC8, 0xD2, 0xFF));
        private static readonly IBrush WaveformLoudBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x48, 0xC8, 0xD2, 0xFF));
        private static readonly IBrush FullWaveformPeakBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x70, 0xC8, 0xD2, 0xFF));
        private static readonly IBrush FullWaveformLoudBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xC0, 0xC8, 0xD2, 0xFF));
        private static readonly IPen ChannelPen = new ImmutablePen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF).ToUInt32(), 1);

        // How strongly a spectrogram behind the lanes shows through, so the notes stay clear over it.
        private const double SpectrogramBehindOpacity = 0.45;

        // The onsets' ticks, by band (charter-spec C14 item 1): the whole spectrum's, then the kick's, snare's and cymbals'
        // in the colours of the pads they likely belong to; each faint, middling or strong by how far it stands out.
        private static readonly IBrush[][] OnsetBrushes =
        [
            OnsetShades(Color.FromRgb(0x9C, 0xE0, 0xD4)),
            OnsetShades(HighwayPalette.Kick),
            OnsetShades(HighwayPalette.Red),
            OnsetShades(HighwayPalette.Yellow),
        ];

        // A proposed tempo map's ghost (charter-spec C14 item 4): dashed beats, measure starts solid, a handle for each beat
        // in the margin (filled once dragged into place), and the stretches too unsure to trust shaded. Find first
        // downbeat's guess is a line of its own (C14 item 5).
        private static readonly Color ProposalColor = Color.FromRgb(0x7F, 0xD4, 0xFF);
        private static readonly IPen ProposalBeatPen = new ImmutablePen(HighwayPalette.WithAlpha(ProposalColor, 0x90).ToUInt32(), 1, new ImmutableDashStyle([4, 3], 0));
        private static readonly IPen ProposalMeasurePen = new ImmutablePen(HighwayPalette.WithAlpha(ProposalColor, 0xD8).ToUInt32(), 2);
        private static readonly IPen ProposalHandlePen = new ImmutablePen(ProposalColor.ToUInt32(), 1.2);
        private static readonly IBrush ProposalHandleBrush = new ImmutableSolidColorBrush(ProposalColor);
        private static readonly IBrush ProposalLabelBrush = new ImmutableSolidColorBrush(ProposalColor);
        private static readonly IBrush UnsureBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0x6E, 0x7A));
        private static readonly IBrush UnsureLabelBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x94));
        private static readonly IPen DownbeatPen = new ImmutablePen(HighwayPalette.WithAlpha(HighwayPalette.Yellow, 0xE0).ToUInt32(), 2, new ImmutableDashStyle([6, 3], 0));
        private static readonly IBrush DownbeatLabelBrush = new ImmutableSolidColorBrush(HighwayPalette.Yellow);
        private static readonly Geometry Diamond = new PolylineGeometry([new Point(0, -5), new Point(5, 0), new Point(0, 5), new Point(-5, 0)], true);

        // How near a proposed beat's handle the pointer must be to grab it, in pixels.
        private const double HandlePixels = 6;

        private static IBrush[] OnsetShades(Color color) =>
            [.. new byte[] { 0x60, 0xA0, 0xE8 }.Select(alpha => (IBrush)new ImmutableSolidColorBrush(HighwayPalette.WithAlpha(color, alpha)))];
        private static readonly IBrush BadgeBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xE0, 0x20, 0x22, 0x2E));
        private static readonly IBrush DefaultLabelBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF));
        private static readonly IBrush DefaultAccentBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x7C, 0xB4, 0xFF));
        private static readonly IBrush StrikeBandBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x90, 0x00, 0x00, 0x00));
        private static readonly Color SoloColor = PhraseColors.Solo;
        private static readonly IBrush SoloLabelBrush = new ImmutableSolidColorBrush(SoloColor);

        private static readonly Color DiscoFlipColor = PhraseColors.DiscoFlip;
        private static readonly Color SectionColor = Color.FromRgb(0x5E, 0xEA, 0xD4);
        private static readonly IPen SectionPen = new ImmutablePen(HighwayPalette.WithAlpha(SectionColor, 0x70).ToUInt32(), 1.5);
        private static readonly IBrush SectionBrush = new ImmutableSolidColorBrush(SectionColor);

        // What a click of the erase tool would take away, in red over it.
        private static readonly Color EraseColor = Color.FromRgb(0xF8, 0x71, 0x71);
        private static readonly IBrush EraseBrush = new ImmutableSolidColorBrush(HighwayPalette.WithAlpha(EraseColor, 0x70));
        private static readonly IPen ErasePen = new ImmutablePen(EraseColor.ToUInt32(), 1.5);

        private readonly GemPainter _gems = new();
        private readonly NotePainter _notePainter;
        private readonly NoteDecorations _decorations;
        private readonly List<NoteGlyph> _glyphs = [];
        private readonly List<BandGlyph> _bands = [];
        private readonly List<EditableNote> _glyphNotes = [];
        private readonly LabelCache _labels = new();
        private readonly List<string> _measureNumbers = [];

        // What the rail's text is made of, each worked out once rather than each frame it's drawn: the beat lines in view, the sections'
        // names by their events' text, and the tempo markers' and time signatures' labels. Let go when there are too many, as after
        // a long edit session.
        private const int MaxRailTexts = 4096;
        private readonly List<BeatLine> _beatLines = [];
        private readonly Dictionary<string, string?> _sectionNames = [];
        private readonly Dictionary<(long Tick, long MicrosecondsPerQuarter, bool Anchored, int Numerator, int Denominator), string> _tempoTexts = [];
        private readonly Dictionary<(int Numerator, int Denominator), string> _signatureTexts = [];

        // The callbacks for each frame while the song plays, and for the pages drawn after one changes: made once, not each time.
        private readonly Action<TimeSpan> _onFrame;
        private readonly Action _redrawStalePages;

        // Where a second of audio is along the time axis, for the waveform's and spectrogram's painters, of the projection they're given.
        private readonly AlongOfProjection _alongOf = new();

        // How brightly each column is lit as the song plays through the strike line.
        private double[] _hits = [];
        private EditorLaneViewModel? _lane;
        private ChartEditorViewModel? _editor;
        private bool _isFrameRequested;
        private bool _isRedrawPosted;
        private bool _isPressed;

        // The button whose press started the gesture going on, where it was pressed, and whether it has been dragged since:
        // a right click that isn't dragged opens the note menu.
        private MouseButton _pressButton;
        private Point _pressedAt;
        private bool _pressDragged;

        // The modifier keys held as the pointer last moved or a key last went down or up: with Alt, the place tool's ghost
        // is an open note, or a kick, or a fret while the place tool places open notes.
        private KeyModifiers _keys;

        // The proposed beat being dragged by its handle, if one is.
        private int? _proposalDrag;
        private Point? _hover;

        // The gestures that move through the song and zoom into it, which a highway shares with the editor's lanes: the wheel,
        // and a drag with Shift and the middle button, as the left and the others are the tools'.
        private readonly ViewportGestures _gestures;
        private Point? _boxFrom;
        private Point _boxTo;
        // The box's press anchor in audio seconds, so the box tracks the song when the view scrolls or zooms mid-drag.
        private double? _boxFromSeconds;
        private ChartPoint _lastPoint;
        private VocalPoint _lastVocalPoint;

        // The longest note of the track drawn, so the notes held into view from before it are found; worked out again after an edit.
        private (ChartTrackKey Track, long Length)? _longest;
        private (ChartTrackKey Track, long Length)? _ghostLongest;

        // What's drawn every frame that would otherwise be made every frame: the pens in the accent, as of which accent,
        // a short sustain's outline's pen by colour, and the chords whose forcing marks are drawn.
        private (IBrush Accent, IPen Selected, IPen Cursor, IPen Focus)? _accentPens;
        private readonly Dictionary<Color, IPen> _shortTailPens = [];
        private readonly HashSet<long> _flagged = [];

        // The ticks of the measures' first beats, for numbering them; kept for the tempo map they're for.
        private TempoMap? _measuresOf;
        private List<long> _measures = [];

        // The pointer's shapes over the phrase rail, made once each: a resize arrow over a bar's end, a hand over its body.
        private static readonly Dictionary<StandardCursorType, Cursor> RailCursors = [];

        private StandardCursorType? _cursorType;

        // The phrases of the lane's part that reach into the ticks shown, found once a frame for each of the passes that
        // draw them.
        private readonly List<Phrase> _phrasesShown = [];

        // How far a page reaches past the screen, in screens: a little back, and most of it the way the view is moving.
        private const double PageBehind = 0.25;
        private const double PageAhead = 0.75;


        // How near a page's end the screen may come, in pixels, before it's drawn again around the screen: room for what's
        // drawn around a moment rather than at it, a gem or a measure's number, whose moment may be past the page's end.
        private const double PageMargin = 32;

        // How far beyond the page what's drawn on it may reach, in pixels: a long phrase's or sustain's box is cut there,
        // so the page's picture is the page's size, whatever's on it.
        private const double PagePad = 32;

        // The longest a page's picture may be along the time axis, in the screen's pixels, which some graphics cards
        // can't make pictures much bigger than; and the least a page must reach past the screen to be worth keeping.
        // A lane too long for both is drawn again every frame, as a whole, without pictures.
        private const double MaxPagePixels = 8192;
        private const double MinPageReach = 64;

        // Whether the layers that move with the song are drawn from pictures of themselves, kept while they're only moved:
        // far less to draw each frame, for a picture's worth of memory each. False to compare, or should the pictures look
        // worse than the drawing.
        private const bool CacheLayers = true;

        // The least a tilted plane's depth is taken to be foreshortened to at its near end, for how long its waveform's bars are, so a
        // steep tilt doesn't make them too coarse to show the beats.
        private const double MinWaveformDepthScale = 0.25;

        // How long a page with pictures still to come waits before it's drawn again with them.
        private static readonly TimeSpan RedrawDelay = TimeSpan.FromMilliseconds(50);

        // The canvas's layers, bottom to top; the page the song and the notes are drawn as; whether the lane is short
        // enough to have pages; which way the view last moved; and how far along the time axis a box may reach, while a
        // page is drawn.
        private readonly Layer _song;
        private readonly Layer _strike;
        private readonly Layer _notes;
        private readonly Layer _playtest;
        private readonly Layer _overlay;
        private readonly Layer[] _layers;
        private LanePage? _page;
        private bool _isPaged;
        private bool _isBackwards;
        private double? _lastStart;
        private Size _arranged;
        private (double From, double To) _alongLimit = (double.NegativeInfinity, double.PositiveInfinity);
        private TopLevel? _topLevel;
        private readonly FramePerf? _perf;
        private DispatcherTimer? _redrawTimer;

        static EditorCanvas()
        {
            FocusableProperty.OverrideDefaultValue<EditorCanvas>(true);
            ClipToBoundsProperty.OverrideDefaultValue<EditorCanvas>(true);
        }

        protected EditorCanvas()
        {
            _onFrame = OnFrame;
            _redrawStalePages = RedrawStalePages;
            _notePainter = new NotePainter(_gems);
            _upright = new UprightGemPainter(_gems);
            _uprightNotes = new NotePainter(_upright);
            _gestures = new ViewportGestures(this, new ViewportHost(this), new ScrubBinding(MouseButton.Middle, KeyModifiers.Shift), WatchesKeys);
            _decorations = new NoteDecorations(this);
            _song = new Layer(this, LayerKind.Song);
            _strike = new Layer(this, LayerKind.Strike);
            _notes = new Layer(this, LayerKind.Notes);
            _playtest = new Layer(this, LayerKind.Playtest);
            _overlay = new Layer(this, LayerKind.Overlay);
            _layers = [_song, _strike, _notes, _playtest, _overlay];
            foreach (var layer in _layers)
                VisualChildren.Add(layer);

            if (FramePerf.IsEnabled)
            {
                _perf = new FramePerf(() => $"{(_camera is null ? "flat" : "persp")} {Bounds.Width:F0}x{Bounds.Height:F0} @{TopLevel.GetTopLevel(this)?.RenderScaling ?? 1:F2}  {(_lane?.Track is { } track ? $"{track.Instrument} {track.Difficulty}" : "-")}");
                _perf.Rolled += () => _overlay.Redraw();
            }
        }

        /// <summary>The cursor, the selection and the place tool's ghost.</summary>
        public IBrush? AccentBrush
        {
            get => GetValue(AccentBrushProperty);
            set => SetValue(AccentBrushProperty, value);
        }

        /// <summary>Measure numbers and tempo labels.</summary>
        public IBrush? LabelBrush
        {
            get => GetValue(LabelBrushProperty);
            set => SetValue(LabelBrushProperty, value);
        }

        /// <summary>Whether the wheel turned up moves the cursor later, as up the highway; otherwise turned down does.</summary>
        public bool WheelUpIsLater
        {
            get => GetValue(WheelUpIsLaterProperty);
            set => SetValue(WheelUpIsLaterProperty, value);
        }

        /// <summary>How the lane draws the song.</summary>
        protected abstract LaneView View { get; }

        private EditorSession? Session => _editor?.Session;

        /// <summary>
        /// Where the time axis and the lanes are, for a canvas of <paramref name="size"/> showing as far beyond the strike line
        /// as <paramref name="length"/> says, with room kept for a phrase rail of <paramref name="phraseRows"/> rows.
        /// </summary>
        protected abstract LaneProjection Project(Size size, LaneSlots slots, EditorViewport viewport, HighwayLength length, int phraseRows);

        /// <summary>How long the time axis is, in pixels, for a canvas of <paramref name="size"/>: what <see cref="Project"/> gives it.</summary>
        protected abstract double TimeAxisLength(Size size);

        // The areas below run the length of the projection's time axis, rather than the canvas's, so a page's areas run the
        // length of the page.

        /// <summary>Where the margin is that measure numbers, tempo markers and section names are drawn in: the left rail of a highway, the ruler along a timeline's top.</summary>
        protected abstract Rect LabelsArea(Size size, LaneProjection projection);

        /// <summary>The room the lane's audio takes beside the lanes, and how many bands of onsets it shows (charter-spec C12–C14).</summary>
        protected readonly record struct LaneExtras(double BesideSize, int OnsetBands);

        /// <summary>How thick each band of onsets is across the view, in pixels.</summary>
        protected const double OnsetBandSize = 6;

        /// <summary>The lane's extras, as the layout being worked out now has them.</summary>
        protected LaneExtras Extras { get; private set; }

        /// <summary>How thick the strip of onsets is across the view, in pixels.</summary>
        protected double OnsetStripSize => Extras.OnsetBands * OnsetBandSize;

        /// <summary>How wide the lane's audio is when it's drawn beside the lanes, for a canvas of <paramref name="size"/>.</summary>
        protected abstract double BesideSizeFor(Size size);

        /// <summary>Where the lane's audio is drawn faintly behind the notes: a highway's lanes, a timeline's rows.</summary>
        protected abstract Rect BehindArea(Size size, LaneProjection projection);

        /// <summary>Where a timeline's strip of the audio is, always drawn at full strength; nothing for a highway.</summary>
        protected abstract Rect StripArea(Size size, LaneProjection projection);

        /// <summary>Where the lane's audio is drawn at full strength beside the lanes: a highway's column, a timeline's strip.</summary>
        protected abstract Rect BesideArea(Size size, LaneProjection projection, int phraseRows);

        /// <summary>Where the onsets are drawn: beside a highway's lanes, under a timeline's rows (charter-spec C14 item 1).</summary>
        protected abstract Rect OnsetArea(Size size, LaneProjection projection);

        /// <summary>
        /// Where the phrase rail of <paramref name="phraseRows"/> rows starts across the view, in pixels (charter-spec C8):
        /// right of a highway's forcing marks, above a timeline's. Its rows follow on from there, in
        /// <see cref="PhraseRail.KindsOf"/>' order.
        /// </summary>
        protected abstract double PhraseRailFrom(LaneProjection projection, int phraseRows);

        /// <summary>
        /// Where the time axis and a vocal part's pitch rows are, for a canvas of <paramref name="size"/>; null for a view
        /// that doesn't draw vocals, which only the timeline does.
        /// </summary>
        protected virtual LaneProjection? ProjectVocals(Size size, LaneSlots slots, EditorViewport viewport, HighwayLength length) => null;

        /// <summary>Where a vocal part's lyrics are drawn, under its pitch rows.</summary>
        protected virtual Rect LyricsArea(Size size, LaneProjection projection) => default;

        /// <summary>Where a vocal part's phrases are drawn as bars to select and drag, above its pitch rows.</summary>
        protected virtual Rect VocalPhraseArea(Size size, LaneProjection projection) => default;

        /// <summary>How thick the phrase rail of <paramref name="phraseRows"/> rows is across the view, in pixels.</summary>
        protected static double PhraseRailSize(int phraseRows) =>
            phraseRows == 0 ? 0 : (phraseRows * PhraseRowSize) + ((phraseRows - 1) * PhraseRowGap) + (2 * PhraseRailMargin);

        /// <summary>The point <paramref name="along"/> the time axis and <paramref name="across"/> the lanes.</summary>
        protected Point At(double along, double across) => View == LaneView.Highway ? new Point(across, along) : new Point(along, across);

        /// <summary>The rectangle between two points along the time axis and two across the lanes.</summary>
        protected Rect Box(double along1, double along2, double across1, double across2)
        {
            var a = At(Limited(Math.Min(along1, along2)), Math.Min(across1, across2));
            var b = At(Limited(Math.Max(along1, along2)), Math.Max(across1, across2));
            return new Rect(a, b);
        }

        /// <summary><paramref name="along"/>, cut to the page being drawn, so nothing drawn on it reaches far past it.</summary>
        private double Limited(double along) => Math.Clamp(along, _alongLimit.From, _alongLimit.To);

        /// <summary>How far along the time axis <paramref name="point"/> is.</summary>
        private double AlongOf(Point point) => View == LaneView.Highway ? point.Y : point.X;

        /// <summary>How far across the lanes <paramref name="point"/> is.</summary>
        private double AcrossOf(Point point) => View == LaneView.Highway ? point.X : point.Y;

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            Attach(DataContext as EditorLaneViewModel);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _perf?.Probe(ElementComposition.GetElementVisual(this)?.Compositor);
            Attach(DataContext as EditorLaneViewModel);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            Detach();

            // The perspective labels' page goes with the canvas, rather than waiting on the collector; attached again, it's
            // filled again as they're drawn.
            _pictures.Dispose();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsVisibleProperty)
            {
                // Hidden, a lane isn't kept up with the cursor, and lets go of its pictures.
                UpdatePaging();
                if (IsVisible)
                {
                    InvalidateLayers();
                    RequestFrame();
                }
            }
            else if (change.Property == AccentBrushProperty || change.Property == LabelBrushProperty)
            {
                InvalidateLayers();
            }
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            foreach (var layer in _layers)
                layer.Arrange(new Rect(finalSize));
            if (finalSize != _arranged)
            {
                _arranged = finalSize;
                // In pixels per second, how far ahead the lane shows follows how long it is.
                _lane?.SetExtent(TimeAxisLength(finalSize));
                UpdatePaging();
                InvalidateLayers();
            }

            return finalSize;
        }

        // The canvas itself is only the background, which takes the pointer: its layers draw the rest.
        public override void Render(DrawingContext context) => context.FillRectangle(RailBrush, new Rect(Bounds.Size));

        /// <summary>Draws <paramref name="layer"/>: a page of what moves with the song, or what stays put on the screen.</summary>
        private void RenderLayer(DrawingContext context, Layer layer)
        {
            layer.Page = null;
            if (Session is not { } session || _lane is not { } lane)
                return;
            if (lane.Vocal is { } vocal)
            {
                if (session.VocalsOf(vocal) is { } vocals)
                    RenderVocalLayer(context, layer, session, lane, vocal, vocals);
                return;
            }

            if (lane.Track is not { } key || session.TrackOf(key) is not { } track)
                return;

            var size = Bounds.Size;
            var slots = session.SlotsOf(key.Instrument, View);
            var part = session.Chart.PartOf(key.Instrument)!;
            var rows = PhraseRail.RowsOf(part.Kind);
            Extras = ExtrasOf(size, key);
            var screen = Project(size, slots, session.Viewport, lane.Length, rows.Count);
            // Tilted, the strike line and the notes' tails are drawn with the song, in one drawing (DrawsWithSong).
            if (DrawsWithSong(layer.Kind))
                return;

            switch (layer.Kind)
            {
                case LayerKind.Strike:
                    RenderStrike(context, session, key, track, slots, screen);
                    return;
                case LayerKind.Overlay:
                    RenderOverlay(context, session, key, track, part, rows, slots, screen);
                    return;
                case LayerKind.Playtest:
                    RenderPlaytest(context, session, key, slots, screen);
                    return;
            }

            var projection = ShowPage(layer, screen);
            try
            {
                if (layer.Kind == LayerKind.Song)
                {
                    RenderSong(context, session, key, part, rows, slots, projection, size);
                    if (_camera is not null)
                        RenderStrikeAndNotes(context, layer, session, lane, key, track, part, slots, screen, projection);
                }
                else
                    RenderNotes(context, session, lane, key, track, part, slots, projection);
            }
            finally
            {
                _alongLimit = (double.NegativeInfinity, double.PositiveInfinity);
            }
        }

        /// <summary>
        /// Whether a layer of <paramref name="kind"/> is drawn as part of the song's rather than on its own: the strike line and the notes'
        /// tails, while the lane is tilted. Then all three are drawn again for every move anyway, and each drawing has a cost of its own
        /// apart from what's in it, for every frame: what it's made of to keep, and to hand to the thread that puts it on the screen.
        /// </summary>
        private bool DrawsWithSong(LayerKind kind) => _camera is not null && kind is LayerKind.Strike or LayerKind.Notes;

        /// <summary>
        /// The strike line, then the notes' tails, over the song, in the song's drawing (see <see cref="DrawsWithSong"/>): in the order
        /// their layers are in, the strike line where it is on the screen, though the song's layer is on a page, and the tails on it.
        /// </summary>
        private void RenderStrikeAndNotes(DrawingContext context, Layer song, EditorSession session, EditorLaneViewModel lane, ChartTrackKey key, EditableTrack track, EditablePart part, LaneSlots slots, LaneProjection screen, LaneProjection projection)
        {
            // The song's layer is moved by its page's shift from the screen (Layer.Show): the strike line is moved back by as much.
            var limit = _alongLimit;
            _alongLimit = (double.NegativeInfinity, double.PositiveInfinity);
            double shift = song.Page?.ShiftOf(screen) ?? 0;
            using (context.PushTransform(shift == 0 ? Matrix.Identity : View == LaneView.Highway ? Matrix.CreateTranslation(0, -shift) : Matrix.CreateTranslation(-shift, 0)))
                RenderStrike(context, session, key, track, slots, screen);
            _alongLimit = limit;

            RenderNotes(context, session, lane, key, track, part, slots, projection);
        }

        /// <summary>
        /// Shows <paramref name="layer"/> on the page for what <paramref name="screen"/> shows, and limits what's drawn to the
        /// page, until the caller puts <see cref="_alongLimit"/> back: the projection to draw the page with.
        /// </summary>
        private LaneProjection ShowPage(Layer layer, LaneProjection screen)
        {
            var page = PageFor(screen);
            layer.Show(page, screen);
            var projection = page.Page;
            _alongLimit = (projection.TimeFrom - PagePad, projection.TimeFrom + projection.TimeLength + PagePad);
            return projection;
        }

        /// <summary>
        /// The song under the notes, across a page: the lanes, the audio, the beats and their numbers, the tempo markers, the
        /// sections and the phrase rail.
        /// </summary>
        private void RenderSong(DrawingContext context, EditorSession session, ChartTrackKey key, EditablePart part, IReadOnlyList<RailKind> rows, LaneSlots slots, LaneProjection projection, Size size)
        {
            var (fromTick, toTick) = TicksOf(session, projection);
            FillPage(context, projection);
            if (!FramePerf.IsOff(FramePerf.Part.LaneFill))
            {
                context.FillRectangle(LanesBrush, LanesOf(projection, slots));
                DrawLaneDividers(context, projection, slots);
            }

            if (!FramePerf.IsOff(FramePerf.Part.Audio))
                DrawBackdrop(context, size, projection, rows.Count);

            // The rail's rows are tinted under whatever is marked across them, such as a timeline's section lines.
            FindPhrasesShown(part, fromTick, toTick);
            if (FramePerf.IsOff(FramePerf.Part.Markings))
            {
                if (!FramePerf.IsOff(FramePerf.Part.BeatLines))
                    DrawBeats(context, session, session.TempoMap, projection, fromTick, toTick, size, RailOnPlane);
                return;
            }

            DrawPhraseRailBackground(context, rows, projection);
            DrawPhraseRanges(context, session, key, projection, fromTick, toTick);
            DrawSpans(context, session, projection);
            DrawSyncBands(context, session, key, projection);
            if (!FramePerf.IsOff(FramePerf.Part.BeatLines))
                DrawBeats(context, session, session.TempoMap, projection, fromTick, toTick, size, RailOnPlane);
            DrawOnsets(context, size, projection, key);
            DrawTempoMarkers(context, session, projection, fromTick, toTick, size, RailOnPlane);
            DrawTempoProposal(context, session, projection, size, RailOnPlane);
            DrawSections(context, session, projection, fromTick, toTick, size, RailOnPlane);

            // The phrase rail is beside the lanes, clear of the strike line's band, which is drawn over this.
            DrawPhraseRail(context, session, part, rows, projection);
            DrawDiscoFlips(context, session, key, rows, projection, fromTick, toTick);
        }

        /// <summary>
        /// The strike line's band, and while the song plays, the columns it's playing through, lit. While the lane is
        /// playtested, the frets and pads pressed are shown on it, as the games show theirs, and each hit bursts from it.
        /// </summary>
        private void RenderStrike(DrawingContext context, EditorSession session, ChartTrackKey key, EditableTrack track, LaneSlots slots, LaneProjection screen)
        {
            if (FramePerf.IsOff(FramePerf.Part.Strike))
                return;

            DrawStrikeBand(context, session, screen);
            if ((_editor?.Playback.IsPlaying != true && !FramePerf.SimulatesPlaying) || FramePerf.IsOff(FramePerf.Part.Glow))
                return;
            using (new OptionalClip(context, LanesOf(screen, slots), !FramePerf.IsOff(FramePerf.Part.PlaneClips)))
            {
                // What lights at the strike line, a target as a note is hit or a fret pressed, lies on the plane, as a glow on the highway's
                // surface, on a perspective highway too: standing up, it would look like a note that isn't there.
                var playing = PlayingOf(key);
                if (playing is not null)
                    DrawPresses(context, session, key, screen, slots);
                DrawGlow(context, session, key, track, screen, slots);
                if (playing is not null)
                    DrawHitBursts(context, session, key, playing, screen, slots);
            }
        }

        /// <summary>The notes across a page, within the lanes: the ghost difficulty's, a draft over the notes it would replace, and the solos' counts.</summary>
        private void RenderNotes(DrawingContext context, EditorSession session, EditorLaneViewModel lane, ChartTrackKey key, EditableTrack track, EditablePart part, LaneSlots slots, LaneProjection projection)
        {
            if (FramePerf.IsOff(FramePerf.Part.Tails))
                return;

            var (fromTick, toTick) = TicksOf(session, projection);
            FillPage(context, projection);
            using (new OptionalClip(context, LanesOf(projection, slots), !FramePerf.IsOff(FramePerf.Part.PlaneClips)))
            {
                // On a perspective highway the notes stand up in a layer of their own (see PaintGems): only their tails lie here.
                DrawNoteSets(context, session, lane, key, track, part, projection, slots, fromTick, toTick, _notePainter, _camera is null ? NotePass.All : NotePass.Tails);
                FindPhrasesShown(part, fromTick, toTick);
                if (_camera is null)
                    DrawSoloCounts(context, session, track, projection, fromTick, toTick, upright: false);
            }
        }

        /// <summary>
        /// The notes <paramref name="painter"/> draws, in <paramref name="pass"/>: the ghost difficulty's, and the track's own,
        /// or a draft not yet added (Reduce, record mode) over the notes it would replace, which fade behind it.
        /// </summary>
        private void DrawNoteSets(DrawingContext context, EditorSession session, EditorLaneViewModel lane, ChartTrackKey key, EditableTrack track, EditablePart part, LaneProjection projection, LaneSlots slots, double fromTick, double toTick, NotePainter painter, NotePass pass)
        {
            if (lane.Ghost is { } ghost)
            {
                var ghostKey = new ChartTrackKey(key.Instrument, ghost);
                using (context.PushOpacity(0.28))
                    DrawNotes(context, session, ghostKey, part[ghost], projection, slots, fromTick, toTick, LongestOf(ref _ghostLongest, ghostKey, part[ghost]), isGhost: true, painter, pass);
            }

            long longest = LongestOf(ref _longest, key, track);
            if (session.GhostTrack(key) is { } draft)
            {
                using (context.PushOpacity(0.3))
                    DrawNotes(context, session, key, track, projection, slots, fromTick, toTick, longest, isGhost: false, painter, pass);
                DrawNotes(context, session, key, draft, projection, slots, fromTick, toTick, LongestIn(draft), isGhost: true, painter, pass);
            }
            else
            {
                DrawNotes(context, session, key, track, projection, slots, fromTick, toTick, longest, isGhost: false, painter, pass);
            }
        }

        /// <summary>
        /// What follows the pointer and the focus: the place tool's ghost, what the erase tool would take away, where the
        /// Phrase tool would start drawing, where the Tempo tool would add a marker, where the Loop tool would start
        /// drawing a region, where the Sync tool would start drawing one, the synchronized region's notes, its X and what
        /// of it the pointer would take hold of, the loop region's X and what of it the pointer would take hold of, the
        /// cursor, a box being drawn, and the focus's edge.
        /// </summary>
        private void RenderOverlay(DrawingContext context, EditorSession session, ChartTrackKey key, EditableTrack track, EditablePart part, IReadOnlyList<RailKind> rows, LaneSlots slots, LaneProjection screen)
        {
            if (_camera is null)
                _perf?.Draw(context, new Point(4, 4));

            var lanes = LanesOf(screen, slots);
            using (context.PushClip(lanes))
            {
                // On a perspective highway the place tool's ghost and the notes marked stand up, in the layer above (PaintGemMarks).
                if (_camera is null)
                    DrawHover(context, session, key, track, screen, slots, _gems);
                DrawSyncNotes(context, session, key, track, screen, slots, upright: false);
            }

            DrawEraseHover(context, session, key, track, part, rows, screen, slots, upright: false);
            DrawPhraseHover(context, session, key, part, rows, screen, slots);
            DrawTempoHover(context, session, screen, slots);
            DrawLoopLine(context, session, screen, slots);
            DrawSyncLine(context, session, screen, slots);
            DrawSyncHover(context, session, key, screen, slots);
            DrawLoopHover(context, session, screen, slots);

            DrawCursor(context, session, screen, lanes);
            DrawBox(context, screen);

            if (IsFocused)
                context.DrawRectangle(null, AccentPens().Focus, new Rect(Bounds.Size).Deflate(0.5));
        }

        /// <summary>The box being drawn out to select notes, if one is: its press anchor sticks to the song, so scrolling or zooming mid-drag moves the box with the content the selection covers.</summary>
        private void DrawBox(DrawingContext context, LaneProjection screen)
        {
            if (_boxFrom is not { } from)
                return;
            if (_boxFromSeconds is { } seconds)
                from = At(screen.AlongOf(seconds), AcrossOf(from));
            context.DrawRectangle(BoxBrush, BoxPen, new Rect(from, _boxTo).Normalize());
        }

        /// <summary>
        /// Nothing, across all of a page and a little beyond, so the page's picture is the same size whatever's on it, and
        /// isn't made again each time the page is.
        /// </summary>
        private void FillPage(DrawingContext context, LaneProjection projection)
        {
            // Nothing to keep the size of, on a plane that's drawn again for every move: it is just a screen.
            if (_camera is not null)
                return;

            double across = View == LaneView.Highway ? Bounds.Width : Bounds.Height;
            context.FillRectangle(Brushes.Transparent, Box(projection.TimeFrom - PagePad, projection.TimeFrom + projection.TimeLength + PagePad, 0, across));
        }

        /// <summary>The columns or rows of <paramref name="slots"/>, along all of <paramref name="projection"/>'s time axis.</summary>
        private Rect LanesOf(LaneProjection projection, LaneSlots slots) =>
            Box(projection.TimeFrom, projection.TimeFrom + projection.TimeLength, projection.LaneFrom, projection.LaneFrom + (projection.LaneSize * slots.Slots.Count));

        /// <summary>The ticks at <paramref name="projection"/>'s ends, from 0.</summary>
        private static (double From, double To) TicksOf(EditorSession session, LaneProjection projection) =>
            TicksOf(session, projection.Start, projection.End);

        /// <summary>The ticks at <paramref name="start"/> and <paramref name="end"/>, in seconds of audio, from 0.</summary>
        private static (double From, double To) TicksOf(EditorSession session, double start, double end) =>
            (Math.Max(0, session.TickAt(start)), Math.Max(0, session.TickAt(end)));

        /// <summary>
        /// The page the song and the notes are drawn as, for the lane <paramref name="screen"/> shows: the one they share, or,
        /// when it doesn't serve, a new one around the screen. The viewport's changes make new pages ahead of the drawing;
        /// one made here, as a lane drawn first or laid out again, has the other layer drawn on it too, once this frame is
        /// out, rather than now, while the frame's layers are being drawn.
        /// </summary>
        private LanePage PageFor(LaneProjection screen)
        {
            if (_page is { } page && page.Fits(screen))
                return page;

            var (behind, ahead) = PageReach();
            _page = LanePage.Of(screen, _isBackwards ? ahead : behind, _isBackwards ? behind : ahead, MaxPageLength());

            // A lane without pages kept, a tilted one or one too long for them, has both layers drawn again for every move
            // (OnViewportChanged), each on the page of the moment: there's none left behind to catch up.
            if (_isPaged)
                Dispatcher.UIThread.Post(_redrawStalePages);
            return _page;
        }

        /// <summary>Has the song and the notes drawn again where they're on a page other than the one now shown.</summary>
        private void RedrawStalePages()
        {
            foreach (var layer in (ReadOnlySpan<Layer>)[_song, _notes])
            {
                if (layer.Page is { } drawn && !ReferenceEquals(drawn, _page))
                    layer.Redraw();
            }
        }

        /// <summary>
        /// How far a page reaches past the screen, in screens: a little back and most of it ahead, as the view moves. None for a
        /// tilted plane, whose lane is drawn again for every move, on the screen as it is (see <see cref="UpdatePaging"/>).
        /// </summary>
        private (double Behind, double Ahead) PageReach() => _camera is null ? (PageBehind, PageAhead) : (0, 0);

        /// <summary>The longest a page may be along the time axis, in the canvas's own pixels.</summary>
        private double MaxPageLength() => MaxPagePixels / (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);

        /// <summary>
        /// Whether the lane is short enough along the time axis to have pages that reach usefully past the screen, and whether
        /// its pages are kept as pictures: only while it's shown, so a hidden view doesn't hold on to its pictures.
        /// </summary>
        private void UpdatePaging()
        {
            double length = View == LaneView.Highway ? _arranged.Height : _arranged.Width;

            // Not while the lane is tilted: its layers are drawn again for every move, on the screen as it is, as a lane too long
            // for pages is. A page is a picture, or a drawing, moved as the song plays; tilted, the tilt draws it again, smaller and
            // smoothed, so thin lines and the waveform shimmer, and what it holds past the plane's near end is drawn ever larger,
            // so large towards the camera that the graphics card drops it, and the lane with it, until the next page.
            _isPaged = _camera is null && length > 0 && MaxPageLength() - length >= MinPageReach;
            bool cached = CacheLayers && _isPaged && IsVisible;
            foreach (var layer in (ReadOnlySpan<Layer>)[_song, _notes])
            {
                if (!cached)
                    layer.CacheMode = null;
                else                     
                    // Not snapped to the screen's pixels: snapped, a slow lane's picture stands still for frames, then
                    // jumps a pixel; drawn smoothed between them, it moves as smoothly as the song.
                    layer.CacheMode ??= new BitmapCache { SnapsToDevicePixels = false };
            }
        }

        /// <summary>Has every layer drawn again, on a new page: after anything but the cursor's moving changes what's drawn.</summary>
        private void InvalidateLayers()
        {
            _page = null;
            foreach (var layer in _layers)
                layer.Redraw();
        }

        /// <summary>
        /// Whether the place tool's ghost, the erase tool's red, the Phrase tool's line or the Tempo tool's marker preview
        /// is shown under the pointer, which follow the song as it moves under it; or a Select tool's rail hover, whose
        /// placement line does too; or, with a synchronized region or the loop region drawn, its X and hover, which do too.
        /// </summary>
        private bool IsHoverShown => _hover is { } hover && !_isPressed
            && (Session?.Tool is EditorTool.Place or EditorTool.Erase or EditorTool.Phrase or EditorTool.Tempo
                || Session?.Sync.IsActive == true
                || Session?.Loop?.Span is not null
                || (Session?.Tool == EditorTool.Select && PointAt(hover) is { Rail: not null }));

        /// <summary>
        /// Follows the cursor: the song and the notes are moved, as far as their page reaches, and drawn again around the
        /// screen past it, rather than the whole lane drawn again every frame the song plays. The glow is drawn again while
        /// the song plays, and the tools' hover marks, which snap to the song under a still pointer.
        /// </summary>
        private void OnViewportChanged(object? sender, EventArgs e)
        {
            if (!IsVisible)
                return;
            if (CurrentProjection() is not { } screen)
            {
                InvalidateLayers();
                return;
            }

            if (_lastStart is { } last && screen.Start != last)
                _isBackwards = screen.Start < last;
            _lastStart = screen.Start;

            if (_isPaged && _page is { } page && page.Fits(screen, PageMargin))
            {
                _song.Follow(page, screen);
                _notes.Follow(page, screen);
            }
            else
            {
                _page = null;
                _song.Redraw();
                _notes.Redraw();
            }

            // The notes standing up on a perspective highway aren't on the page, so they are drawn again for every move.
            RedrawGems();

            // A vocal part lights the note and the phrase on the strike line wherever the cursor stops, not only as it plays.
            bool vocal = _lane is { Vocal: not null };
            if (_editor?.Playback.IsPlaying == true || vocal || FramePerf.SimulatesPlaying)
                _strike.Redraw();
            if (_editor?.Playtest.HasFeedback == true)
                _playtest.Redraw();
            if (IsHoverShown || vocal)
            {
                _overlay.Redraw();
            }
            else if (_isPressed && _boxFrom is not null && Session is { } moving)
            {
                // Scrolling mid-drag moves the song under a still pointer: keep the selection live at the pointer and
                // redraw the box, whose press anchor is re-projected when drawn.
                if (_lane is { Vocal: not null })
                {
                    if (VocalPointAt(_boxTo, clamp: true) is { } vocalPoint)
                    {
                        _lastVocalPoint = vocalPoint;
                        moving.Tools.Drag(vocalPoint, ModifiersOf(_keys));
                    }
                }
                else if (PointAt(_boxTo + _pressOffset, onGems: false) is { } scrolled)
                {
                    _lastPoint = scrolled;
                    moving.Tools.Drag(scrolled, ModifiersOf(_keys));
                }

                _overlay.Redraw();
            }
        }

        /// <summary>Which of the canvas's layers, bottom to top.</summary>
        private enum LayerKind
        {
            /// <summary>The song under the notes, on a page.</summary>
            Song,

            /// <summary>The strike line's band and the glow, which stay put on the screen.</summary>
            Strike,

            /// <summary>The notes, on a page.</summary>
            Notes,

            /// <summary>What a playtest found, which stays put on the screen, over the notes (charter-spec C19 item 4).</summary>
            Playtest,

            /// <summary>The place tool's ghost, the cursor, a box being drawn and the focus's edge.</summary>
            Overlay,
        }

        /// <summary>
        /// One of the canvas's layers, which the canvas draws. One that moves with the song is drawn on a page, then only
        /// moved, by its composition visual's translation: moving it by a render transform would have it drawn again.
        /// Kept as a picture of itself too, it's no more than that picture to draw each frame.
        /// </summary>
        private sealed class Layer : Control
        {
            private readonly Painter _painter;

            private readonly EditorCanvas _owner;

            public Layer(EditorCanvas owner, LayerKind kind)
            {
                _owner = owner;
                Kind = kind;
                IsHitTestVisible = false;
                _painter = new Painter(owner, this);
                VisualChildren.Add(_painter);
            }

            public LayerKind Kind { get; }

            /// <summary>The page this was last drawn on; none for a layer that stays put, or that had nothing to draw.</summary>
            public LanePage? Page { get; set; }

            protected override Size ArrangeOverride(Size finalSize)
            {
                _painter.Arrange(new Rect(finalSize));
                return finalSize;
            }

            /// <summary>Has this drawn again; and the notes standing up on a perspective highway, with the layer that draws their tails.</summary>
            public void Redraw()
            {
                // Drawn with the song while the lane is tilted: the song's drawing is the one to draw again.
                if (_owner.DrawsWithSong(Kind))
                {
                    _owner._song.Redraw();
                    return;
                }

                _painter.InvalidateVisual();
                if (Kind is LayerKind.Notes or LayerKind.Song)
                    _owner.RedrawGems();
                else if (Kind is LayerKind.Overlay or LayerKind.Playtest)
                    _owner.RedrawGemMarks();
            }

            /// <summary>Has this drawn again, itself, whatever draws it: as when it has just come to be drawn with the song, and is to be empty.</summary>
            public void RedrawItself() => _painter.InvalidateVisual();

            /// <summary>Drawn on <paramref name="page"/>, moved to show what <paramref name="screen"/> shows.</summary>
            public void Show(LanePage page, LaneProjection screen)
            {
                Page = page;
                MoveTo(page.ShiftOf(screen), screen.View);
            }

            /// <summary>Moves this to show what <paramref name="screen"/> shows, or has it drawn again when it isn't on <paramref name="page"/>.</summary>
            public void Follow(LanePage page, LaneProjection screen)
            {
                if (!ReferenceEquals(Page, page) || !MoveTo(page.ShiftOf(screen), screen.View))
                    Redraw();
            }

            private bool MoveTo(double shift, LaneView view)
            {
                if (ElementComposition.GetElementVisual(this) is not { } visual)
                    return false;
                visual.Translation = view == LaneView.Highway ? new Vector3D(0, shift, 0) : new Vector3D(shift, 0, 0);
                return true;
            }
        }

        /// <summary>
        /// What a layer draws, drawn by a visual of its own inside the layer. A layer kept as a picture has its picture drawn
        /// again only when what's inside it changes, not when its own drawing does, so a layer that drew its page itself
        /// would go on showing its first page however many it was drawn on since.
        /// </summary>
        private sealed class Painter : Control
        {
            private readonly EditorCanvas _owner;
            private readonly Layer _layer;

            public Painter(EditorCanvas owner, Layer layer)
            {
                _owner = owner;
                _layer = layer;
                IsHitTestVisible = false;
            }

            public override void Render(DrawingContext context)
            {
                var perf = _owner._perf;
                long start = FramePerf.Now(perf);
                _owner.RenderLayer(context, _layer);
                perf?.Add((FramePerf.Slot)(int)_layer.Kind, start);
            }
        }

        protected override void OnGotFocus(FocusChangedEventArgs e)
        {
            base.OnGotFocus(e);
            if (Session is { } session)
            {
                if (_lane?.Vocal is { } vocal)
                    session.FocusedVocal = vocal;
                else if (_lane?.Track is { } key)
                {
                    session.Focused = key;
                    session.FocusedVocal = null;
                }
            }

            _overlay.Redraw();
        }

        protected override void OnLostFocus(FocusChangedEventArgs e)
        {
            base.OnLostFocus(e);
            _overlay.Redraw();
        }

        /// <summary>
        /// A press starts a gesture of the button pressed: the left button's of the current tool (with Shift, the select
        /// tool's; with Ctrl or Alt on a selected note, its sustains or runs), the middle button's erasing whatever the
        /// tool, and the right button's lengthening or shortening the selected notes, or laying runs of them with Alt, or
        /// opening the note menu when it isn't dragged. Another button pressed while one's gesture goes on is ignored.
        /// </summary>
        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var current = e.GetCurrentPoint(this);
            if (Session is not { } session)
                return;
            if (_isPressed || _proposalDrag is not null || _gestures.IsScrubbing)
            {
                e.Handled = true;
                return;
            }

            var button = current.Properties.PointerUpdateKind switch
            {
                PointerUpdateKind.LeftButtonPressed => MouseButton.Left,
                PointerUpdateKind.MiddleButtonPressed => MouseButton.Middle,
                PointerUpdateKind.RightButtonPressed => MouseButton.Right,
                _ => MouseButton.None,
            };
            if (button == MouseButton.None)
                return;

            // Shift and the middle button scrub the song, in a vocal lane as well, before they're the tools'.
            if (_gestures.IsScrubPress(e))
            {
                Focus();
                ShowTip(null);
                _gestures.Press(e, current.Position);
                return;
            }

            _pressButton = button;
            _pressedAt = current.Position;
            _pressDragged = false;
            ShowTip(null);

            // The small X at the loop region's top right clears it, whatever the tool, in a vocal part's lane too.
            if (button == MouseButton.Left && LoopCloseAt(current.Position))
            {
                session.ClearLoop();
                Focus();
                e.Handled = true;
                _overlay.Redraw();
                return;
            }

            if (_lane is { Vocal: not null })
            {
                PressVocals(session, current.Position, e);
                return;
            }

            // The small X at the synchronized region's top right clears it, whatever the tool.
            if (button == MouseButton.Left && SyncCloseAt(current.Position))
            {
                session.Sync.Clear();
                Focus();
                e.Handled = true;
                _overlay.Redraw();
                return;
            }

            // A gem standing up on a perspective highway is pressed where its floor is, and the drag that follows keeps the
            // pointer's distance from there, so the note doesn't jump as it starts to move.
            var pressedOn = OnGem(current.Position);
            _pressOffset = pressedOn - current.Position;
            if (PointAt(pressedOn, onGems: false) is not { } point)
                return;

            Focus();
            e.Pointer.Capture(this);

            // A proposed beat's handle, while a proposed tempo map is reviewed, comes before the tools.
            if (button == MouseButton.Left && ProposalHandleAt(current.Position) is { } beat)
            {
                _proposalDrag = beat;
                e.Handled = true;
                return;
            }

            _isPressed = true;
            _lastPoint = point;

            // A box is drawn from a press in the lanes that's on no note, with the select tool or Shift, which lends any tool
            // the select tool's press. The Sync and Loop tools' presses draw no box.
            bool sync = session.Tools.IsSyncPress(ModifiersOf(e.KeyModifiers));
            bool selecting = !sync && !session.Tools.LoopActive && (session.Tool == EditorTool.Select || (e.KeyModifiers & KeyModifiers.Shift) != 0);
            _boxFrom = button == MouseButton.Left && selecting && point.Rail is null && session.TrackOf(point.Track) is not null && session.Tools.HitTest(point) is null
                ? current.Position
                : null;
            _boxTo = current.Position;
            _boxFromSeconds = _boxFrom is not null && CurrentProjection() is { } anchorScreen
                ? anchorScreen.SecondsAt(AlongOf(current.Position))
                : null;
            var modifiers = ModifiersOf(e.KeyModifiers);
            if (button == MouseButton.Middle)
                session.Tools.PressErase(point);
            else if (button == MouseButton.Right)
                session.Tools.PressContext(point, modifiers);
            else
                session.Tools.Press(point, modifiers);
            e.Handled = true;
            _overlay.Redraw();
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var position = e.GetPosition(this);
            _hover = position;
            _keys = e.KeyModifiers;
            if (_gestures.Move(e))
                return;

            // Keys pressed while the window was in the background reach the toolbar's place options here.
            _editor?.HoldModifiers(ModifiersOf(e.KeyModifiers));
            if (!_isPressed)
                Session?.Tools.HoldKeys(ModifiersOf(e.KeyModifiers));
            if (_proposalDrag is { } beat)
            {
                DragProposedBeat(beat, position);
                return;
            }

            if (_isPressed)
            {
                var moved = position - _pressedAt;
                _pressDragged |= (moved.X * moved.X) + (moved.Y * moved.Y) > ClickSlop * ClickSlop;
            }

            if (_isPressed && Session is { } session && _lane is { Vocal: not null })
            {
                if (VocalPointAt(position, clamp: true) is { } vocalPoint)
                {
                    _lastVocalPoint = vocalPoint;
                    _boxTo = position;
                    session.Tools.Drag(vocalPoint, ModifiersOf(e.KeyModifiers));
                }
            }
            else if (_isPressed && Session is { } highway && PointAt(position + _pressOffset, onGems: false) is { } point)
            {
                _lastPoint = point;
                _boxTo = position;
                highway.Tools.Drag(point, ModifiersOf(e.KeyModifiers));
            }
            else if (!_isPressed)
            {
                if (_lane is { Vocal: not null })
                {
                    if (!ShowLoopCursor(position) && !ShowVocalTempoCursor(position))
                        ShowVocalCursor(position);
                }
                else if (!ShowLoopCursor(position) && !ShowSyncCursor(position) && !ShowTempoCursor(position))
                {
                    ShowRailCursor(position);
                }
            }

            _overlay.Redraw();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (_gestures.Release(e))
                return;
            if (_proposalDrag is not null)
            {
                _proposalDrag = null;
                e.Pointer.Capture(null);
                return;
            }

            if (!_isPressed || e.InitialPressMouseButton != _pressButton)
                return;
            _isPressed = false;
            _boxFrom = null;
            _boxFromSeconds = null;
            e.Pointer.Capture(null);
            if (Session is not { } session)
                return;
            if (_pressButton == MouseButton.Right && !_pressDragged)
            {
                // A right click: the note it was on is selected, and the menu acts on the selection.
                session.Tools.Cancel();
                ShowNoteMenu();
            }
            else if (_lane is { Vocal: not null })
            {
                session.Tools.Release(VocalPointAt(e.GetPosition(this), clamp: true) ?? _lastVocalPoint);
            }
            else
            {
                session.Tools.Release(PointAt(e.GetPosition(this) + _pressOffset, onGems: false) ?? _lastPoint);
            }

            e.Handled = true;
            _overlay.Redraw();
        }

        /// <summary>Opens the note menu at the pointer, for the selected notes, after a right click; nothing with none.</summary>
        private void ShowNoteMenu()
        {
            if (_lane?.BuildNoteMenu() is not { Count: > 0 } items)
                return;
            new MenuFlyout { ItemsSource = LaneMenu.MenuItems(items) }.ShowAt(this, showAtPointer: true);
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            _gestures.CaptureLost();
            _proposalDrag = null;
            if (!_isPressed)
                return;
            _isPressed = false;
            _boxFrom = null;
            _boxFromSeconds = null;
            if (_lane is { Vocal: not null })
                Session?.Tools.Release(_lastVocalPoint);
            else
                Session?.Tools.Release(_lastPoint);
            _overlay.Redraw();
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            _hover = null;
            ShowTip(null);
            _overlay.Redraw();
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            if (Session is not { } session || _lane is not { HasCanvas: true } lane)
                return;

            // The song moves past the strike line a grid step a notch, through the tempo map, so the strike line stays on the
            // grid across tempo and time-signature changes; Ctrl zooms, turned up the lane looking less far ahead. A turn of none
            // isn't taken, so a sideways touchpad swipe still reaches what's above.
            _gestures.Wheel(e);
        }

        // Ctrl is watched, to make a scrub fine, while one goes on.
        private bool WatchesKeys() => _gestures.IsScrubbing;

        /// <summary>What the shared gestures move in an editor lane: the cursor, as the keys and the tools do.</summary>
        private sealed class ViewportHost(EditorCanvas canvas) : IViewportGestureHost
        {
            public double Now => canvas.Session?.Viewport.Cursor ?? 0;

            public double SecondsPerPixel => canvas.CurrentProjection() is { } projection && projection.PixelsPerSecond > 0 ? 1 / projection.PixelsPerSecond : 0;

            public bool IsTimeAxisVertical => canvas.View == LaneView.Highway;

            public bool WheelUpIsLater => canvas.WheelUpIsLater;

            public double FineFactor => LaneScrub.DefaultFineFactor;

            public void ScrubStarted()
            {
            }

            public void ScrubEnded()
            {
            }

            // The cursor moves, and playback seeks there, as it does for every move of it by hand.
            public void ScrubTo(double seconds)
            {
                if (canvas.Session is not { } session)
                    return;
                double length = canvas._editor?.Playback.LengthSeconds ?? 0;
                session.Viewport.MoveCursor(length > 0 ? Math.Clamp(seconds, 0, length) : Math.Max(0, seconds));
            }

            public void Scroll(int steps) => canvas.Session?.Tools.StepCursor(steps);

            public void Zoom(int steps) => canvas._lane?.StepLookAhead(-steps);

            public double? OverviewTimeAt(Point at) => null;
        }

        // The canvas's keys are the window's key dispatcher's, which finds the canvas with focus and runs the key's action on it
        // (EditorKeys); here, only the modifiers held are taken in.
        protected override void OnKeyDown(KeyEventArgs e)
        {
            // The frame-time readout's switches for parts of the drawing, to see what they cost.
            if (_perf is not null && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
            {
                if (e.Key == Key.F12 && _editor is { Playback.IsPlaying: false } && Session is { } session && TopLevel.GetTopLevel(this) is { } top)
                {
                    e.Handled = true;
                    _perf.Benchmark(top, session.Viewport.Cursor, seconds => { session.Viewport.Follow(seconds); _strike.Redraw(); }, ApplyPerfSwitches);
                    return;
                }

                if (FramePerf.Toggle(e.Key))
                {
                    e.Handled = true;
                    ApplyPerfSwitches();
                    return;
                }
            }

            base.OnKeyDown(e);
            TakeKeys(e.KeyModifiers);
        }

        /// <summary>Takes up the frame-time readout's switches: the canvas's clip, and every layer drawn again.</summary>
        private void ApplyPerfSwitches()
        {
            ClipToBounds = _camera is null || FramePerf.IsOff(FramePerf.Part.CanvasClip);
            InvalidateLayers();
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            TakeKeys(e.KeyModifiers);
        }

        /// <summary>The editor and the lane this canvas shows, for a key that acts on them; false while it shows none.</summary>
        internal bool TryGetKeyContext([NotNullWhen(true)] out ChartEditorViewModel? editor, [NotNullWhen(true)] out EditorLaneViewModel? lane)
        {
            editor = _editor;
            lane = _lane;
            return editor is not null && lane is not null && Session is not null;
        }

        /// <summary>
        /// Takes in the modifier keys held after one goes down or up: a drag goes on with them, as Ctrl makes the right
        /// button's sustains end together and Alt makes it lay runs, and the place tool's drag a sustain with Ctrl; the
        /// place tool's ghost moves to the open lane with Alt, and goes with Shift, which lends it the select tool's press.
        /// </summary>
        private void TakeKeys(KeyModifiers keys)
        {
            Session?.Tools.HoldKeys(ModifiersOf(keys));
            if (keys == _keys)
                return;
            _keys = keys;
            if (_isPressed && Session is { } session)
            {
                if (_lane is { Vocal: not null })
                    session.Tools.Drag(_lastVocalPoint, ModifiersOf(keys));
                else
                    session.Tools.Drag(_lastPoint, ModifiersOf(keys));
            }

            _overlay.Redraw();
        }

        /// <summary>
        /// Takes in the modifier keys held as the window sees them, wherever focus is: a key that goes down or up while
        /// focus is elsewhere, or that something above the lane takes, as Alt can be, never reaches <see cref="OnKeyDown"/>.
        /// </summary>
        private void OnModifiersHeld(object? sender, EditorModifiers held)
        {
            var keys = (_keys & ~(KeyModifiers.Shift | KeyModifiers.Control | KeyModifiers.Alt))
                | (held.HasFlag(EditorModifiers.Shift) ? KeyModifiers.Shift : KeyModifiers.None)
                | (held.HasFlag(EditorModifiers.Control) ? KeyModifiers.Control : KeyModifiers.None)
                | (held.HasFlag(EditorModifiers.Alt) ? KeyModifiers.Alt : KeyModifiers.None);
            TakeKeys(keys);
        }

        /// <summary>Moves focus to the lane's other canvas, once it's shown in place of this one.</summary>
        internal void FocusSibling()
        {
            Dispatcher.UIThread.Post(() =>
            {
                // The highway's canvas may be inside a perspective host, so look through the whole lane.
                if (this.FindAncestorOfType<EditorLane>()?.GetVisualDescendants().OfType<EditorCanvas>().FirstOrDefault(c => c.IsVisible) is { } shown)
                    shown.Focus();
            });
        }

        /// <summary>The lanes as the canvas lays them out now, for the keys that move across them.</summary>
        internal LaneSlots? Slots => Session is { } session && _lane?.Track is { } key ? session.SlotsOf(key.Instrument, View) : null;

        internal LaneView CanvasView => View;

        /// <summary>
        /// The chart point under <paramref name="position"/>, or null when there's no part to edit: in the lanes, or in the
        /// phrase rail's row there.
        /// </summary>
        /// <param name="onGems">Whether a gem standing up on a perspective highway is taken to be where its floor is, wherever it is pointed at.</param>
        private ChartPoint? PointAt(Point position, bool onGems = true)
        {
            if (Session is not { } session || _lane?.Track is not { } key || session.TrackOf(key) is null)
                return null;
            if (onGems)
                position = OnGem(position);
            var slots = session.SlotsOf(key.Instrument, View);
            var rows = PhraseRail.RowsOf(ChartInstruments.KindOf(key.Instrument));
            Extras = ExtrasOf(Bounds.Size, key);
            var projection = Project(Bounds.Size, slots, session.Viewport, _lane.Length, rows.Count);
            double seconds = projection.SecondsAt(AlongOf(position));
            double tick = session.TickAt(seconds);
            double tolerance = Math.Max(1, projection.TicksOf(HitPixelsAt(position), Math.Max(0, tick), session.TempoMap));
            double across = AcrossOf(position);
            var rail = PhraseRowAt(projection, rows, across);
            return new ChartPoint(key, tick, rail is null ? projection.LaneAt(across) : null, tolerance, slots, rail);
        }

        /// <summary>The kind whose phrase rail row is at <paramref name="across"/> pixels, or null when it's outside the rail.</summary>
        private RailKind? PhraseRowAt(LaneProjection projection, IReadOnlyList<RailKind> rows, double across)
        {
            double from = PhraseRailFrom(projection, rows.Count);
            if (rows.Count == 0 || across < from || across > from + PhraseRailSize(rows.Count))
                return null;

            // A gap goes to the rows either side of it, and the margins to the first and last.
            int row = (int)Math.Floor((across - from - PhraseRailMargin + (PhraseRowGap / 2)) / (PhraseRowSize + PhraseRowGap));
            return rows[Math.Clamp(row, 0, rows.Count - 1)];
        }

        /// <summary>Where the row of the phrase rail at <paramref name="index"/> is across the view: its near and far edges.</summary>
        private (double From, double To) PhraseRowOf(LaneProjection projection, int rowCount, int index)
        {
            double from = PhraseRailFrom(projection, rowCount) + PhraseRailMargin + (index * (PhraseRowSize + PhraseRowGap));
            return (from, from + PhraseRowSize);
        }

        /// <summary>
        /// Shows what the pointer would take hold of on the loop region: a hand over its X, which clears it whatever the
        /// tool; and with the Loop tool picked, or L held, a resize arrow over an end and a hand over its body to move it;
        /// each with a tip saying what it does. Whether it showed anything.
        /// </summary>
        private bool ShowLoopCursor(Point position)
        {
            if (Loop is not { } loop || CurrentProjection() is not { } projection)
                return false;
            var band = LoopBandOf(projection, loop);
            if (CloseRectOf(band, LanesOf(projection, projection.Slots)).Inflate(ClickSlop).Contains(position))
            {
                SetCursor(StandardCursorType.Hand);
                ShowTip(Strings.LoopClearToolTip);
                return true;
            }

            if (Session is not { Tools.LoopActive: true } session || !band.Contains(position))
                return false;
            if (BandTickAt(projection, position) is not { } at || session.Tools.LoopGripAt(at.Tick, at.Tolerance) is not { } grip)
                return false;
            SetCursor(grip == LoopGrip.Body
                ? StandardCursorType.Hand
                : View == LaneView.Highway ? StandardCursorType.SizeNorthSouth : StandardCursorType.SizeWestEast);
            ShowTip(grip == LoopGrip.Body ? Strings.LoopMoveToolTip : Strings.LoopResizeToolTip);
            return true;
        }

        /// <summary>
        /// Shows what the pointer would take hold of on the synchronized region: a hand over its X, which clears it whatever
        /// the tool; and with the Sync tool picked, or Ctrl + Shift held, a resize arrow over an end, a hand over its body
        /// to move it, and over a passage synced with it to make it the source; each with a tip saying what it does.
        /// Whether it showed anything.
        /// </summary>
        private bool ShowSyncCursor(Point position)
        {
            if (Session is not { } session || _lane?.Track is not { } key || session.Sync is not { IsActive: true } sync || sync.Track != key)
                return false;
            if (CurrentProjection() is not { } projection || sync.Span is not var (from, to))
                return false;
            var close = CloseRectOf(SyncBandOf(session, projection, from, to), LanesOf(projection, projection.Slots));
            if (close.Inflate(ClickSlop).Contains(position))
            {
                SetCursor(StandardCursorType.Hand);
                ShowTip(Strings.SyncClearToolTip);
                return true;
            }

            if (!session.Tools.IsSyncPress(ModifiersOf(_keys)))
                return false;
            if (PointAt(position) is not { Rail: null } point)
                return false;

            long length = to - from;
            foreach (long start in sync.Matches)
            {
                if (point.Tick >= start && point.Tick < start + length)
                {
                    SetCursor(StandardCursorType.Hand);
                    ShowTip(Strings.SyncSourceToolTip);
                    return true;
                }
            }

            if (Math.Abs(point.Tick - from) <= point.Tolerance || Math.Abs(point.Tick - to) <= point.Tolerance)
            {
                SetCursor(View == LaneView.Highway ? StandardCursorType.SizeNorthSouth : StandardCursorType.SizeWestEast);
                ShowTip(Strings.SyncResizeToolTip);
                return true;
            }

            if (point.Tick > from && point.Tick < to)
            {
                SetCursor(StandardCursorType.Hand);
                ShowTip(Strings.SyncMoveToolTip);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Shows a resize arrow over a tempo or time signature change the Tempo tool would drag: up and down a highway's
        /// lane, left and right along a timeline's. The change the song starts with can't be moved, so shows the usual
        /// cursor. Whether it showed anything.
        /// </summary>
        private bool ShowTempoCursor(Point position)
        {
            if (Session is not { } session || session.Tool != EditorTool.Tempo || (_keys & KeyModifiers.Shift) != 0
                || session.Tools.IsSyncPress(ModifiersOf(_keys)))
            {
                return false;
            }

            if (PointAt(position) is not { Rail: null } point || session.Tools.TempoChangeAt(point) is not { } tick)
                return false;
            ShowTempoCursorFor(tick);
            return true;
        }

        /// <summary>Shows the cursor a tempo or time signature change can be dragged by, or the usual one at the song's start.</summary>
        private void ShowTempoCursorFor(long tick)
        {
            SetCursor(EditorTools.TempoChangeMoves(tick)
                ? View == LaneView.Highway ? StandardCursorType.SizeNorthSouth : StandardCursorType.SizeWestEast
                : null);
            ShowTip(null);
        }

        /// <summary>
        /// Shows a tooltip naming the phrase rail's row or bar under the pointer; a resize arrow over a phrase or disco flip
        /// bar's end and a move arrow over its body; and a cross over an empty row where a drag would draw a phrase of its
        /// kind.
        /// </summary>
        private void ShowRailCursor(Point position)
        {
            StandardCursorType? wanted = null;
            string? tip = null;
            if (PointAt(position) is { Rail: not null } point && Session is { } session)
            {
                tip = ChartEditorViewModel.PhraseKindName(point.Rail.Value);
                PhraseGrip? grip = null;
                if (session.Chart.PartOf(point.Track.Instrument) is { } part && PhraseRail.HitTest(part, point) is { } phrase)
                    grip = phrase.Grip;
                else if (PhraseRail.HitTestDiscoFlip(point.Track.Difficulty, session.DiscoFlipRangesOf(point.Track), point) is { } flip)
                    grip = flip.Grip;

                // The erase tool erases a bar rather than grabbing it, as its red shows.
                if (grip is { } grabbed && (session.Tool != EditorTool.Erase || (_keys & KeyModifiers.Shift) != 0))
                {
                    wanted = grabbed == PhraseGrip.Body ? StandardCursorType.SizeAll
                        : View == LaneView.Highway ? StandardCursorType.SizeNorthSouth
                        : StandardCursorType.SizeWestEast;
                }
                else if (grip is null && (_keys & KeyModifiers.Shift) == 0
                    && session.Tools.PhraseDrawAt(point) is { } draw
                    && session.Tools.DrawsIn(point.Track, draw.Kind))
                {
                    wanted = StandardCursorType.Cross;
                }
            }

            SetCursor(wanted);
            ShowTip(tip);
        }

        /// <summary>Shows <paramref name="tip"/> by the pointer, as what's under it says what it is, or none for null.</summary>
        private void ShowTip(string? tip)
        {
            if (Equals(ToolTip.GetTip(this), tip))
                return;
            ToolTip.SetTip(this, tip);
            if (tip is null)
                ToolTip.SetIsOpen(this, false);
        }

        /// <summary>Shows <paramref name="wanted"/> as the pointer's cursor, or the usual one for null.</summary>
        private void SetCursor(StandardCursorType? wanted)
        {
            if (wanted == _cursorType)
                return;
            _cursorType = wanted;
            if (wanted is not { } type)
            {
                Cursor = null;
                return;
            }

            if (!RailCursors.TryGetValue(type, out var cursor))
            {
                cursor = new Cursor(type);
                RailCursors[type] = cursor;
            }

            Cursor = cursor;
        }

        internal static EditorModifiers ModifiersOf(KeyModifiers keys) =>
            ((keys & KeyModifiers.Shift) != 0 ? EditorModifiers.Shift : EditorModifiers.None)
            | ((keys & KeyModifiers.Control) != 0 ? EditorModifiers.Control : EditorModifiers.None)
            | ((keys & KeyModifiers.Alt) != 0 ? EditorModifiers.Alt : EditorModifiers.None);

        private void Attach(EditorLaneViewModel? lane)
        {
            // Only while it's on screen, so a lane closed lets go of the editor.
            var wanted = TopLevel.GetTopLevel(this) is null ? null : lane;
            if (ReferenceEquals(wanted, _lane))
                return;
            Detach();
            if (wanted is null)
                return;

            _lane = wanted;
            lane = wanted;
            _editor = lane.Editor;
            lane.PropertyChanged += OnLaneChanged;
            if (_editor is { } editor)
            {
                editor.Session.ChartChanged += OnChartChanged;
                editor.Session.StateChanged += OnRedraw;
                editor.Session.Selection.Changed += OnRedraw;
                editor.Session.Viewport.Changed += OnViewportChanged;
                editor.Playback.PropertyChanged += OnPlaybackChanged;
                editor.Audio.Changed += OnAudioChanged;
                editor.AudioAssist.PropertyChanged += OnAudioAssistChanged;
                editor.Playtest.Changed += OnPlaytestChanged;
                editor.ModifiersHeld += OnModifiersHeld;
            }

            _topLevel = TopLevel.GetTopLevel(this);
            if (_topLevel is { } top)
                top.ScalingChanged += OnScalingChanged;

            // Laid out before it had a lane to tell how long it is.
            lane.SetExtent(TimeAxisLength(_arranged));
            InvalidateLayers();
            RequestFrame();
        }

        private void Detach()
        {
            if (_lane is { } lane)
                lane.PropertyChanged -= OnLaneChanged;
            if (_editor is { } editor)
            {
                editor.Session.ChartChanged -= OnChartChanged;
                editor.Session.StateChanged -= OnRedraw;
                editor.Session.Selection.Changed -= OnRedraw;
                editor.Session.Viewport.Changed -= OnViewportChanged;
                editor.Playback.PropertyChanged -= OnPlaybackChanged;
                editor.Audio.Changed -= OnAudioChanged;
                editor.AudioAssist.PropertyChanged -= OnAudioAssistChanged;
                editor.Playtest.Changed -= OnPlaytestChanged;
                editor.ModifiersHeld -= OnModifiersHeld;
            }

            if (_topLevel is { } top)
                top.ScalingChanged -= OnScalingChanged;
            _topLevel = null;
            _redrawTimer?.Stop();
            _isRedrawPosted = false;

            _lane = null;
            _editor = null;
        }

        private void OnLaneChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(EditorLaneViewModel.Track) && IsFocused && Session is { } session && _lane?.Track is { } key)
                session.Focused = key;
            _longest = null;
            _ghostLongest = null;
            InvalidateLayers();
        }

        private void OnChartChanged(object? sender, EditorChartChangedEventArgs e)
        {
            _longest = null;
            _ghostLongest = null;
            InvalidateLayers();
        }

        private void OnRedraw(object? sender, EventArgs e) => InvalidateLayers();

        // More of the lane's audio read or analysed: only the song's page has it.
        private void OnAudioChanged(object? sender, EventArgs e) => _song.Redraw();

        // The pictures of the lane's audio are made at the screen's pixels.
        private void OnScalingChanged(object? sender, EventArgs e)
        {
            UpdatePaging();
            InvalidateLayers();
        }

        // Find first downbeat's guess is drawn over the lanes, on the song's page.
        private void OnAudioAssistChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AudioAssistViewModel.Downbeat))
                _song.Redraw();
        }

        private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(PlaybackViewModel.IsPlaying):
                    // Paused, the strike line's glow goes out.
                    _strike.Redraw();
                    RequestFrame();
                    break;
                case nameof(PlaybackViewModel.Waveform):
                    _song.Redraw();
                    break;
                case nameof(PlaybackViewModel.Loop):
                case nameof(PlaybackViewModel.IsLooping):
                    // The region's box, its X and what the pointer would take hold of.
                    _song.Redraw();
                    _overlay.Redraw();
                    break;
            }
        }

        /// <summary>Asks to be called back when the screen is next drawn, while the song plays.</summary>
        private void RequestFrame()
        {
            if (_isFrameRequested || _editor is not { Playback.IsPlaying: true } || !IsVisible)
                return;
            if (TopLevel.GetTopLevel(this) is not { } top)
                return;
            _isFrameRequested = true;
            top.RequestAnimationFrame(_onFrame);
        }

        private void OnFrame(TimeSpan time)
        {
            _isFrameRequested = false;
            if (_editor is not { } editor)
                return;

            // The view moves with the cursor, which moves the lane's pages (OnViewportChanged).
            _perf?.Frame();
            long start = FramePerf.Now(_perf);
            editor.FollowPlayback(time);
            _perf?.Follow(start);
            RequestFrame();
        }

        private void DrawLaneDividers(DrawingContext context, LaneProjection projection, LaneSlots slots)
        {
            // A shared column's halves, which a click picks between: GHL's black fret, then its white.
            HighwayChrome.DrawLaneDividers(context, projection, projection.TimeFrom, projection.TimeFrom + projection.TimeLength, View == LaneView.Timeline ? AlternateRowBrush : null, GridPen, asRect: _camera is not null);
        }

        /// <summary>
        /// The room the lane's audio and onsets take: a column beside a highway's lanes when its audio is drawn there, and
        /// a band of onsets for each band shown, three for drums (charter-spec C14 item 1).
        /// </summary>
        private LaneExtras ExtrasOf(Size size, ChartTrackKey key)
        {
            if (_lane is not { Audio: { HasAudio: true } audio })
                return default;
            double beside = audio.Options.Placement == LaneAudioPlacement.Beside ? BesideSizeFor(size) : 0;
            int bands = audio.Options.ShowOnsets ? (ChartInstruments.KindOf(key.Instrument) == ChartInstrumentKind.Drums ? 3 : 1) : 0;
            return new LaneExtras(beside, bands);
        }

        /// <summary>
        /// The lane's audio, as a waveform or a spectrogram (charter-spec C12, C13): faintly behind the lanes, or at full
        /// strength beside them, and always in a timeline's strip. Pictures there was no time to draw this frame are drawn
        /// once it's out.
        /// </summary>
        private void DrawBackdrop(DrawingContext context, Size size, LaneProjection projection, int phraseRows)
        {
            if (_lane?.Audio is not { Source: { } source } audio || _editor is not { } editor)
                return;

            var full = audio.Options.Placement == LaneAudioPlacement.Beside ? BesideArea(size, projection, phraseRows) : StripArea(size, projection);
            bool complete = true;
            if (audio.IsSpectrogram)
            {
                // Its chunks near the middle of the view are computed first.
                var spectrogram = editor.Audio.Analysis.SpectrogramFor(source, (projection.Start + projection.End) / 2);
                if (spectrogram is not null)
                {
                    if (audio.Options.Placement == LaneAudioPlacement.Behind)
                    {
                        using (context.PushOpacity(SpectrogramBehindOpacity))
                            complete &= DrawSpectrogram(context, audio, spectrogram, BehindArea(size, projection), projection);
                    }

                    complete &= DrawSpectrogram(context, audio, spectrogram, full, projection);
                }
            }
            else
            {
                if (audio.Options.Placement == LaneAudioPlacement.Behind)
                    complete &= DrawWaveform(context, editor, audio, source, BehindArea(size, projection), projection, full: false);
                complete &= DrawWaveform(context, editor, audio, source, full, projection, full: true);
            }

            if (!complete)
                PostRedraw();
        }

        /// <summary>Where a second of audio is along <paramref name="projection"/>'s time axis, as a painter takes it, which reads it as it begins.</summary>
        private Func<double, double> AlongOf(LaneProjection projection) => _alongOf.For(projection);

        /// <returns>Whether all of it was drawn as it is now.</returns>
        private bool DrawSpectrogram(DrawingContext context, LaneAudioViewModel audio, Spectrogram spectrogram, Rect area, LaneProjection projection)
        {
            if (area.Width <= 1 || area.Height <= 1)
                return true;
            double scaling = 1 / (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
            return SpectrogramPainter.Draw(context, spectrogram, area, View == LaneView.Highway, projection.Start, projection.End, AlongOf(projection),
                audio.Options.FloorDb, audio.Options.CeilingDb, audio.Options.Band, scaling);
        }

        /// <returns>Whether all of it was drawn.</returns>
        private bool DrawWaveform(DrawingContext context, ChartEditorViewModel editor, LaneAudioViewModel audio, AudioSource source, Rect area, LaneProjection projection, bool full)
        {
            if (area.Width <= 1 || area.Height <= 1)
                return true;

            var peak = full ? FullWaveformPeakBrush : WaveformPeakBrush;
            var loud = full ? FullWaveformLoudBrush : WaveformLoudBrush;
            double boost = Math.Pow(10, audio.Options.GainDb / 20);
            bool vertical = View == LaneView.Highway;

            // The painter's position is evaluated at two times and must be linear, as the projection is.
            double scaling = 1 / (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);

            // Tilted, the plane's pixels are foreshortened, even at its near end, where a bar one pixel long is half a pixel on the
            // screen: its pictures are made with bars as long as the screen shows them, a bar to a pixel there, and as few again to draw.
            if (_camera is { } camera)
                scaling /= Math.Max(camera.DepthScaleAt(camera.PlaneHeight), MinWaveformDepthScale);
            using (new OptionalClip(context, area, !FramePerf.IsOff(FramePerf.Part.PlaneClips)))
            {
                if (audio.IsStereo && editor.Audio.Waveforms.Stereo(source) is var (left, right))
                {
                    // Left beside right across a highway; left above right along a timeline. Both at one gain, so they compare.
                    double gain = LaneWaveformPainter.FullScaleGain(left.Peak >= right.Peak ? left : right) * boost;
                    var first = vertical ? new Rect(area.X, area.Y, area.Width / 2, area.Height) : new Rect(area.X, area.Y, area.Width, area.Height / 2);
                    var second = vertical ? new Rect(area.X + (area.Width / 2), area.Y, area.Width / 2, area.Height) : new Rect(area.X, area.Y + (area.Height / 2), area.Width, area.Height / 2);
                    bool done = LaneWaveformPainter.Draw(context, left, first, vertical, projection.Start, projection.End, AlongOf(projection), peak, loud, gain, scaling, budgeted: true, smooth: _camera is not null, clip: !FramePerf.IsOff(FramePerf.Part.PlaneClips));
                    done &= LaneWaveformPainter.Draw(context, right, second, vertical, projection.Start, projection.End, AlongOf(projection), peak, loud, gain, scaling, budgeted: true, smooth: _camera is not null, clip: !FramePerf.IsOff(FramePerf.Part.PlaneClips));
                    Line(context, ChannelPen, vertical ? first.TopRight : first.BottomLeft, vertical ? first.BottomRight : first.BottomRight);
                    return done;
                }

                if (editor.Audio.Waveforms.Mono(source) is not { } waveform)
                    return true;
                return LaneWaveformPainter.Draw(context, waveform, area, vertical, projection.Start, projection.End, AlongOf(projection),
                    peak, loud, LaneWaveformPainter.FullScaleGain(waveform) * boost, scaling, budgeted: true, smooth: _camera is not null, clip: !FramePerf.IsOff(FramePerf.Part.PlaneClips));
            }
        }

        /// <summary>
        /// The onsets of the lane's audio, as ticks in a strip beside the lanes (charter-spec C14 item 1): one band for
        /// guitar and bass, three for drums, kick nearest the lanes on a highway and at the top on a timeline.
        /// </summary>
        private void DrawOnsets(DrawingContext context, Size size, LaneProjection projection, ChartTrackKey key)
        {
            if (Extras.OnsetBands == 0 || _lane?.Audio is not { Source: { } source } audio || _editor is not { } editor)
                return;

            bool drums = Extras.OnsetBands > 1;
            var onsets = editor.Audio.Analysis.OnsetsAt(source, drums, audio.Options.OnsetSensitivity);
            if (onsets.Count == 0)
                return;

            var area = OnsetArea(size, projection);
            double from = Math.Min(projection.Start, projection.End), to = Math.Max(projection.Start, projection.End);
            int first = 0, last = onsets.Count;
            while (first < last)
            {
                int mid = (first + last) >>> 1;
                if (onsets[mid].Seconds < from)
                    first = mid + 1;
                else
                    last = mid;
            }

            bool vertical = View == LaneView.Highway;
            for (int i = first; i < onsets.Count && onsets[i].Seconds <= to; i++)
            {
                var onset = onsets[i];
                int band = drums ? Math.Max(0, (int)onset.Band - 1) : 0;
                int shade = onset.Strength >= 0.6f ? 2 : onset.Strength >= 0.2f ? 1 : 0;
                var brush = OnsetBrushes[drums ? (int)onset.Band : 0][shade];
                double along = projection.AlongOf(onset.Seconds);

                // A highway's strip is left of the lanes, so its first band is the one beside them.
                double near = vertical ? area.Right - ((band + 1) * OnsetBandSize) : area.Top + (band * OnsetBandSize);
                var tick = vertical
                    ? new Rect(near + 1, along - 0.75, OnsetBandSize - 2, 1.5)
                    : new Rect(along - 0.75, near + 1, 1.5, OnsetBandSize - 2);
                context.FillRectangle(brush, tick);
            }
        }

        /// <summary>
        /// The proposed tempo map being reviewed, as a ghost over the real one (charter-spec C14 item 4), and Find first
        /// downbeat's guess (C14 item 5).
        /// </summary>
        private void DrawTempoProposal(DrawingContext context, EditorSession session, LaneProjection projection, Size size, RailPass pass)
        {
            bool lines = (pass & RailPass.Lines) != 0, texts = (pass & RailPass.Text) != 0;
            var labels = LabelsArea(size, projection);
            bool highway = View == LaneView.Highway;
            double across1 = projection.LaneFrom, across2 = projection.LaneFrom + (projection.LaneSize * projection.Slots.Slots.Count);
            double from = Math.Min(projection.Start, projection.End), to = Math.Max(projection.Start, projection.End);

            var proposal = session.TempoProposal;
            if (proposal.IsActive)
            {
                var beats = proposal.Beats;
                foreach (var (first, last) in proposal.Unsure)
                {
                    if (beats[last] < from || beats[first] > to)
                        continue;
                    double a = projection.AlongOf(beats[first]), b = projection.AlongOf(beats[last]);
                    if (lines)
                        context.FillRectangle(UnsureBrush, Box(a, b, across1, across2));
                    if (!texts)
                        continue;

                    var text = Label(Strings.ProposalUnsureLabel, 10, UnsureLabelBrush, highway ? Math.Max(1, labels.Width - 4) : null);
                    if (!lines)
                        DrawStandingLabel(context, text, new Point(labels.X + 2, Math.Max(a, b)), rightAligned: false);
                    else
                        text.Draw(context, highway ? new Point(labels.X + 2, Math.Max(a, b) - text.Height) : new Point(Math.Min(a, b) + 3, labels.Bottom - text.Height));
                }

                int perMeasure = Math.Max(1, proposal.BeatsPerMeasure);
                int start = Math.Max(0, BeatMath.NearestIndex(beats, from, double.PositiveInfinity) - 1);
                for (int i = start; lines && i < beats.Count && beats[i] <= to + 1; i++)
                {
                    double along = projection.AlongOf(beats[i]);
                    bool measure = ((i - proposal.Downbeat) % perMeasure + perMeasure) % perMeasure == 0;
                    Line(context, measure ? ProposalMeasurePen : ProposalBeatPen, At(along, across1), At(along, across2));

                    // The handle, in the margin beside the lanes, that drags the beat.
                    var handle = highway ? new Point(labels.Right - 7, along) : new Point(along, labels.Bottom - 7);
                    using (context.PushTransform(Matrix.CreateTranslation(handle.X, handle.Y)))
                        context.DrawGeometry(proposal.Pinned[i] ? ProposalHandleBrush : null, ProposalHandlePen, Diamond);
                }
            }

            if (_editor?.AudioAssist.Downbeat is { } downbeat && downbeat >= from && downbeat <= to)
            {
                double along = projection.AlongOf(downbeat);
                if (lines)
                    Line(context, DownbeatPen, At(along, highway ? labels.Left : labels.Top), At(along, across2));
                if (!texts)
                    return;

                var text = Label(Strings.DownbeatGhostLabel, 10, DownbeatLabelBrush);
                if (!lines)
                    DrawStandingLabel(context, text, new Point(labels.X + 2, along), rightAligned: false, raise: 2);
                else
                    text.Draw(context, highway ? new Point(labels.X + 2, along - text.Height - 2) : new Point(along + 3, labels.Y));
            }
        }

        /// <summary>The proposed beat whose handle is under <paramref name="position"/>, or null.</summary>
        private int? ProposalHandleAt(Point position)
        {
            if (Session is not { TempoProposal.IsActive: true } session || CurrentProjection() is not { } projection)
                return null;
            if (!LabelsArea(Bounds.Size, projection).Contains(position))
                return null;
            double along = AlongOf(position);
            double seconds = projection.SecondsAt(along);
            double tolerance = Math.Abs(projection.SecondsAt(along + HandlePixels) - seconds);
            return session.TempoProposal.HitTest(seconds, tolerance);
        }

        /// <summary>Drags proposed beat <paramref name="beat"/> to the pointer, onto an onset of the mix near it with snap to onset on.</summary>
        private void DragProposedBeat(int beat, Point position)
        {
            if (Session is not { } session || CurrentProjection() is not { } projection || _editor is not { } editor)
                return;
            double seconds = projection.SecondsAt(AlongOf(position));
            if (session.SnapToOnset && editor.Audio.Mix is { } mix)
            {
                var onsets = editor.Audio.Analysis.OnsetTimesAt(mix, drumBands: true, OnsetSensitivity);
                double reach = Math.Abs(projection.SecondsAt(AlongOf(position) + (HandlePixels * 2)) - seconds);
                if (BeatMath.NearestIndex(onsets, seconds, reach) is >= 0 and var index)
                    seconds = onsets[index];
            }

            session.TempoProposal.MoveBeat(beat, seconds);
        }

        /// <summary>The lane's layout as the canvas has it now, for the pointer; null without a part to show.</summary>
        private LaneProjection? CurrentProjection()
        {
            if (Session is not { } session || _lane is null)
                return null;
            if (_lane.Vocal is { } vocal)
                return session.VocalsOf(vocal) is { } vocals ? ProjectVocals(Bounds.Size, VocalSlotsOf(vocals), session.Viewport, _lane.Length) : null;
            if (_lane.Track is not { } key || session.TrackOf(key) is null)
                return null;
            var slots = session.SlotsOf(key.Instrument, View);
            var rows = PhraseRail.RowsOf(ChartInstruments.KindOf(key.Instrument));
            Extras = ExtrasOf(Bounds.Size, key);
            return Project(Bounds.Size, slots, session.Viewport, _lane.Length, rows.Count);
        }

        // The sensitivity a proposed beat snaps to the mix's onsets at: the editor's default.
        private const double OnsetSensitivity = 0.5;

        /// <summary>
        /// Draws the song's page again shortly, to carry on with pictures of the audio there was no time for, or not yet
        /// worked out: not every frame, as the page and its picture are drawn whole each time.
        /// </summary>
        private void PostRedraw()
        {
            if (_isRedrawPosted)
                return;
            _isRedrawPosted = true;
            if (_redrawTimer is null)
            {
                _redrawTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = RedrawDelay };
                _redrawTimer.Tick += (_, _) =>
                {
                    _redrawTimer.Stop();
                    _isRedrawPosted = false;
                    _song.Redraw();
                };
            }

            _redrawTimer.Start();
        }

        /// <summary>
        /// The loop region, boxed in light green across the lanes: drawn wherever a region is, whether or not the song is
        /// repeating it just now (charter-spec C10 item 2). And the selected span.
        /// </summary>
        private void DrawSpans(DrawingContext context, EditorSession session, LaneProjection projection)
        {
            if (Loop is { } loop)
                context.DrawRectangle(LoopBrush, LoopPen, LoopBandOf(projection, loop));
            if (session.Selection.Range is { } range && range.To > range.From)
            {
                double across1 = projection.LaneFrom, across2 = projection.LaneFrom + (projection.LaneSize * projection.Slots.Slots.Count);
                context.FillRectangle(SpanBrush, Box(projection.AlongOf(session.AudioAt(range.From)), projection.AlongOf(session.AudioAt(range.To)), across1, across2));
            }
        }

        /// <summary>The loop region playback holds, in seconds of audio; null with none.</summary>
        private LoopRegion? Loop => _editor?.Playback.Loop;

        /// <summary>The band the loop region covers, from its start up to its end, across the lanes.</summary>
        private Rect LoopBandOf(LaneProjection projection, LoopRegion loop)
        {
            double across1 = projection.LaneFrom, across2 = projection.LaneFrom + (projection.LaneSize * projection.Slots.Slots.Count);
            return Box(projection.AlongOf(loop.Start), projection.AlongOf(loop.End), across1, across2);
        }

        /// <summary>
        /// On the synchronized region's lane, the region outlined and each passage synced with it as an orange band, behind
        /// the notes; a region nothing matched is hashed in yellow instead. And the region being dragged out with the Sync
        /// tool.
        /// </summary>
        private void DrawSyncBands(DrawingContext context, EditorSession session, ChartTrackKey key, LaneProjection projection)
        {
            var sync = session.Sync;
            if (sync.Track == key && sync.Span is var (regionFrom, regionTo))
            {
                long length = regionTo - regionFrom;
                foreach (long start in sync.Matches)
                    context.FillRectangle(SyncedBrush, SyncBandOf(session, projection, start, start + length));
                var band = SyncBandOf(session, projection, regionFrom, regionTo);
                if (sync.IsUnmatched)
                    DrawSyncHash(context, band);
                else
                    context.DrawRectangle(SyncRegionBrush, SyncRegionPen, band);
            }

            if (sync.Draft is var (track, draftFrom, draftTo) && track == key)
                context.DrawRectangle(SyncRegionBrush, SyncRegionPen, SyncBandOf(session, projection, draftFrom, draftTo));
        }

        /// <summary>The band the synchronized region, or a passage synced with it, covers from its start up to its end.</summary>
        private Rect SyncBandOf(EditorSession session, LaneProjection projection, long from, long to)
        {
            double across1 = projection.LaneFrom, across2 = projection.LaneFrom + (projection.LaneSize * projection.Slots.Slots.Count);
            return Box(projection.AlongOf(session.AudioAt(from)), projection.AlongOf(session.AudioAt(to)), across1, across2);
        }

        /// <summary>A synchronized region nothing matched: hashed in yellow over its wash, inside its outline.</summary>
        private void DrawSyncHash(DrawingContext context, Rect band)
        {
            context.DrawRectangle(SyncUnmatchedBrush, SyncUnmatchedPen, band);
            using (context.PushClip(band))
            {
                for (double x = band.Left - band.Height; x < band.Right; x += SyncHashGap)
                    Line(context, SyncHashPen, new Point(x, band.Bottom), new Point(x + band.Height, band.Top));
            }
        }

        /// <summary>
        /// Where the small X that clears the synchronized region or the loop region goes: at the top right of the band it
        /// covers, kept within the lanes so it can always be reached.
        /// </summary>
        private static Rect CloseRectOf(Rect band, Rect lanes)
        {
            double left = Math.Clamp(Math.Min(band.Right, lanes.Right) - CloseSize, lanes.Left, Math.Max(lanes.Left, lanes.Right - CloseSize));
            return new(left, Math.Max(band.Top, lanes.Top), CloseSize, CloseSize);
        }

        /// <summary>Whether the pointer at <paramref name="position"/> is on the X that clears the synchronized region.</summary>
        private bool SyncCloseAt(Point position)
        {
            if (Session is not { } session || _lane?.Track is not { } key || session.Sync is not { IsActive: true } sync || sync.Track != key)
                return false;
            if (CurrentProjection() is not { } projection || sync.Span is not var (from, to))
                return false;
            var close = CloseRectOf(SyncBandOf(session, projection, from, to), LanesOf(projection, projection.Slots));
            return close.Inflate(ClickSlop).Contains(position);
        }

        /// <summary>
        /// A faint tint across the lanes, behind the notes, over every phrase and the difficulty's disco flip ranges
        /// (charter-spec C8), each in its kind's colour, with its start, and its end where it has one, marked by a
        /// stronger line of that colour. Overlapping tints show through each other.
        /// </summary>
        private void DrawPhraseRanges(DrawingContext context, EditorSession session, ChartTrackKey key, LaneProjection projection, double fromTick, double toTick)
        {
            var ranges = _bands;
            ranges.Clear();
            foreach (var phrase in _phrasesShown)
            {
                double start = projection.AlongOf(session.AudioAt(phrase.Tick));
                double end = projection.AlongOf(session.AudioAt(phrase.CoverEnd));
                ranges.Add(RangeOf(start, end, PhraseColors.Of(phrase.Kind), PhraseColors.TintOf(phrase.Kind), projection.View));
            }

            double farEnd = projection.AlongOf(projection.End);
            foreach (var range in session.DiscoFlipRangesOf(key))
            {
                if (range.Tick > toTick || range.EndTick < fromTick)
                    continue;
                double start = projection.AlongOf(session.AudioAt(range.Tick));
                double end = range.EndTick is { } endTick ? projection.AlongOf(session.AudioAt(endTick)) : farEnd;
                ranges.Add(RangeOf(start, end, PhraseColors.DiscoFlip, PhraseColors.TintAlpha, projection.View, hasEnd: range.EndTick is not null));
            }

            if (ranges.Count == 0)
                return;

            // Cut to the page, like the tints, so a range reaching far past it doesn't make its picture bigger than the page.
            double across1 = projection.LaneFrom, across2 = projection.LaneFrom + (projection.LaneSize * projection.Slots.Slots.Count);
            HighwayChrome.DrawBands(context, _gems, projection.View, CollectionsMarshal.AsSpan(ranges), across1, across2, _alongLimit.From, _alongLimit.To, asRect: _camera is not null);
        }

        /// <summary>A range's start and end along the time axis, in the order it runs: at least two pixels long, so one with no length shows.</summary>
        private static BandGlyph RangeOf(double start, double end, Color color, byte alpha, LaneView view, bool hasEnd = true)
        {
            if (Math.Abs(end - start) < 2)
                end = start + (view == LaneView.Highway ? -2 : 2);
            return new BandGlyph { Start = start, End = end, Color = color, TintAlpha = alpha, HasEnd = hasEnd };
        }

        /// <summary>
        /// The phrase rail's rows, each a dark shade of its kind's colour (charter-spec C8). Drawn under the rest of the
        /// song so the marks across the rail, as a timeline's section lines, still show in it.
        /// </summary>
        private void DrawPhraseRailBackground(DrawingContext context, IReadOnlyList<RailKind> rows, LaneProjection projection)
        {
            double along1 = projection.TimeFrom, along2 = projection.TimeFrom + projection.TimeLength;
            for (int i = 0; i < rows.Count; i++)
            {
                var (from, to) = PhraseRowOf(projection, rows.Count, i);
                context.FillRectangle(_gems.Brush(PhraseColors.RailBackground(rows[i])), Box(along1, along2, from, to));
            }
        }

        /// <summary>
        /// The phrase rail (charter-spec C8): a row for each kind the part can have, with each phrase a bar in its kind's
        /// row, so phrases that overlap don't hide each other. The selected ones are lit and outlined.
        /// </summary>
        private void DrawPhraseRail(DrawingContext context, EditorSession session, EditablePart part, IReadOnlyList<RailKind> rows, LaneProjection projection)
        {
            var selection = session.Selection;
            var accent = ((AccentBrush ?? DefaultAccentBrush) as ISolidColorBrush)?.Color ?? Color.FromRgb(0x7C, 0xB4, 0xFF);
            foreach (var phrase in _phrasesShown)
            {
                int row = IndexOf(rows, PhraseRail.RowOf(part.Kind, phrase.Kind));
                if (row < 0)
                    continue;
                var (from, to) = PhraseRowOf(projection, rows.Count, row);
                var bar = BoxOf(projection, session, phrase, from, to);
                var color = PhraseColor(phrase.Kind);
                if (selection.Contains(part.Instrument, phrase))
                    context.DrawRectangle(_gems.Brush(HighwayPalette.Lighten(color, 0.45)), _gems.Pen(accent, 1.5), bar.Inflate(1), 2, 2);
                else
                    context.DrawRectangle(_gems.Brush(color), null, bar, 2, 2);
            }
        }

        /// <summary>
        /// The disco flip sections of the lane's difficulty (charter-spec C6 item 7) as bars in the rail's last row, as
        /// phrases are drawn. A section a loaded file never ends runs on to the far end of the view.
        /// </summary>
        private void DrawDiscoFlips(DrawingContext context, EditorSession session, ChartTrackKey key, IReadOnlyList<RailKind> rows, LaneProjection projection, double fromTick, double toTick)
        {
            int row = IndexOf(rows, RailKind.DiscoFlip);
            if (row < 0)
                return;
            var (across1, across2) = PhraseRowOf(projection, rows.Count, row);
            var accent = ((AccentBrush ?? DefaultAccentBrush) as ISolidColorBrush)?.Color ?? Color.FromRgb(0x7C, 0xB4, 0xFF);
            double farEnd = projection.AlongOf(projection.End);
            foreach (var range in session.DiscoFlipRangesOf(key))
            {
                if (range.Tick > toTick || range.EndTick < fromTick)
                    continue;
                double start = projection.AlongOf(session.AudioAt(range.Tick));
                double end = range.EndTick is { } endTick ? projection.AlongOf(session.AudioAt(endTick)) : farEnd;
                if (Math.Abs(end - start) < 2)
                    end = start + (View == LaneView.Highway ? -2 : 2);
                var bar = Box(start, end, across1, across2);
                if (session.Selection.Contains(new DiscoFlipSection(key.Difficulty, range)))
                    context.DrawRectangle(_gems.Brush(HighwayPalette.Lighten(DiscoFlipColor, 0.45)), _gems.Pen(accent, 1.5), bar.Inflate(1), 2, 2);
                else
                    context.DrawRectangle(_gems.Brush(DiscoFlipColor), null, bar, 2, 2);
            }
        }

        /// <summary>
        /// A solo's note count on the difficulty shown, at its start (charter-spec C8 item 2): only for the solos that start
        /// on screen, as the others' counts aren't seen.
        /// </summary>
        /// <param name="upright">Whether the counts stand up beside a perspective highway's plane, to be read, rather than lie on it.</param>
        private void DrawSoloCounts(DrawingContext context, EditorSession session, EditableTrack track, LaneProjection projection, double fromTick, double toTick, bool upright)
        {
            long from = (long)Math.Floor(fromTick) - 1, to = (long)Math.Ceiling(toTick);
            foreach (var phrase in _phrasesShown)
            {
                if (phrase.Kind != PhraseKind.Solo || phrase.Tick < from || phrase.Tick > to)
                    continue;
                var text = Label(PhraseRail.NoteCount(track, phrase).ToString(CultureInfo.CurrentCulture), 9, SoloLabelBrush);
                double along = projection.AlongOf(session.AudioAt(phrase.Tick));
                if (upright)
                {
                    DrawStandingLabel(context, text, new Point(projection.LaneFrom + 3, along), rightAligned: false, raise: 2, badge: BadgeBrush);
                    continue;
                }

                var at = View == LaneView.Highway
                    ? new Point(projection.LaneFrom + 3, along - text.Height - 2)
                    : new Point(along + 3, projection.LaneFrom + 2);
                context.FillRectangle(BadgeBrush, new Rect(at, new Size(text.Width, text.Height)).Inflate(1));
                text.Draw(context, at);
            }
        }

        /// <summary>Finds the part's phrases that reach into the ticks shown, for this frame's passes.</summary>
        private void FindPhrasesShown(EditablePart part, double fromTick, double toTick)
        {
            _phrasesShown.Clear();
            long from = (long)Math.Floor(fromTick);
            var phrases = part.Phrases;
            for (int i = 0, end = phrases.FirstAfter((long)Math.Ceiling(toTick)); i < end; i++)
            {
                if (phrases[i].CoverEnd >= from)
                    _phrasesShown.Add(phrases[i]);
            }
        }

        /// <summary>The box a phrase spans along the time axis, between two points across it: at least two pixels long, so one with no length shows.</summary>
        private Rect BoxOf(LaneProjection projection, EditorSession session, Phrase phrase, double across1, double across2)
        {
            double start = projection.AlongOf(session.AudioAt(phrase.Tick));
            double end = projection.AlongOf(session.AudioAt(phrase.CoverEnd));
            if (Math.Abs(end - start) < 2)
                end = start + (projection.View == LaneView.Highway ? -2 : 2);
            return Box(start, end, across1, across2);
        }

        private static int IndexOf(IReadOnlyList<RailKind> rows, RailKind kind)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i] == kind)
                    return i;
            }

            return -1;
        }

        /// <summary>The colour of a kind of phrase in the rail: star power as its notes are drawn, the others each their own.</summary>
        private static Color PhraseColor(PhraseKind kind) => PhraseColors.Of(kind);

        /// <summary>
        /// What the labels' margin and the lines across the lanes are made of, which a perspective highway draws apart: the lines lie on
        /// the plane, and the text beside them stands up, to be read.
        /// </summary>
        [Flags]
        private enum RailPass
        {
            Lines = 1,
            Text = 2,
            Both = Lines | Text,
        }

        /// <summary>What the song layer draws of the rail: all of it on a flat highway, the lines alone on a perspective one.</summary>
        private RailPass RailOnPlane => _camera is null ? RailPass.Both : RailPass.Lines;

        /// <summary>The grid's steps faintly, then the beat and measure lines, with the measures' numbers in the labels' margin.</summary>
        private void DrawBeats(DrawingContext context, EditorSession session, TempoMap map, LaneProjection projection, double fromTick, double toTick, Size size, RailPass pass)
        {
            bool lines = (pass & RailPass.Lines) != 0, text = (pass & RailPass.Text) != 0;
            double across1 = projection.LaneFrom, across2 = projection.LaneFrom + (projection.LaneSize * projection.Slots.Slots.Count);
            long first = (long)Math.Floor(fromTick), last = (long)Math.Ceiling(toTick);

            // The snap's steps, when they're far enough apart to see.
            var snap = session.Snap;
            double step = snap.StepTicks(map.Resolution);
            if (lines && step > 0 && Math.Abs(projection.AlongOf(session.AudioAt(first + step)) - projection.AlongOf(session.AudioAt(first))) >= 6)
            {
                long tick = snap.Step(Math.Max(0, first - 1), 1, map);
                for (int guard = 0; tick <= last && guard < 4000; guard++)
                {
                    double along = projection.AlongOf(session.AudioAt(tick));
                    Line(context, GridPen, At(along, across1), At(along, across2));
                    tick = snap.Step(tick, 1, map);
                }
            }

            var labels = LabelsArea(size, projection);
            Measures(map, last);
            var beatLines = _beatLines;
            beatLines.Clear();
            map.BeatLines(first, last, beatLines);
            foreach (var line in beatLines)
            {
                double along = projection.AlongOf(session.AudioAt(line.Tick));
                if (lines)
                    HighwayChrome.DrawBeatLine(context, View, along, across1, across2, line.IsMeasure, asRect: _camera is not null);
                if (!text || !line.IsMeasure)
                    continue;

                int number = _measures.BinarySearch(line.Tick);
                if (number < 0)
                    continue;
                var numberText = Label(MeasureNumber(number), 10, LabelBrush ?? DefaultLabelBrush);
                if (!lines)
                {
                    DrawStandingLabel(context, numberText, new Point(labels.Right - 4, along), rightAligned: true);
                    continue;
                }

                var at = View == LaneView.Highway
                    ? new Point(labels.Right - numberText.Width - 4, along - numberText.Height)
                    : new Point(along + 3, labels.Bottom - numberText.Height - 1);
                numberText.Draw(context, at);
            }
        }

        /// <summary>The tempo markers: a line across the lanes, the tempo and any time signature in the labels' margin, and whether each is anchored.</summary>
        private void DrawTempoMarkers(DrawingContext context, EditorSession session, LaneProjection projection, double fromTick, double toTick, Size size, RailPass pass)
        {
            bool lines = (pass & RailPass.Lines) != 0, texts = (pass & RailPass.Text) != 0;
            var tempoMap = session.Chart.TempoMap;
            var labels = LabelsArea(size, projection);
            double across1 = projection.LaneFrom, across2 = projection.LaneFrom + (projection.LaneSize * projection.Slots.Slots.Count);
            var accent = AccentBrush ?? DefaultAccentBrush;
            foreach (var marker in tempoMap.Tempos.Between((long)Math.Floor(fromTick), (long)Math.Ceiling(toTick)))
            {
                double along = projection.AlongOf(session.AudioAt(marker.Tick));
                bool selected = session.Selection.TempoTicks.Contains(marker.Tick);
                if (lines)
                    Line(context, TempoPen, At(along, across1), At(along, across2));
                if (!texts)
                    continue;

                var text = Label(TempoText(tempoMap, marker), 10, selected ? accent : TempoBrush);
                if (!lines)
                {
                    DrawStandingLabel(context, text, new Point(labels.X + 2, along), rightAligned: false, badge: selected ? BadgeBrush : null);
                    continue;
                }

                var at = View == LaneView.Highway
                    ? new Point(labels.X + 2, along + 1)
                    : new Point(along + 3, labels.Y + 1);
                if (selected)
                    context.FillRectangle(BadgeBrush, new Rect(at, new Size(text.Width, text.Height)).Inflate(1));
                text.Draw(context, at);
            }

            // Time signatures with no tempo marker at their tick.
            foreach (var signature in tempoMap.TimeSignatures.Between((long)Math.Floor(fromTick), (long)Math.Ceiling(toTick)))
            {
                if (!tempoMap.Tempos.At(signature.Tick).IsEmpty)
                    continue;
                double along = projection.AlongOf(session.AudioAt(signature.Tick));
                if (lines)
                    Line(context, TempoPen, At(along, across1), At(along, across2));
                if (!texts)
                    continue;

                var text = Label(SignatureText(signature), 10, TempoBrush);
                if (!lines)
                    DrawStandingLabel(context, text, new Point(labels.X + 2, along), rightAligned: false);
                else
                    text.Draw(context, View == LaneView.Highway ? new Point(labels.X + 2, along + 1) : new Point(along + 3, labels.Y + 1));
            }
        }

        /// <summary>A tempo marker's label: its tempo, any time signature at its tick, and whether it's anchored; made once for each.</summary>
        private string TempoText(EditableTempoMap tempoMap, TempoChange marker)
        {
            bool anchored = !tempoMap.Anchors.At(marker.Tick).IsEmpty;
            var signatures = tempoMap.TimeSignatures.At(marker.Tick);
            var (numerator, denominator) = signatures.IsEmpty ? (0, 0) : (signatures[^1].Numerator, signatures[^1].Denominator);
            var key = (marker.Tick, marker.MicrosecondsPerQuarter, anchored, numerator, denominator);
            if (_tempoTexts.TryGetValue(key, out string? text))
                return text;

            string bpm = EditableTempoMap.BpmOf(marker.MicrosecondsPerQuarter).ToString("0.###", CultureInfo.CurrentCulture);
            if (!signatures.IsEmpty)
                bpm += " " + SignatureText(signatures[^1]);
            text = (anchored ? "⚓ " : "") + bpm;
            if (_tempoTexts.Count >= MaxRailTexts)
                _tempoTexts.Clear();
            return _tempoTexts[key] = text;
        }

        /// <summary>A time signature's label, made once for each.</summary>
        private string SignatureText(TimeSignatureMarker signature)
        {
            var key = (signature.Numerator, signature.Denominator);
            if (!_signatureTexts.TryGetValue(key, out string? text))
                _signatureTexts[key] = text = string.Create(CultureInfo.CurrentCulture, $"{signature.Numerator}/{signature.Denominator}");
            return text;
        }

        /// <summary>The name of the section an event's <paramref name="text"/> is, or null when it isn't one; worked out once for each text.</summary>
        private string? SectionName(string text)
        {
            if (_sectionNames.TryGetValue(text, out string? name))
                return name;
            if (_sectionNames.Count >= MaxRailTexts)
                _sectionNames.Clear();
            return _sectionNames[text] = SectionEvents.NameOf(text);
        }

        /// <summary>
        /// The sections (charter-spec C8 item 3): a faint line across the lanes and the labels' margin at each one's start,
        /// and its name in the margin. A highway's name is above the measure number, cut short to the rail's width; a
        /// timeline's is in the ruler, after the measure number and any tempo there.
        /// </summary>
        private void DrawSections(DrawingContext context, EditorSession session, LaneProjection projection, double fromTick, double toTick, Size size, RailPass pass)
        {
            bool lines = (pass & RailPass.Lines) != 0, texts = (pass & RailPass.Text) != 0;
            var labels = LabelsArea(size, projection);
            bool highway = View == LaneView.Highway;
            double across1 = highway ? labels.Left : labels.Top;
            double across2 = projection.LaneFrom + (projection.LaneSize * projection.Slots.Slots.Count);
            var tempoMap = session.Chart.TempoMap;
            foreach (var e in session.Chart.Events.Between((long)Math.Floor(fromTick), (long)Math.Ceiling(toTick)))
            {
                if (SectionName(e.Text) is not { } name)
                    continue;
                double along = projection.AlongOf(session.AudioAt(e.Tick));
                if (lines)
                    Line(context, SectionPen, At(along, across1), At(along, across2));
                if (!texts)
                    continue;

                // The measure number drawn at its tick, when it starts a measure.
                int measure = _measures.BinarySearch(e.Tick);
                var number = measure >= 0 ? Label(MeasureNumber(measure), 10, LabelBrush ?? DefaultLabelBrush) : null;
                if (highway)
                {
                    var text = Label(name, 10, SectionBrush, Math.Max(1, labels.Width - 4));
                    if (!lines)
                        DrawStandingLabel(context, text, new Point(labels.X + 2, along), rightAligned: false, raise: number?.Height ?? 0);
                    else
                        text.Draw(context, new Point(labels.X + 2, along - (number?.Height ?? 0) - text.Height));
                    continue;
                }

                double before = number?.Width ?? 0;
                var tempos = tempoMap.Tempos.At(e.Tick);
                var signatures = tempoMap.TimeSignatures.At(e.Tick);
                if (!tempos.IsEmpty)
                    before = Math.Max(before, Label(TempoText(tempoMap, tempos[^1]), 10, TempoBrush).Width);
                else if (!signatures.IsEmpty)
                    before = Math.Max(before, Label(SignatureText(signatures[^1]), 10, TempoBrush).Width);
                var label = Label(name, 10, SectionBrush);
                var at = new Point(along + 3 + (before > 0 ? before + 6 : 0), labels.Y + Math.Max(0, (labels.Height - label.Height) / 2));
                context.FillRectangle(BadgeBrush, new Rect(at, new Size(label.Width, label.Height)).Inflate(1));
                label.Draw(context, at);
            }
        }

        /// <param name="longest">The longest note's length in <paramref name="track"/>, so a sustain begun before the view is drawn.</param>
        private void DrawNotes(DrawingContext context, EditorSession session, ChartTrackKey key, EditableTrack track, LaneProjection projection, LaneSlots slots, double fromTick, double toTick, long longest, bool isGhost, NotePainter painter, NotePass pass)
        {
            var kind = track.Kind;
            bool drums = kind == ChartInstrumentKind.Drums;
            int laneCount = drums ? slots.Slots.Count - (slots.Across is null ? 1 : 0) : 5;
            var game = isGhost ? null : session.GameTrack(key).Notes;
            var notes = track.Notes.Between((long)Math.Max(0, Math.Floor(fromTick) - longest), (long)Math.Ceiling(toTick) + 1);
            double size = GemSize(projection);

            // The notes as the shared painter draws them, in the order they're drawn from the back.
            var glyphs = _glyphs;
            glyphs.Clear();
            _glyphNotes.Clear();
            for (int i = 0; i < notes.Length; i++)
            {
                var note = notes[i];
                bool across = slots.Across == note.Lane;
                int lane = session.ShownLane(key, note);
                int column = across ? -1 : slots.IndexOf(lane);
                if (column < 0 && !across)
                    continue;

                var parts = NoteGlyphParts.None;

                // A kick has no sustain.
                if (note.Length > EditableNote.PlainLength && !(across && drums))
                    parts |= NoteGlyphParts.Tail;

                // A GHL barre: the black fret covers the top half of its column's white one, as in the game, so it's
                // drawn just after it, in its place, and not as a note of its own.
                bool inView = note.Tick <= toTick + 1 && note.Tick >= fromTick - 1;
                bool coveredByBarre = slots.IsShared(note.Lane) && slots.PartnerOf(note.Lane) is { } white && track.TryGetNote(note.Tick, white, false, out _);
                if (inView && !coveredByBarre)
                    parts |= NoteGlyphParts.Note;
                if (parts == NoteGlyphParts.None)
                    continue;

                var shown = Shown(game, note, drums, lane);
                var color = HighwayPalette.NoteColor(kind, laneCount, shown);
                double along = projection.AlongOf(session.AudioAt(note.Tick));
                if (across)
                    parts |= NoteGlyphParts.Across;
                else if (column == slots.Slots.Count - 1 && slots.Across is null && note.Lane == EditorTools.ApartLane(slots))
                    parts |= NoteGlyphParts.Bar;
                if (isGhost)
                    parts |= NoteGlyphParts.Outlined;

                // Shorter than the sustain cutoff: the games read a plain note, so the tail is only dashed (C5 item 3).
                if (!isGhost && shown.Length == 0)
                    parts |= NoteGlyphParts.DashedTail;

                // Its black fret, if it has one, is drawn over it, so it comes after this one's glyph from the back.
                if ((parts & NoteGlyphParts.Note) != 0 && !across && slots.SharedOf(slots.IndexOf(note.Lane)) is { } black && black != note.Lane && track.TryGetNote(note.Tick, black, false, out var shared))
                    AddBarre(shared);

                _glyphNotes.Add(note);
                glyphs.Add(new NoteGlyph
                {
                    Index = _glyphNotes.Count - 1,
                    Note = shown,
                    Color = color,
                    Column = column,
                    Along = along,
                    TailFrom = along,
                    TailTo = projection.AlongOf(session.AudioAt(note.Tick + note.Length)),
                    Opacity = 1,
                    Parts = parts,
                });
            }

            void AddBarre(EditableNote barre)
            {
                // Inside a disco flip section, on the pad the player hits (charter-spec C6 item 7).
                int lane = session.ShownLane(key, barre);
                int column = slots.IndexOf(lane);
                if (column < 0)
                    return;
                var shown = Shown(game, barre, drums, lane);
                _glyphNotes.Add(barre);
                glyphs.Add(new NoteGlyph
                {
                    Index = _glyphNotes.Count - 1,
                    Note = shown,
                    Color = HighwayPalette.NoteColor(kind, laneCount, shown),
                    Column = column,
                    Along = projection.AlongOf(session.AudioAt(barre.Tick)),
                    Opacity = 1,
                    Parts = NoteGlyphParts.Note | NoteGlyphParts.Barre | (isGhost ? NoteGlyphParts.Outlined : NoteGlyphParts.None),
                });
            }

            _flagged.Clear();
            _decorations.Begin(session, key, track, projection, slots, AccentPens().Selected, isGhost, upright: ReferenceEquals(painter, _uprightNotes));
            painter.Draw(context, projection, kind, CollectionsMarshal.AsSpan(glyphs), size, _decorations, _alongLimit.From, _alongLimit.To, pass);
        }

        /// <summary>What the editor draws over its notes, and in place of a short sustain's tail: its selection rings and forcing marks.</summary>
        private sealed class NoteDecorations : INoteDecorator
        {
            private readonly EditorCanvas _canvas;
            private EditorSession? _session;
            private ChartTrackKey _key;
            private EditableTrack? _track;
            private LaneProjection? _projection;
            private LaneSlots? _slots;
            private IPen? _selectedPen;
            private bool _isGhost;
            private bool _isUpright;

            public NoteDecorations(EditorCanvas canvas)
            {
                _canvas = canvas;
            }

            public void Begin(EditorSession session, ChartTrackKey key, EditableTrack track, LaneProjection projection, LaneSlots slots, IPen selectedPen, bool isGhost, bool upright)
            {
                _session = session;
                _key = key;
                _track = track;
                _projection = projection;
                _slots = slots;
                _selectedPen = selectedPen;
                _isGhost = isGhost;
                _isUpright = upright;
            }

            public void DrawDashedTail(DrawingContext context, in NoteGlyph glyph, Rect tail)
            {
                // Tilted, a dashed outline is a path of many dashes for the graphics card to work out each frame, for each short note: a
                // plain faint fill stands for it, as it still shows the tail is there and is too short to be one.
                if (_canvas._camera is not null)
                    context.FillRectangle(_canvas._gems.Brush(HighwayPalette.WithAlpha(glyph.Color, 0x60)), tail);
                else
                    DashedOutline.DrawRectangle(context, _canvas.ShortTailPen(glyph.Color), tail.Deflate(0.5), 0, ShortTailDash, ShortTailDash);
            }

            public void AfterNote(DrawingContext context, in NoteGlyph glyph, Rect rect)
            {
                if (_isGhost)
                    return;

                var note = _canvas._glyphNotes[glyph.Index];
                bool across = glyph.Has(NoteGlyphParts.Across);
                if (_session!.Selection.IsMarked(_key, note))
                {
                    if (_isUpright)
                        _canvas._upright.DrawSelection(context, rect, _selectedPen!);
                    else
                        context.DrawRectangle(null, _selectedPen, rect.Inflate(2), 3, 3);
                }

                if (!across && _track!.Kind != ChartInstrumentKind.Drums && _canvas._flagged.Add(note.Tick) && _track.FlagsAt(note.Tick) is var flags and not ChordFlags.None)
                {
                    if (_isUpright)
                        _canvas.DrawForcingUpright(context, _projection!, _slots!, glyph.Along, flags);
                    else
                        _canvas.DrawForcing(context, _projection!, _slots!, glyph.Along, flags);
                }
            }
        }

        /// <summary>The marks of a forced or tapped chord, beside it: H, S and T.</summary>
        private void DrawForcing(DrawingContext context, LaneProjection projection, LaneSlots slots, double along, ChordFlags flags)
        {
            string marks = (flags.HasFlag(ChordFlags.ForceHopo) ? "H" : "") + (flags.HasFlag(ChordFlags.ForceStrum) ? "S" : "") + (flags.HasFlag(ChordFlags.Tap) ? "T" : "");
            var text = Label(marks, 9, AccentBrush ?? DefaultAccentBrush);
            double edge = projection.LaneFrom + (projection.LaneSize * slots.Slots.Count);
            var at = View == LaneView.Highway
                ? new Point(edge + 2, along - (text.Height / 2))
                : new Point(along - (text.Width / 2), projection.LaneFrom - text.Height);
            context.FillRectangle(BadgeBrush, new Rect(at, new Size(text.Width, text.Height)).Inflate(1));
            text.Draw(context, at);
        }

        /// <summary>
        /// The note the place tool would put down where the pointer is, as it would be drawn: an open note or a kick with
        /// Alt held, or a fret while it places open notes, and made as the toolbar's place options have it. None with
        /// Shift held, which lends the place tool the select tool's press.
        /// </summary>
        private void DrawHover(DrawingContext context, EditorSession session, ChartTrackKey key, EditableTrack track, LaneProjection projection, LaneSlots slots, IGemPainter gems)
        {
            if (_hover is not { } hover || _isPressed || session.Tool != EditorTool.Place || (_keys & KeyModifiers.Shift) != 0 || PointAt(hover) is not { Rail: null } point)
                return;
            if (session.Tools.PlacedLane(track.Kind, point.Lane, slots, alt: (_keys & KeyModifiers.Alt) != 0) is not { } lane)
                return;
            long tick = session.Quantize(point.Tick, key);
            int column = slots.IndexOf(lane);
            int stored = session.StoredLane(key, tick, lane);
            bool across = slots.Across == lane;
            var note = session.Tools.PlacedNote(key, track.Kind, tick, stored);
            if ((column < 0 && !across) || EditorTools.TryGetPlaced(track, note, out _))
                return;
            double along = projection.AlongOf(session.AudioAt(tick));

            // As it would be placed: numbered and coloured as Shown has it, and on Pro drums a cymbal unless a tom marker
            // covers it or a tom is picked. Inside a disco flip section, the red placed in yellow's column is a yellow
            // cymbal, and the yellow placed in red's a red pad (C6 item 7).
            bool drums = track.Kind == ChartInstrumentKind.Drums;
            int laneCount = drums ? slots.Slots.Count - (slots.Across is null ? 1 : 0) : 5;
            var placing = session.Placing;
            bool cymbal = stored != lane
                ? lane == 2
                : drums && session.Chart.DrumsVariant == DrumsVariant.Pro && lane is >= TomMarker.FirstPad and <= TomMarker.LastPad
                    && !placing.Tom && !DrumCymbals.IsTom(session.Chart.PartOf(key.Instrument)!, lane, tick);
            var flags = cymbal ? NoteFlags.Cymbal : NoteFlags.None;
            if (drums)
            {
                flags |= note.Dynamics switch { NoteDynamics.Accent => NoteFlags.Accent, NoteDynamics.Ghost => NoteFlags.Ghost, _ => NoteFlags.None };
                flags |= note.DoubleKick ? NoteFlags.DoubleKick : NoteFlags.None;
            }
            else
            {
                flags |= placing.ChordType switch { ChordFlags.ForceHopo => NoteFlags.Hopo, ChordFlags.Tap => NoteFlags.Tap, _ => NoteFlags.None };
            }

            var placed = new ChartNote(tick, 0, drums ? lane - 1 : lane, flags);
            var color = HighwayPalette.NoteColor(track.Kind, laneCount, placed);
            double size = GemSize(projection);

            // An open note or kick is a bar: across the highway, or in a timeline's last row.
            if (across || (column == slots.Slots.Count - 1 && slots.Across is null && lane == EditorTools.ApartLane(slots)))
            {
                double thickness = Math.Max(4, size * 0.45);
                var bar = across
                    ? Box(along - (thickness / 2), along + (thickness / 2), projection.LaneFrom + 4, projection.LaneFrom + (projection.LaneSize * slots.Slots.Count) - 4)
                    : GemRect(projection, column, along, size);
                using (context.PushOpacity(0.45))
                    gems.DrawBar(context, track.Kind, placed, color, bar, outlined: true);
                return;
            }

            var rect = GemRect(projection, column, along, size);

            // A GHL black fret on a white one makes a barre, drawn over the white's top half.
            if (slots.IsShared(lane) && slots.PartnerOf(lane) is { } white && track.TryGetNote(tick, white, false, out _))
                rect = rect.WithHeight(rect.Height / 2);
            using (context.PushOpacity(0.45))
                gems.DrawGem(context, track.Kind, placed, color, rect, outlined: true);
        }

        /// <summary>
        /// What a click of the erase tool would take away where the pointer is, in red over it: a note with its sustain, or
        /// in the phrase rail a phrase's bar or a disco flip section's. None with Shift held, which lends the erase tool the
        /// select tool's press.
        /// </summary>
        /// <param name="upright">Whether to draw what stands up from a perspective highway's plane, which is a note's gem, and nothing else.</param>
        private void DrawEraseHover(DrawingContext context, EditorSession session, ChartTrackKey key, EditableTrack track, EditablePart part, IReadOnlyList<RailKind> rows, LaneProjection projection, LaneSlots slots, bool upright)
        {
            if (_hover is not { } hover || _isPressed || session.Tool != EditorTool.Erase || (_keys & KeyModifiers.Shift) != 0 || PointAt(hover) is not { } point)
                return;

            switch (session.Tools.EraseTargetAt(point))
            {
                case { Note: { } note }:
                    if (upright)
                    {
                        DrawNoteMark(context, session, key, track, note, projection, slots, EraseBrush, ErasePen, NoteMarkPart.Gem, upright: true);
                        break;
                    }

                    using (context.PushClip(LanesOf(projection, slots)))
                        DrawNoteMark(context, session, key, track, note, projection, slots, EraseBrush, ErasePen, _camera is null ? NoteMarkPart.All : NoteMarkPart.Tail, upright: false);
                    break;
                case { Phrase: { } phrase } when !upright && IndexOf(rows, PhraseRail.RowOf(part.Kind, phrase.Kind)) is var row and >= 0:
                {
                    var (across1, across2) = PhraseRowOf(projection, rows.Count, row);
                    context.DrawRectangle(EraseBrush, ErasePen, BoxOf(projection, session, phrase, across1, across2).Inflate(1), 2, 2);
                    break;
                }
                case { DiscoFlip: { } flip } when !upright && IndexOf(rows, RailKind.DiscoFlip) is var row and >= 0:
                {
                    var (across1, across2) = PhraseRowOf(projection, rows.Count, row);
                    double start = projection.AlongOf(session.AudioAt(flip.Range.Tick));
                    double end = flip.Range.EndTick is { } endTick ? projection.AlongOf(session.AudioAt(endTick)) : projection.AlongOf(projection.End);
                    if (Math.Abs(end - start) < 2)
                        end = start + (View == LaneView.Highway ? -2 : 2);
                    context.DrawRectangle(EraseBrush, ErasePen, Box(start, end, across1, across2).Inflate(1), 2, 2);
                    break;
                }
            }
        }

        /// <summary>The parts of a note that is marked over.</summary>
        [Flags]
        private enum NoteMarkPart
        {
            /// <summary>The sustain's tail, which lies on the plane.</summary>
            Tail = 1,

            /// <summary>The gem or bar, which stands up from the plane on a perspective highway.</summary>
            Gem = 2,

            All = Tail | Gem,
        }

        /// <summary>
        /// A note marked over, where DrawNotes draws its gem or bar and its sustain's tail: what the erase tool takes away in red, or the
        /// notes a synchronized region syncs in orange.
        /// </summary>
        /// <param name="parts">Which of its parts to mark here: on a perspective highway the tail is marked on the plane and the gem above it.</param>
        /// <param name="upright">Whether the gem's mark is drawn standing up, in the host's layer, rather than flat.</param>
        private void DrawNoteMark(DrawingContext context, EditorSession session, ChartTrackKey key, EditableTrack track, EditableNote note, LaneProjection projection, LaneSlots slots, IBrush? brush, IPen pen, NoteMarkPart parts, bool upright)
        {
            bool across = slots.Across == note.Lane;
            int column = slots.IndexOf(session.ShownLane(key, note));
            if (column < 0 && !across)
                return;
            double size = GemSize(projection);
            double across1 = projection.LaneFrom + 4, across2 = projection.LaneFrom + (projection.LaneSize * slots.Slots.Count) - 4;
            double along = projection.AlongOf(session.AudioAt(note.Tick));
            if ((parts & NoteMarkPart.Tail) != 0 && note.Length > EditableNote.PlainLength && track.Kind != ChartInstrumentKind.Drums)
            {
                double end = projection.AlongOf(session.AudioAt(note.Tick + note.Length));
                double width = Math.Max(3, projection.LaneSize * 0.2);
                var tail = across ? Box(along, end, across1, across2) : Box(along, end, projection.AcrossOf(column) - (width / 2), projection.AcrossOf(column) + (width / 2));
                context.DrawRectangle(brush, pen, tail);
            }

            if ((parts & NoteMarkPart.Gem) == 0)
                return;

            double thickness = Math.Max(4, size * 0.45);
            var gem = across ? Box(along - (thickness / 2), along + (thickness / 2), across1, across2) : GemRect(projection, column, along, size);
            if (upright)
                _upright.DrawMark(context, gem, brush, pen);
            else
                context.DrawRectangle(brush, pen, gem.Inflate(1), 3, 3);
        }

        /// <summary>
        /// Where a press would start drawing, on the grid: a line across the lanes in the colour of the kind drawn, and a
        /// mark in its row of the rail. The Phrase tool draws where a press does in the lanes; the Select, Place and Phrase
        /// tools draw in an empty part of the rail, where the row's kind is drawn whatever's picked. None where a press does
        /// something else, such as on a phrase's bar or a proposed phrase's ghost, with Shift held, which lends the select
        /// tool's press; nor where a press is refused, as a disco flip section is off Pro drums.
        /// </summary>
        private void DrawPhraseHover(DrawingContext context, EditorSession session, ChartTrackKey key, EditablePart part, IReadOnlyList<RailKind> rows, LaneProjection projection, LaneSlots slots)
        {
            if (_hover is not { } hover || _isPressed || (_keys & KeyModifiers.Shift) != 0 || PointAt(hover) is not { } point)
                return;
            if (point.Rail is null && session.Tool != EditorTool.Phrase)
                return;
            if (session.Tools.PhraseDrawAt(point) is not { } draw || !session.Tools.DrawsIn(key, draw.Kind))
                return;

            var brush = _gems.Brush(draw.Kind.Phrase is { } phrase ? PhraseColor(phrase) : DiscoFlipColor);
            double along = projection.AlongOf(session.AudioAt(draw.Tick));
            context.FillRectangle(brush, Box(along - 1, along + 1, projection.LaneFrom, projection.LaneFrom + (projection.LaneSize * slots.Slots.Count)));
            int row = IndexOf(rows, draw.Kind.Phrase is { } drawn ? PhraseRail.RowOf(part.Kind, drawn) : RailKind.DiscoFlip);
            if (row < 0)
                return;
            var (across1, across2) = PhraseRowOf(projection, rows.Count, row);
            context.FillRectangle(brush, Box(along - 1.5, along + 1.5, across1 - 1, across2 + 1));
        }

        /// <summary>
        /// Where a press of the Tempo tool would add a tempo marker: a line across the lanes in the tempo markers' amber,
        /// on the grid step the press would snap to, as the Phrase tool's hover line is drawn. None over a change the press
        /// would drag instead, in the phrase rail, or with Shift held or the Sync tool's press.
        /// </summary>
        private void DrawTempoHover(DrawingContext context, EditorSession session, LaneProjection projection, LaneSlots slots)
        {
            if (_hover is not { } hover || _isPressed || session.Tool != EditorTool.Tempo || (_keys & KeyModifiers.Shift) != 0
                || session.Tools.IsSyncPress(ModifiersOf(_keys)))
            {
                return;
            }

            if (PointAt(hover) is not { Rail: null } point || point.Lane is null || session.Tools.TempoAddAt(point) is not { } tick)
                return;

            double along = projection.AlongOf(session.AudioAt(tick));
            context.FillRectangle(TempoBrush, Box(along - 1, along + 1, projection.LaneFrom, projection.LaneFrom + (projection.LaneSize * slots.Slots.Count)));
        }

        /// <summary>
        /// Where a press of the Sync tool would start drawing a synchronized region: a line across the lanes in the
        /// region's orange, on the grid step the press would snap to, as the Phrase tool's hover line is drawn. None
        /// where a press does something else: on the region or a passage synced with it, on its X, or one that isn't the
        /// Sync tool's press.
        /// </summary>
        private void DrawSyncLine(DrawingContext context, EditorSession session, LaneProjection projection, LaneSlots slots)
        {
            if (_hover is not { } hover || _isPressed || !session.Tools.IsSyncPress(ModifiersOf(_keys)) || SyncCloseAt(hover))
                return;
            if (PointAt(hover) is not { Rail: null } point || session.Tools.SyncDrawStart(point, ModifiersOf(_keys)) is not { } tick)
                return;
            double along = projection.AlongOf(session.AudioAt(tick));
            context.FillRectangle(SyncLineBrush, Box(along - 1, along + 1, projection.LaneFrom, projection.LaneFrom + (projection.LaneSize * slots.Slots.Count)));
        }

        /// <summary>
        /// The notes the synchronized region holds, and each passage synced with it holds, marked over in orange while the
        /// Sync tool is active: just which notes are synced.
        /// </summary>
        private void DrawSyncNotes(DrawingContext context, EditorSession session, ChartTrackKey key, EditableTrack track, LaneProjection projection, LaneSlots slots, bool upright)
        {
            if (session.Sync is not { IsActive: true } sync || sync.Track != key || sync.Span is not var (from, to))
                return;
            if (!session.Tools.IsSyncPress(ModifiersOf(_keys)))
                return;
            long length = to - from;
            foreach (long start in sync.Matches.Prepend(from))
            {
                foreach (var note in track.Notes.Between(start, start + length - 1))
                    DrawNoteMark(context, session, key, track, note, projection, slots, SyncedBrush, SyncRegionPen, upright ? NoteMarkPart.Gem : _camera is null ? NoteMarkPart.All : NoteMarkPart.Tail, upright);
            }
        }

        /// <summary>
        /// The small X at the synchronized region's top right, which clears it with a click whatever the tool, shown while
        /// the pointer is over the region; and with the Sync tool picked, or Ctrl + Shift held, what the pointer would take
        /// hold of: an end to resize, the region's body to move it, or a passage synced with it to make the source.
        /// </summary>
        private void DrawSyncHover(DrawingContext context, EditorSession session, ChartTrackKey key, LaneProjection projection, LaneSlots slots)
        {
            if (_hover is not { } hover || _isPressed || session.Sync is not { IsActive: true } sync || sync.Track != key)
                return;
            if (sync.Span is not var (from, to))
                return;
            var lanes = LanesOf(projection, slots);
            var band = SyncBandOf(session, projection, from, to);
            var close = CloseRectOf(band, lanes);
            if (!band.Inflate(2).Contains(hover) && !close.Inflate(ClickSlop).Contains(hover))
                return;
            DrawSyncClose(context, close);
            if (!session.Tools.IsSyncPress(ModifiersOf(_keys)))
                return;
            if (PointAt(hover) is not { Rail: null } point)
                return;

            long length = to - from;
            foreach (long start in sync.Matches)
            {
                if (point.Tick >= start && point.Tick < start + length)
                {
                    context.DrawRectangle(null, SyncRegionPen, SyncBandOf(session, projection, start, start + length).Inflate(1), 2, 2);
                    return;
                }
            }

            double across1 = projection.LaneFrom, across2 = projection.LaneFrom + (projection.LaneSize * slots.Slots.Count);
            if (Math.Abs(point.Tick - from) <= point.Tolerance || Math.Abs(point.Tick - to) <= point.Tolerance)
            {
                double at = projection.AlongOf(session.AudioAt(Math.Abs(point.Tick - from) <= point.Tolerance ? from : to));
                context.FillRectangle(SyncRegionBrush, Box(at - 1.5, at + 1.5, across1, across2));
            }
            else if (point.Tick > from && point.Tick < to)
            {
                context.DrawRectangle(null, SyncRegionPen, band.Inflate(1), 2, 2);
            }
        }

        /// <summary>Draws the small X at <paramref name="close"/> that clears the synchronized region, or the loop region.</summary>
        private void DrawSyncClose(DrawingContext context, Rect close)
        {
            context.DrawRectangle(SyncCloseBrush, null, close, 3, 3);
            var inner = close.Deflate(close.Width * 0.28);
            Line(context, SyncClosePen, inner.TopLeft, inner.BottomRight);
            Line(context, SyncClosePen, inner.TopRight, inner.BottomLeft);
        }

        /// <summary>
        /// Where a press of the Loop tool would start drawing the loop region: a line across the lanes in the region's
        /// green, on the grid step the press would snap to, as the Phrase tool's hover line is drawn. None where a press
        /// does something else: on the region, on its X, or one that isn't the Loop tool's press.
        /// </summary>
        private void DrawLoopLine(DrawingContext context, EditorSession session, LaneProjection projection, LaneSlots slots)
        {
            if (_hover is not { } hover || _isPressed || !session.Tools.LoopActive || LoopCloseAt(hover))
                return;
            if (!LanesOf(projection, slots).Contains(hover) || BandTickAt(projection, hover) is not { } at)
                return;
            if (session.Tools.LoopDrawStart(at.Tick, at.Tolerance) is not { } tick)
                return;
            double along = projection.AlongOf(session.AudioAt(tick));
            context.FillRectangle(LoopLineBrush, Box(along - 1, along + 1, projection.LaneFrom, projection.LaneFrom + (projection.LaneSize * slots.Slots.Count)));
        }

        /// <summary>
        /// The small X at the loop region's top right, which clears it with a click whatever the tool, shown while the
        /// pointer is over the region in this lane; and with the Loop tool picked, or L held, what the pointer would take
        /// hold of: an end to resize, or the region's body to move it.
        /// </summary>
        private void DrawLoopHover(DrawingContext context, EditorSession session, LaneProjection projection, LaneSlots slots)
        {
            if (_hover is not { } hover || _isPressed || Loop is not { } loop)
                return;
            var lanes = LanesOf(projection, slots);
            var band = LoopBandOf(projection, loop);
            var close = CloseRectOf(band, lanes);
            if (!band.Inflate(2).Contains(hover) && !close.Inflate(ClickSlop).Contains(hover))
                return;
            DrawSyncClose(context, close);
            if (!session.Tools.LoopActive || BandTickAt(projection, hover) is not { } at || session.Tools.LoopGripAt(at.Tick, at.Tolerance) is not { } grip)
                return;

            switch (grip)
            {
                case LoopGrip.Start or LoopGrip.End:
                {
                    double edge = projection.AlongOf(grip == LoopGrip.Start ? loop.Start : loop.End);
                    double across1 = projection.LaneFrom, across2 = projection.LaneFrom + (projection.LaneSize * slots.Slots.Count);
                    context.FillRectangle(LoopBrush, Box(edge - 1.5, edge + 1.5, across1, across2));
                    break;
                }

                case LoopGrip.Body:
                    context.DrawRectangle(null, LoopPen, band.Inflate(1), 2, 2);
                    break;
            }
        }

        /// <summary>Whether the pointer at <paramref name="position"/> is on the X that clears the loop region.</summary>
        private bool LoopCloseAt(Point position)
        {
            if (Loop is not { } loop || CurrentProjection() is not { } projection)
                return false;
            var close = CloseRectOf(LoopBandOf(projection, loop), LanesOf(projection, projection.Slots));
            return close.Inflate(ClickSlop).Contains(position);
        }

        /// <summary>
        /// The tick under <paramref name="position"/>, and how many ticks either way it reaches, in a highway's, a
        /// timeline's or a vocal part's lane alike: what the Loop tool takes hold of there is where it is in the song.
        /// </summary>
        private (double Tick, double Tolerance)? BandTickAt(LaneProjection projection, Point position)
        {
            if (Session is not { } session)
                return null;
            double tick = session.TickAt(projection.SecondsAt(AlongOf(position)));
            return (tick, Math.Max(1, projection.TicksOf(HitPixels, Math.Max(0, tick), session.TempoMap)));
        }

        /// <summary>The strike line's band, on the cursor, behind the notes: dark across the lanes, as in the games.</summary>
        private void DrawStrikeBand(DrawingContext context, EditorSession session, LaneProjection projection)
        {
            double along = projection.AlongOf(session.Viewport.Cursor);
            double across1 = projection.LaneFrom, across2 = projection.LaneFrom + (projection.LaneSize * projection.Slots.Slots.Count);
            double half = Math.Max(3, GemSize(projection) * 0.6);
            HighwayChrome.DrawStrikeBand(context, View, Box(along - half, along + half, across1, across2), StrikeBandBrush, asRect: _camera is not null);
        }

        /// <summary>
        /// The columns the song is playing through, lit as the inspector's highway lights them: a beam rising from the
        /// strike line and the column's target filled in, as each note reaches it, fading, and slowly over a sustain.
        /// </summary>
        private void DrawGlow(DrawingContext context, EditorSession session, ChartTrackKey key, EditableTrack track, LaneProjection projection, LaneSlots slots)
        {
            var kind = track.Kind;
            bool drums = kind == ChartInstrumentKind.Drums;
            int laneCount = drums ? slots.Slots.Count - (slots.Across is null ? 1 : 0) : 5;
            double now = session.Viewport.Cursor;
            var hits = PlayingOf(key) is { } run ? PlaytestHitsAt(run, key, slots, now) : HitsAt(session, key, track, slots, now);
            double strike = projection.AlongOf(now);
            double reach = StrikeGlow.BeamReach * (projection.AlongOf(projection.End) - strike);
            double size = GemSize(projection);
            for (int column = 0; column < slots.Slots.Count; column++)
            {
                double hit = hits[column];
                if (hit <= 0)
                    continue;

                // The timeline's row for open notes or the kick lights in their colour.
                var color = slots.Slots[column] == (drums ? 0 : ChartNote.OpenLane)
                    ? (drums ? HighwayPalette.Kick : HighwayPalette.Open)
                    : HighwayPalette.HitColor(kind, laneCount, column);
                double edge = projection.LaneFrom + (column * projection.LaneSize);
                HighwayChrome.DrawLitBeam(context, View, color, hit, Box(strike, strike + reach, edge, edge + projection.LaneSize));

                // The target, as opaque as the column is lit.
                HighwayChrome.DrawLitTarget(context, _gems, GemRect(projection, column, strike, size), color, hit);
            }
        }

        /// <summary>How brightly each column is lit at <paramref name="now"/>, from 0 to 1: just struck, or sustained.</summary>
        private double[] HitsAt(EditorSession session, ChartTrackKey key, EditableTrack track, LaneSlots slots, double now)
        {
            int count = slots.Slots.Count;
            if (_hits.Length < count)
                _hits = new double[count];
            var hits = _hits;
            Array.Clear(hits, 0, count);

            bool drums = track.Kind == ChartInstrumentKind.Drums;
            var game = session.GameTrack(key).Notes;
            double from = Math.Max(0, session.TickAt(now - StrikeGlow.Lingers));
            var notes = track.Notes.Between((long)Math.Max(0, Math.Floor(from) - LongestOf(ref _longest, key, track)), (long)Math.Ceiling(Math.Max(0, session.TickAt(now))) + 1);
            foreach (var note in notes)
            {
                double start = session.AudioAt(note.Tick);
                if (start > now)
                    continue;

                // Fading over its tail while the games read it as a sustain.
                int lane = session.ShownLane(key, note);
                var shown = Shown(game, note, drums, lane);
                double glow = StrikeGlow.Brightness(now - start, shown.Length > 0 ? session.AudioAt(note.Tick + shown.Length) - start : 0);
                if (glow <= 0)
                    continue;

                // An open note or kick across the highway lights every column; a pad, the column it's drawn in.
                if (slots.Across == note.Lane)
                    StrikeGlow.Light(hits, count, -1, glow);
                else if (slots.IndexOf(lane) is var column and >= 0)
                    StrikeGlow.Light(hits, count, column, glow);
            }

            return hits;
        }

        /// <summary>The cursor's line through the strike band, over the notes and into the labels' margin.</summary>
        private void DrawCursor(DrawingContext context, EditorSession session, LaneProjection projection, Rect lanes)
        {
            double along = projection.AlongOf(session.Viewport.Cursor);
            var pen = AccentPens().Cursor;
            var labels = LabelsArea(Bounds.Size, projection);
            Line(context, pen, At(along, View == LaneView.Highway ? labels.Left : labels.Top), At(along, View == LaneView.Highway ? lanes.Right : lanes.Bottom));
        }

        /// <summary>A gem in <paramref name="column"/> at <paramref name="along"/>.</summary>
        private Rect GemRect(LaneProjection projection, int column, double along, double size) =>
            NotePainter.GemRect(projection, column, along, size, _alongLimit.From, _alongLimit.To);

        // A highway's gem is as deep as its lane is long, not as wide as its lane is: measured on the canvas, not the projection, which
        // a page widens. In perspective the lane is as tall as the host, not the plane, which reaches up past it.
        private double GemSize(LaneProjection projection) => View == LaneView.Highway
            ? GemDepth.For(_camera is { } camera ? camera.HostHeight : Bounds.Height, perspective: _camera is not null)
            : Math.Clamp(projection.LaneSize * 0.9, 8, 16);

        /// <summary>
        /// How the games read <paramref name="note"/>, drawn in <paramref name="shownLane"/>
        /// (<see cref="EditorSession.ShownLane(ChartTrackKey, EditableNote)"/>): its game-view note, with its natural HOPO,
        /// star power and sustain cutoff; or, when they don't read it as written, the note as it is. Its lane is numbered as
        /// the game view's are, which the palette colours by: a drum pad's from red, and the kick as an open note. Inside a
        /// disco flip section, that's the pad the player hits, so a red note finds the yellow cymbal the games make of it,
        /// and is one when there's no game view to find it in (a ghost difficulty's).
        /// </summary>
        private static ChartNote Shown(IReadOnlyList<ChartNote>? game, EditableNote note, bool drums, int shownLane)
        {
            int lane = drums ? (note.Lane == 0 ? ChartNote.OpenLane : shownLane - 1) : note.Lane;
            if (game is not null)
            {
                int lo = 0, hi = game.Count;
                while (lo < hi)
                {
                    int mid = (lo + hi) / 2;
                    if (game[mid].Tick < note.Tick)
                        lo = mid + 1;
                    else
                        hi = mid;
                }

                for (int i = lo; i < game.Count && game[i].Tick == note.Tick; i++)
                {
                    var found = game[i];
                    if (found.Lane == lane && ((found.Flags & NoteFlags.DoubleKick) != 0) == note.DoubleKick)
                        return found;
                }
            }

            var flags = note.DoubleKick ? NoteFlags.DoubleKick : NoteFlags.None;
            if (drums && note.Lane != shownLane && shownLane == 2)
                flags |= NoteFlags.Cymbal;
            return new ChartNote(note.Tick, note.Length, lane, flags);
        }

        /// <summary>The longest note of <paramref name="track"/>, worked out once after each edit.</summary>
        /// <summary>The longest note's length in <paramref name="track"/>, kept in <paramref name="known"/> until the chart changes.</summary>
        private static long LongestOf(ref (ChartTrackKey Track, long Length)? known, ChartTrackKey key, EditableTrack track)
        {
            if (known is { } kept && kept.Track == key)
                return kept.Length;
            long longest = LongestIn(track);
            known = (key, longest);
            return longest;
        }

        private static long LongestIn(EditableTrack track)
        {
            long longest = 0;
            foreach (var note in track.Notes)
                longest = Math.Max(longest, note.Length);
            return longest;
        }

        /// <summary>The selection's, the cursor's and the focus's pens, in the accent, made again only when it changes.</summary>
        private (IBrush Accent, IPen Selected, IPen Cursor, IPen Focus) AccentPens()
        {
            var accent = AccentBrush ?? DefaultAccentBrush;
            if (_accentPens is { } pens && ReferenceEquals(pens.Accent, accent))
                return pens;
            uint color = (accent as ISolidColorBrush)?.Color.ToUInt32() ?? 0xFF7CB4FF;
            (IBrush, IPen, IPen, IPen) made = (accent, new ImmutablePen(color, 2), new ImmutablePen(color, 1.5), new ImmutablePen(color, 1));
            _accentPens = made;
            return made;
        }

        /// <summary>
        /// The pen of a short sustain's outline in <paramref name="color"/>, made once: plain, as the outline's dashes are
        /// drawn as shapes (<see cref="DashedOutline"/>).
        /// </summary>
        private IPen ShortTailPen(Color color)
        {
            if (!_shortTailPens.TryGetValue(color, out var pen))
                _shortTailPens[color] = pen = new ImmutablePen(HighwayPalette.WithAlpha(color, 0xC0).ToUInt32(), 1);
            return pen;
        }

        /// <summary>The ticks of the measures' first beats up to <paramref name="last"/>, for numbering them.</summary>
        private void Measures(TempoMap map, long last)
        {
            if (!ReferenceEquals(map, _measuresOf))
            {
                _measuresOf = map;
                _measures = [];
            }

            long known = _measures.Count > 0 ? _measures[^1] : -1;
            if (known >= last)
                return;
            foreach (var line in map.BeatLines(known + 1, Math.Max(last, known + 1) + (map.Resolution * 64L)))
            {
                if (line.IsMeasure)
                    _measures.Add(line.Tick);
            }
        }

        /// <summary>A label, made once and kept; cut short with an ellipsis at <paramref name="maxWidth"/> when it's given.</summary>
        /// <remarks>
        /// A <see cref="TextLayout"/>, not a <see cref="FormattedText"/>: that lays its text out again each time it's drawn
        /// or measured, which, for the measure numbers alone, was most of the time a frame took to draw. Drawn in an
        /// immutable copy of <paramref name="brush"/>, so it isn't one of the frame's resources.
        /// </remarks>
        private TextLayout Label(string text, double size, IBrush brush, double? maxWidth = null) => _labels.Get(text, size, brush, maxWidth);

        /// <summary>A straight line on the canvas: filled as a rectangle while it's tilted back, which costs far less to draw through the tilt (see <see cref="HighwayChrome.DrawStraight"/>).</summary>
        private void Line(DrawingContext context, IPen pen, Point from, Point to) => HighwayChrome.DrawStraight(context, pen, from, to, _camera is not null);

        /// <summary>Measure <paramref name="index"/>'s number, counting from one, made once.</summary>
        private string MeasureNumber(int index)
        {
            while (_measureNumbers.Count <= index)
                _measureNumbers.Add((_measureNumbers.Count + 1).ToString(CultureInfo.CurrentCulture));
            return _measureNumbers[index];
        }
    }
}
