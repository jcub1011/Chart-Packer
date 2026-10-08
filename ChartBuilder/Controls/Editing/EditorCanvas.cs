using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ChartBuilder.Resources;
using ChartBuilder.ViewModels;
using ChartPacker.Audio.Analysis;
using ChartPacker.Core.Charting.Analysis;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Editing;
using ChartPacker.Core.Charting.Layout;
using ChartPacker.Core.Charts;
using ChartPacker.UI.Controls;
using ChartPacker.UI.Controls.Highway;

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
    /// <para>Only what's on screen is drawn: the notes between the ticks at the view's ends, found by binary search (§11).
    /// While the song plays, each frame moves the cursor with it, which redraws the lane.</para>
    /// </remarks>
    public abstract class EditorCanvas : Control
    {
        public static readonly StyledProperty<IBrush?> AccentBrushProperty =
            AvaloniaProperty.Register<EditorCanvas, IBrush?>(nameof(AccentBrush));

        public static readonly StyledProperty<IBrush?> LabelBrushProperty =
            AvaloniaProperty.Register<EditorCanvas, IBrush?>(nameof(LabelBrush));

        // How near a note's start, or a phrase bar's end in the rail, the pointer hits it, in pixels.
        private const double HitPixels = 7;

        // The phrase rail's rows, across the view, in pixels: each one's thickness, the gap between them, and the margin
        // either side of the rail.
        private const double PhraseRowSize = 6;
        private const double PhraseRowGap = 2;
        private const double PhraseRailMargin = 3;

        private static readonly IBrush LanesBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xF0, 0x08, 0x08, 0x0F));
        private static readonly IBrush RailBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xFF, 0x12, 0x13, 0x1B));
        private static readonly IBrush AlternateRowBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF));
        private static readonly IPen DividerPen = new ImmutablePen(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF).ToUInt32(), 1);
        private static readonly IPen BeatPen = new ImmutablePen(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF).ToUInt32(), 1);
        private static readonly IPen GridPen = new ImmutablePen(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF).ToUInt32(), 1, new ImmutableDashStyle([2, 3], 0));
        private static readonly IPen MeasurePen = new ImmutablePen(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF).ToUInt32(), 2);
        private static readonly IPen TempoPen = new ImmutablePen(Color.FromArgb(0xB0, 0xFF, 0xB0, 0x40).ToUInt32(), 1.5);
        private static readonly IBrush TempoBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x40));
        private static readonly IBrush SpanBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x26, 0x6E, 0x9C, 0xFF));
        private static readonly IBrush LoopBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x18, 0x3C, 0xD2, 0x78));
        private static readonly IBrush BoxBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x30, 0x6E, 0x9C, 0xFF));
        private static readonly IPen BoxPen = new ImmutablePen(Color.FromArgb(0xC0, 0x6E, 0x9C, 0xFF).ToUInt32(), 1);
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
        private static readonly IPen StrikeEdgePen = new ImmutablePen(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF).ToUInt32(), 1);
        private static readonly IBrush PhraseRowBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
        private static readonly Color SoloColor = Color.FromRgb(0xC3, 0x8E, 0xFF);
        private static readonly IBrush StarPowerTintBrush = new ImmutableSolidColorBrush(HighwayPalette.WithAlpha(HighwayPalette.StarPower, 0x16));
        private static readonly IBrush SoloTintBrush = new ImmutableSolidColorBrush(HighwayPalette.WithAlpha(SoloColor, 0x1A));
        private static readonly IBrush SoloLabelBrush = new ImmutableSolidColorBrush(SoloColor);
        private static readonly Color DiscoFlipColor = Color.FromRgb(0xF2, 0x7B, 0xD0);
        private static readonly Color FillColor = Color.FromRgb(0x7C, 0xDB, 0x8A);

        // Generate fills' proposals (charter-spec C6 item 5): a kept one's ghost is a dashed bar, lit, with a faint band across
        // the lanes; a dropped one's only a faint dashed outline, struck through, and a fainter band.
        private static readonly IPen KeptGhostPen = new ImmutablePen(FillColor.ToUInt32(), 1.5, new ImmutableDashStyle([3, 2], 0));
        private static readonly IPen DroppedGhostPen = new ImmutablePen(HighwayPalette.WithAlpha(FillColor, 0x60).ToUInt32(), 1, new ImmutableDashStyle([3, 2], 0));
        private static readonly IBrush KeptGhostBrush = new ImmutableSolidColorBrush(HighwayPalette.WithAlpha(FillColor, 0x50));
        private static readonly IBrush KeptBandBrush = new ImmutableSolidColorBrush(HighwayPalette.WithAlpha(FillColor, 0x1C));
        private static readonly IBrush DroppedBandBrush = new ImmutableSolidColorBrush(HighwayPalette.WithAlpha(FillColor, 0x08));
        private static readonly Color SectionColor = Color.FromRgb(0x5E, 0xEA, 0xD4);
        private static readonly IPen SectionPen = new ImmutablePen(HighwayPalette.WithAlpha(SectionColor, 0x70).ToUInt32(), 1.5);
        private static readonly IBrush SectionBrush = new ImmutableSolidColorBrush(SectionColor);
        private static readonly Typeface LabelFace = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

        private readonly GemPainter _gems = new();
        private readonly Dictionary<string, FormattedText> _labels = [];

        // How brightly each column is lit as the song plays through the strike line.
        private double[] _hits = [];
        private EditorLaneViewModel? _lane;
        private ChartEditorViewModel? _editor;
        private bool _isFrameRequested;
        private bool _isRedrawPosted;
        private bool _isPressed;

        // The proposed beat being dragged by its handle, if one is.
        private int? _proposalDrag;
        private Point? _hover;

        // The wheel's turn not yet taken as a step, as a touchpad turns it a fraction of a notch at a time.
        private double _wheel;
        private Point? _boxFrom;
        private Point _boxTo;
        private ChartPoint _lastPoint;

        // The longest note of the track drawn, so the notes held into view from before it are found; worked out again after an edit.
        private (ChartTrackKey Track, long Length)? _longest;

        // The ticks of the measures' first beats, for numbering them; kept for the tempo map they're for.
        private TempoMap? _measuresOf;
        private List<long> _measures = [];

        // The pointer's shapes over the phrase rail, made once each: a resize arrow over a bar's end, a hand over its body.
        private static readonly Dictionary<StandardCursorType, Cursor> RailCursors = [];

        private StandardCursorType? _cursorType;

        // The phrases of the lane's part that reach into the ticks shown, found once a frame for each of the passes that
        // draw them.
        private readonly List<Phrase> _phrasesShown = [];

        static EditorCanvas()
        {
            FocusableProperty.OverrideDefaultValue<EditorCanvas>(true);
            ClipToBoundsProperty.OverrideDefaultValue<EditorCanvas>(true);
            AffectsRender<EditorCanvas>(AccentBrushProperty, LabelBrushProperty);
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

        /// <summary>How the lane draws the song.</summary>
        protected abstract LaneView View { get; }

        private EditorSession? Session => _editor?.Session;

        /// <summary>
        /// Where the time axis and the lanes are, for a canvas of <paramref name="size"/> showing <paramref name="lookAhead"/>
        /// seconds beyond the strike line, with room kept for a phrase rail of <paramref name="phraseRows"/> rows.
        /// </summary>
        protected abstract LaneProjection Project(Size size, LaneSlots slots, EditorViewport viewport, double lookAhead, int phraseRows);

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

        /// <summary>How thick the phrase rail of <paramref name="phraseRows"/> rows is across the view, in pixels.</summary>
        protected static double PhraseRailSize(int phraseRows) =>
            phraseRows == 0 ? 0 : (phraseRows * PhraseRowSize) + ((phraseRows - 1) * PhraseRowGap) + (2 * PhraseRailMargin);

        /// <summary>The point <paramref name="along"/> the time axis and <paramref name="across"/> the lanes.</summary>
        protected Point At(double along, double across) => View == LaneView.Highway ? new Point(across, along) : new Point(along, across);

        /// <summary>The rectangle between two points along the time axis and two across the lanes.</summary>
        protected Rect Box(double along1, double along2, double across1, double across2)
        {
            var a = At(Math.Min(along1, along2), Math.Min(across1, across2));
            var b = At(Math.Max(along1, along2), Math.Max(across1, across2));
            return new Rect(a, b);
        }

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
            Attach(DataContext as EditorLaneViewModel);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            Detach();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsVisibleProperty && IsVisible)
                RequestFrame();
        }

        public override void Render(DrawingContext context)
        {
            var bounds = new Rect(Bounds.Size);
            context.FillRectangle(RailBrush, bounds);
            if (Session is not { } session || _lane?.Track is not { } key || session.TrackOf(key) is not { } track)
                return;

            var slots = session.SlotsOf(key.Instrument, View);
            var part = session.Chart.PartOf(key.Instrument)!;
            var rows = PhraseRail.RowsOf(part.Kind);
            Extras = ExtrasOf(bounds.Size, key);
            var projection = Project(bounds.Size, slots, session.Viewport, _lane.LookAhead, rows.Count);
            var lanes = Box(projection.TimeFrom, projection.TimeFrom + projection.TimeLength, projection.LaneFrom, projection.LaneFrom + (projection.LaneSize * slots.Slots.Count));
            var map = session.TempoMap;
            double fromTick = Math.Max(0, session.TickAt(projection.Start));
            double toTick = Math.Max(0, session.TickAt(projection.End));

            context.FillRectangle(LanesBrush, lanes);
            DrawLaneDividers(context, projection, slots);
            DrawBackdrop(context, bounds.Size, projection, rows.Count);
            FindPhrasesShown(part, fromTick, toTick);
            DrawPhraseTints(context, session, projection);
            DrawFillBands(context, session, key, projection, fromTick, toTick);
            DrawSpans(context, session, projection);
            DrawBeats(context, session, map, projection, fromTick, toTick, bounds.Size);
            DrawOnsets(context, bounds.Size, projection, key);
            DrawTempoMarkers(context, session, projection, fromTick, toTick, bounds.Size);
            DrawTempoProposal(context, session, projection, bounds.Size);
            DrawSections(context, session, projection, fromTick, toTick, bounds.Size);
            DrawStrikeBand(context, session, projection);
            DrawPhraseRail(context, session, part, rows, projection);
            DrawDiscoFlips(context, session, key, rows, projection, fromTick, toTick);
            DrawFillGhosts(context, session, key, rows, projection, fromTick, toTick);

            using (context.PushClip(lanes))
            {
                if (_editor?.Playback.IsPlaying == true)
                    DrawGlow(context, session, key, track, projection, slots);

                if (_lane.Ghost is { } ghost)
                {
                    using (context.PushOpacity(0.28))
                        DrawNotes(context, session, new ChartTrackKey(key.Instrument, ghost), part[ghost], projection, slots, fromTick, toTick, isGhost: true);
                }

                DrawNotes(context, session, key, track, projection, slots, fromTick, toTick, isGhost: false);
                DrawSoloCounts(context, session, track, projection, fromTick, toTick);
                DrawHover(context, session, key, track, projection, slots);
            }

            DrawCursor(context, session, projection, lanes);
            if (_boxFrom is { } from)
            {
                var box = new Rect(from, _boxTo).Normalize();
                context.DrawRectangle(BoxBrush, BoxPen, box);
            }

            if (IsFocused)
                context.DrawRectangle(null, new ImmutablePen(((AccentBrush ?? DefaultAccentBrush) as ISolidColorBrush)?.Color.ToUInt32() ?? 0xFF7CB4FF, 1), bounds.Deflate(0.5));
        }

        protected override void OnGotFocus(FocusChangedEventArgs e)
        {
            base.OnGotFocus(e);
            if (Session is { } session && _lane?.Track is { } key)
                session.Focused = key;
            InvalidateVisual();
        }

        protected override void OnLostFocus(FocusChangedEventArgs e)
        {
            base.OnLostFocus(e);
            InvalidateVisual();
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var current = e.GetCurrentPoint(this);
            if (!current.Properties.IsLeftButtonPressed || Session is not { } session || PointAt(current.Position) is not { } point)
                return;

            Focus();
            e.Pointer.Capture(this);

            // A proposed beat's handle, while a proposed tempo map is reviewed, comes before the tools.
            if (ProposalHandleAt(current.Position) is { } beat)
            {
                _proposalDrag = beat;
                e.Handled = true;
                return;
            }

            _isPressed = true;
            _lastPoint = point;

            // A box is drawn from a press in the lanes that's on no note, with the select tool.
            _boxFrom = session.Tool == EditorTool.Select && point.Rail is null && session.TrackOf(point.Track) is not null && session.Tools.HitTest(point) is null
                ? current.Position
                : null;
            _boxTo = current.Position;
            session.Tools.Press(point, ModifiersOf(e.KeyModifiers));
            e.Handled = true;
            InvalidateVisual();
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var position = e.GetPosition(this);
            _hover = position;
            if (_proposalDrag is { } beat)
            {
                DragProposedBeat(beat, position);
                return;
            }

            if (_isPressed && Session is { } session && PointAt(position) is { } point)
            {
                _lastPoint = point;
                _boxTo = position;
                session.Tools.Drag(point);
            }
            else if (!_isPressed)
            {
                ShowRailCursor(position);
            }

            InvalidateVisual();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (_proposalDrag is not null)
            {
                _proposalDrag = null;
                e.Pointer.Capture(null);
                return;
            }

            if (!_isPressed)
                return;
            _isPressed = false;
            _boxFrom = null;
            e.Pointer.Capture(null);
            if (Session is { } session)
                session.Tools.Release(PointAt(e.GetPosition(this)) ?? _lastPoint);
            InvalidateVisual();
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            _proposalDrag = null;
            if (!_isPressed)
                return;
            _isPressed = false;
            _boxFrom = null;
            Session?.Tools.Release(_lastPoint);
            InvalidateVisual();
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            _hover = null;
            InvalidateVisual();
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            if (Session is not { } session || _lane is not { Track: not null } lane)
                return;

            double delta = e.Delta.Y;
            if ((e.KeyModifiers & KeyModifiers.Control) != 0)
            {
                // Turned up, the lane looks less far ahead, so the notes are further apart.
                lane.ZoomBy(Math.Pow(0.85, delta));
            }
            else
            {
                // The song moves past the strike line a grid step a notch, through the tempo map, so the strike line stays
                // on the grid across tempo and time-signature changes. Up the highway is later; along a timeline, the
                // wheel turned down is.
                double sign = View == LaneView.Highway ? 1 : -1;
                _wheel += sign * delta;
                int steps = (int)Math.Truncate(_wheel);
                if (steps != 0)
                {
                    _wheel -= steps;
                    session.Tools.StepCursor(steps);
                }
            }

            e.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || _editor is not { } editor || _lane is not { } lane || Session is not { })
                return;
            e.Handled = EditorKeys.Handle(e.PhysicalKey, e.KeyModifiers, editor, lane, this);
        }

        /// <summary>Moves focus to the lane's other canvas, once it's shown in place of this one.</summary>
        internal void FocusSibling()
        {
            Dispatcher.UIThread.Post(() =>
            {
                if ((this.GetVisualParent() as Panel)?.Children.OfType<EditorCanvas>().FirstOrDefault(c => c.IsVisible) is { } shown)
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
        private ChartPoint? PointAt(Point position)
        {
            if (Session is not { } session || _lane?.Track is not { } key || session.TrackOf(key) is null)
                return null;
            var slots = session.SlotsOf(key.Instrument, View);
            var rows = PhraseRail.RowsOf(ChartInstruments.KindOf(key.Instrument));
            Extras = ExtrasOf(Bounds.Size, key);
            var projection = Project(Bounds.Size, slots, session.Viewport, _lane.LookAhead, rows.Count);
            double seconds = projection.SecondsAt(AlongOf(position));
            double tick = session.TickAt(seconds);
            double tolerance = Math.Max(1, projection.TicksOf(HitPixels, Math.Max(0, tick), session.TempoMap));
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
        /// Shows a resize arrow over a phrase or disco flip bar's end in the rail, and a hand over its body, or over a
        /// proposed fill's ghost, which is clicked to keep or drop it.
        /// </summary>
        private void ShowRailCursor(Point position)
        {
            StandardCursorType? wanted = null;
            if (PointAt(position) is { Rail: not null } point && Session is { } session)
            {
                if (session.Fills.HitTest(point) is not null)
                {
                    wanted = StandardCursorType.Hand;
                }
                else if (session.Chart.PartOf(point.Track.Instrument) is { } part
                    && (PhraseRail.HitTest(part, point)?.Grip ?? PhraseRail.HitTestDiscoFlip(point.Track.Difficulty, session.DiscoFlipRangesOf(point.Track), point)?.Grip) is { } grip)
                {
                    wanted = grip == PhraseGrip.Body ? StandardCursorType.Hand
                        : View == LaneView.Highway ? StandardCursorType.SizeNorthSouth
                        : StandardCursorType.SizeWestEast;
                }
            }

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

        private static EditorModifiers ModifiersOf(KeyModifiers keys) =>
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
                editor.Session.Viewport.Changed += OnRedraw;
                editor.Playback.PropertyChanged += OnPlaybackChanged;
                editor.Audio.Changed += OnRedraw;
                editor.AudioAssist.PropertyChanged += OnAudioAssistChanged;
            }

            InvalidateVisual();
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
                editor.Session.Viewport.Changed -= OnRedraw;
                editor.Playback.PropertyChanged -= OnPlaybackChanged;
                editor.Audio.Changed -= OnRedraw;
                editor.AudioAssist.PropertyChanged -= OnAudioAssistChanged;
            }

            _lane = null;
            _editor = null;
        }

        private void OnLaneChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(EditorLaneViewModel.Track) && IsFocused && Session is { } session && _lane?.Track is { } key)
                session.Focused = key;
            _longest = null;
            InvalidateVisual();
        }

        private void OnChartChanged(object? sender, EditorChartChangedEventArgs e)
        {
            _longest = null;
            InvalidateVisual();
        }

        private void OnRedraw(object? sender, EventArgs e) => InvalidateVisual();

        // Find first downbeat's guess is drawn over the lanes.
        private void OnAudioAssistChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AudioAssistViewModel.Downbeat))
                InvalidateVisual();
        }

        private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(PlaybackViewModel.IsPlaying):
                    // Paused, the strike line's glow goes out.
                    InvalidateVisual();
                    RequestFrame();
                    break;
                case nameof(PlaybackViewModel.Waveform):
                case nameof(PlaybackViewModel.Loop):
                case nameof(PlaybackViewModel.IsLooping):
                    InvalidateVisual();
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
            top.RequestAnimationFrame(OnFrame);
        }

        private void OnFrame(TimeSpan time)
        {
            _isFrameRequested = false;
            if (_editor is not { } editor)
                return;

            // The view moves with the cursor, which redraws it.
            editor.FollowPlayback();
            RequestFrame();
        }

        private void DrawLaneDividers(DrawingContext context, LaneProjection projection, LaneSlots slots)
        {
            double from = projection.TimeFrom, to = projection.TimeFrom + projection.TimeLength;
            for (int i = 0; i < slots.Slots.Count; i++)
            {
                double edge = projection.LaneFrom + (i * projection.LaneSize);
                if (View == LaneView.Timeline && i % 2 == 1)
                    context.FillRectangle(AlternateRowBrush, Box(from, to, edge, edge + projection.LaneSize));
                if (i > 0)
                    context.DrawLine(DividerPen, At(from, edge), At(to, edge));

                // A shared column's halves, which a click picks between: GHL's black fret, then its white.
                if (slots.SharedOf(i) is not null)
                    context.DrawLine(GridPen, At(from, edge + (projection.LaneSize / 2)), At(to, edge + (projection.LaneSize / 2)));
            }
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

        /// <returns>Whether all of it was drawn as it is now.</returns>
        private bool DrawSpectrogram(DrawingContext context, LaneAudioViewModel audio, Spectrogram spectrogram, Rect area, LaneProjection projection)
        {
            if (area.Width <= 1 || area.Height <= 1)
                return true;
            double scaling = 1 / (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
            return SpectrogramPainter.Draw(context, spectrogram, area, View == LaneView.Highway, projection.Start, projection.End, projection.AlongOf,
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
            using (context.PushClip(area))
            {
                if (audio.IsStereo && editor.Audio.Waveforms.Stereo(source) is var (left, right))
                {
                    // Left beside right across a highway; left above right along a timeline. Both at one gain, so they compare.
                    double gain = LaneWaveformPainter.FullScaleGain(left.Peak >= right.Peak ? left : right) * boost;
                    var first = vertical ? new Rect(area.X, area.Y, area.Width / 2, area.Height) : new Rect(area.X, area.Y, area.Width, area.Height / 2);
                    var second = vertical ? new Rect(area.X + (area.Width / 2), area.Y, area.Width / 2, area.Height) : new Rect(area.X, area.Y + (area.Height / 2), area.Width, area.Height / 2);
                    bool done = LaneWaveformPainter.Draw(context, left, first, vertical, projection.Start, projection.End, projection.AlongOf, peak, loud, gain, scaling, budgeted: true);
                    done &= LaneWaveformPainter.Draw(context, right, second, vertical, projection.Start, projection.End, projection.AlongOf, peak, loud, gain, scaling, budgeted: true);
                    context.DrawLine(ChannelPen, vertical ? first.TopRight : first.BottomLeft, vertical ? first.BottomRight : first.BottomRight);
                    return done;
                }

                if (editor.Audio.Waveforms.Mono(source) is not { } waveform)
                    return true;
                return LaneWaveformPainter.Draw(context, waveform, area, vertical, projection.Start, projection.End, projection.AlongOf,
                    peak, loud, LaneWaveformPainter.FullScaleGain(waveform) * boost, scaling, budgeted: true);
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
        private void DrawTempoProposal(DrawingContext context, EditorSession session, LaneProjection projection, Size size)
        {
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
                    context.FillRectangle(UnsureBrush, Box(a, b, across1, across2));
                    var text = Label(Strings.ProposalUnsureLabel, 10, UnsureLabelBrush, highway ? Math.Max(1, labels.Width - 4) : null);
                    context.DrawText(text, highway ? new Point(labels.X + 2, Math.Max(a, b) - text.Height) : new Point(Math.Min(a, b) + 3, labels.Bottom - text.Height));
                }

                int perMeasure = Math.Max(1, proposal.BeatsPerMeasure);
                int start = Math.Max(0, BeatMath.NearestIndex(beats, from, double.PositiveInfinity) - 1);
                for (int i = start; i < beats.Count && beats[i] <= to + 1; i++)
                {
                    double along = projection.AlongOf(beats[i]);
                    bool measure = ((i - proposal.Downbeat) % perMeasure + perMeasure) % perMeasure == 0;
                    context.DrawLine(measure ? ProposalMeasurePen : ProposalBeatPen, At(along, across1), At(along, across2));

                    // The handle, in the margin beside the lanes, that drags the beat.
                    var handle = highway ? new Point(labels.Right - 7, along) : new Point(along, labels.Bottom - 7);
                    using (context.PushTransform(Matrix.CreateTranslation(handle.X, handle.Y)))
                        context.DrawGeometry(proposal.Pinned[i] ? ProposalHandleBrush : null, ProposalHandlePen, Diamond);
                }
            }

            if (_editor?.AudioAssist.Downbeat is { } downbeat && downbeat >= from && downbeat <= to)
            {
                double along = projection.AlongOf(downbeat);
                context.DrawLine(DownbeatPen, At(along, highway ? labels.Left : labels.Top), At(along, across2));
                var text = Label(Strings.DownbeatGhostLabel, 10, DownbeatLabelBrush);
                context.DrawText(text, highway ? new Point(labels.X + 2, along - text.Height - 2) : new Point(along + 3, labels.Y));
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
            if (Session is not { } session || _lane?.Track is not { } key || session.TrackOf(key) is null)
                return null;
            var slots = session.SlotsOf(key.Instrument, View);
            var rows = PhraseRail.RowsOf(ChartInstruments.KindOf(key.Instrument));
            Extras = ExtrasOf(Bounds.Size, key);
            return Project(Bounds.Size, slots, session.Viewport, _lane.LookAhead, rows.Count);
        }

        // The sensitivity a proposed beat snaps to the mix's onsets at: the editor's default.
        private const double OnsetSensitivity = 0.5;

        /// <summary>Draws the lane again once this frame is out, to carry on with pictures there was no time for.</summary>
        private void PostRedraw()
        {
            if (_isRedrawPosted)
                return;
            _isRedrawPosted = true;
            Dispatcher.UIThread.Post(() =>
            {
                _isRedrawPosted = false;
                InvalidateVisual();
            }, DispatcherPriority.Background);
        }

        /// <summary>The selected span, and the loop region.</summary>
        private void DrawSpans(DrawingContext context, EditorSession session, LaneProjection projection)
        {
            double across1 = projection.LaneFrom, across2 = projection.LaneFrom + (projection.LaneSize * projection.Slots.Slots.Count);
            if (_editor?.Playback is { Loop: { } loop, IsLooping: true })
                context.FillRectangle(LoopBrush, Box(projection.AlongOf(loop.Start), projection.AlongOf(loop.End), across1, across2));
            if (session.Selection.Range is { } range && range.To > range.From)
                context.FillRectangle(SpanBrush, Box(projection.AlongOf(session.AudioAt(range.From)), projection.AlongOf(session.AudioAt(range.To)), across1, across2));
        }

        /// <summary>A faint tint across the lanes, behind the notes, over each star power phrase and solo (charter-spec C8).</summary>
        private void DrawPhraseTints(DrawingContext context, EditorSession session, LaneProjection projection)
        {
            double across1 = projection.LaneFrom, across2 = projection.LaneFrom + (projection.LaneSize * projection.Slots.Slots.Count);
            foreach (var phrase in _phrasesShown)
            {
                var tint = phrase.Kind switch
                {
                    PhraseKind.StarPower => StarPowerTintBrush,
                    PhraseKind.Solo => SoloTintBrush,
                    _ => null,
                };
                if (tint is not null)
                    context.FillRectangle(tint, BoxOf(projection, session, phrase, across1, across2));
            }
        }

        /// <summary>
        /// The phrase rail (charter-spec C8): a row for each kind the part can have, with each phrase a bar in its kind's
        /// row, so phrases that overlap don't hide each other. The selected ones are lit and outlined.
        /// </summary>
        private void DrawPhraseRail(DrawingContext context, EditorSession session, EditablePart part, IReadOnlyList<RailKind> rows, LaneProjection projection)
        {
            double along1 = projection.TimeFrom, along2 = projection.TimeFrom + projection.TimeLength;
            for (int i = 0; i < rows.Count; i++)
            {
                var (from, to) = PhraseRowOf(projection, rows.Count, i);
                context.FillRectangle(PhraseRowBrush, Box(along1, along2, from, to));
            }

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
        /// The ghosts of Generate fills' proposals (charter-spec C6 item 5) in the fills row beside a drums lane, drawn over
        /// the bars there, as a click on one keeps or drops it before it reaches a bar.
        /// </summary>
        private void DrawFillGhosts(DrawingContext context, EditorSession session, ChartTrackKey key, IReadOnlyList<RailKind> rows, LaneProjection projection, double fromTick, double toTick)
        {
            var fills = session.Fills;
            int row = IndexOf(rows, PhraseKind.DrumFill);
            if (!fills.IsActive || key.Instrument != FillProposals.Part || row < 0)
                return;
            var (across1, across2) = PhraseRowOf(projection, rows.Count, row);
            for (int i = 0; i < fills.Proposals.Count; i++)
            {
                var fill = fills.Proposals[i].Fill;
                if (fill.Tick > toTick || fill.CoverEnd < fromTick)
                    continue;
                var bar = BoxOf(projection, session, fill, across1, across2).Inflate(0.5);
                if (fills.IsKept(i))
                {
                    context.DrawRectangle(KeptGhostBrush, KeptGhostPen, bar, 2, 2);
                }
                else
                {
                    context.DrawRectangle(null, DroppedGhostPen, bar, 2, 2);
                    context.DrawLine(DroppedGhostPen, bar.TopLeft, bar.BottomRight);
                }
            }
        }

        /// <summary>A faint band across the lanes, behind the notes, over each of Generate fills' proposals on a drums lane.</summary>
        private static void DrawFillBands(DrawingContext context, EditorSession session, ChartTrackKey key, LaneProjection projection, double fromTick, double toTick)
        {
            var fills = session.Fills;
            if (!fills.IsActive || key.Instrument != FillProposals.Part)
                return;
            double across1 = projection.LaneFrom, across2 = projection.LaneFrom + (projection.LaneSize * projection.Slots.Slots.Count);
            for (int i = 0; i < fills.Proposals.Count; i++)
            {
                var fill = fills.Proposals[i].Fill;
                if (fill.Tick <= toTick && fill.CoverEnd >= fromTick)
                    context.FillRectangle(fills.IsKept(i) ? KeptBandBrush : DroppedBandBrush, BoxOf(projection, session, fill, across1, across2));
            }
        }

        /// <summary>
        /// A solo's note count on the difficulty shown, at its start (charter-spec C8 item 2): only for the solos that start
        /// on screen, as the others' counts aren't seen.
        /// </summary>
        private void DrawSoloCounts(DrawingContext context, EditorSession session, EditableTrack track, LaneProjection projection, double fromTick, double toTick)
        {
            long from = (long)Math.Floor(fromTick) - 1, to = (long)Math.Ceiling(toTick);
            foreach (var phrase in _phrasesShown)
            {
                if (phrase.Kind != PhraseKind.Solo || phrase.Tick < from || phrase.Tick > to)
                    continue;
                var text = Label(PhraseRail.NoteCount(track, phrase).ToString(CultureInfo.CurrentCulture), 9, SoloLabelBrush);
                double along = projection.AlongOf(session.AudioAt(phrase.Tick));
                var at = View == LaneView.Highway
                    ? new Point(projection.LaneFrom + 3, along - text.Height - 2)
                    : new Point(along + 3, projection.LaneFrom + 2);
                context.FillRectangle(BadgeBrush, new Rect(at, new Size(text.Width, text.Height)).Inflate(1));
                context.DrawText(text, at);
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
        private static Rect BoxOf(LaneProjection projection, EditorSession session, Phrase phrase, double across1, double across2)
        {
            double start = projection.AlongOf(session.AudioAt(phrase.Tick));
            double end = projection.AlongOf(session.AudioAt(phrase.Tick + phrase.Length));
            if (Math.Abs(end - start) < 2)
                end = start + (projection.View == LaneView.Highway ? -2 : 2);
            var a = projection.View == LaneView.Highway ? new Point(across1, Math.Min(start, end)) : new Point(Math.Min(start, end), across1);
            var b = projection.View == LaneView.Highway ? new Point(across2, Math.Max(start, end)) : new Point(Math.Max(start, end), across2);
            return new Rect(a, b);
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
        private static Color PhraseColor(PhraseKind kind) => kind switch
        {
            PhraseKind.StarPower => HighwayPalette.StarPower,
            PhraseKind.Solo => SoloColor,
            PhraseKind.DrumFill => FillColor,
            PhraseKind.Roll => Color.FromRgb(0xFF, 0xD2, 0x5E),
            PhraseKind.Trill => Color.FromRgb(0xFF, 0x9F, 0x5A),
            _ => Color.FromRgb(0xFF, 0x6E, 0x7A),
        };

        /// <summary>The grid's steps faintly, then the beat and measure lines, with the measures' numbers in the labels' margin.</summary>
        private void DrawBeats(DrawingContext context, EditorSession session, TempoMap map, LaneProjection projection, double fromTick, double toTick, Size size)
        {
            double across1 = projection.LaneFrom, across2 = projection.LaneFrom + (projection.LaneSize * projection.Slots.Slots.Count);
            long first = (long)Math.Floor(fromTick), last = (long)Math.Ceiling(toTick);

            // The snap's steps, when they're far enough apart to see.
            var snap = session.Snap;
            double step = snap.StepTicks(map.Resolution);
            if (step > 0 && Math.Abs(projection.AlongOf(session.AudioAt(first + step)) - projection.AlongOf(session.AudioAt(first))) >= 6)
            {
                long tick = snap.Step(Math.Max(0, first - 1), 1, map);
                for (int guard = 0; tick <= last && guard < 4000; guard++)
                {
                    double along = projection.AlongOf(session.AudioAt(tick));
                    context.DrawLine(GridPen, At(along, across1), At(along, across2));
                    tick = snap.Step(tick, 1, map);
                }
            }

            var labels = LabelsArea(size, projection);
            Measures(map, last);
            foreach (var line in map.BeatLines(first, last))
            {
                double along = projection.AlongOf(session.AudioAt(line.Tick));
                context.DrawLine(line.IsMeasure ? MeasurePen : BeatPen, At(along, across1), At(along, across2));
                if (!line.IsMeasure)
                    continue;

                int number = _measures.BinarySearch(line.Tick);
                if (number < 0)
                    continue;
                var text = Label((number + 1).ToString(CultureInfo.CurrentCulture), 10, LabelBrush ?? DefaultLabelBrush);
                var at = View == LaneView.Highway
                    ? new Point(labels.Right - text.Width - 4, along - text.Height)
                    : new Point(along + 3, labels.Bottom - text.Height - 1);
                context.DrawText(text, at);
            }
        }

        /// <summary>The tempo markers: a line across the lanes, the tempo and any time signature in the labels' margin, and whether each is anchored.</summary>
        private void DrawTempoMarkers(DrawingContext context, EditorSession session, LaneProjection projection, double fromTick, double toTick, Size size)
        {
            var tempoMap = session.Chart.TempoMap;
            var labels = LabelsArea(size, projection);
            double across1 = projection.LaneFrom, across2 = projection.LaneFrom + (projection.LaneSize * projection.Slots.Slots.Count);
            var accent = AccentBrush ?? DefaultAccentBrush;
            foreach (var marker in tempoMap.Tempos.Between((long)Math.Floor(fromTick), (long)Math.Ceiling(toTick)))
            {
                double along = projection.AlongOf(session.AudioAt(marker.Tick));
                bool selected = session.Selection.TempoTicks.Contains(marker.Tick);
                context.DrawLine(TempoPen, At(along, across1), At(along, across2));

                var text = Label(TempoText(tempoMap, marker), 10, selected ? accent : TempoBrush);
                var at = View == LaneView.Highway
                    ? new Point(labels.X + 2, along + 1)
                    : new Point(along + 3, labels.Y + 1);
                if (selected)
                    context.FillRectangle(BadgeBrush, new Rect(at, new Size(text.Width, text.Height)).Inflate(1));
                context.DrawText(text, at);
            }

            // Time signatures with no tempo marker at their tick.
            foreach (var signature in tempoMap.TimeSignatures.Between((long)Math.Floor(fromTick), (long)Math.Ceiling(toTick)))
            {
                if (!tempoMap.Tempos.At(signature.Tick).IsEmpty)
                    continue;
                double along = projection.AlongOf(session.AudioAt(signature.Tick));
                var text = Label(SignatureText(signature), 10, TempoBrush);
                context.DrawText(text, View == LaneView.Highway ? new Point(labels.X + 2, along + 1) : new Point(along + 3, labels.Y + 1));
            }
        }

        /// <summary>A tempo marker's label: its tempo, any time signature at its tick, and whether it's anchored.</summary>
        private static string TempoText(EditableTempoMap tempoMap, TempoChange marker)
        {
            bool anchored = !tempoMap.Anchors.At(marker.Tick).IsEmpty;
            string bpm = EditableTempoMap.BpmOf(marker.MicrosecondsPerQuarter).ToString("0.###", CultureInfo.CurrentCulture);
            var signature = tempoMap.TimeSignatures.At(marker.Tick);
            if (!signature.IsEmpty)
                bpm += " " + SignatureText(signature[^1]);
            return (anchored ? "⚓ " : "") + bpm;
        }

        private static string SignatureText(TimeSignatureMarker signature) =>
            string.Create(CultureInfo.CurrentCulture, $"{signature.Numerator}/{signature.Denominator}");

        /// <summary>
        /// The sections (charter-spec C8 item 3): a faint line across the lanes and the labels' margin at each one's start,
        /// and its name in the margin. A highway's name is above the measure number, cut short to the rail's width; a
        /// timeline's is in the ruler, after the measure number and any tempo there.
        /// </summary>
        private void DrawSections(DrawingContext context, EditorSession session, LaneProjection projection, double fromTick, double toTick, Size size)
        {
            var labels = LabelsArea(size, projection);
            bool highway = View == LaneView.Highway;
            double across1 = highway ? labels.Left : labels.Top;
            double across2 = projection.LaneFrom + (projection.LaneSize * projection.Slots.Slots.Count);
            var tempoMap = session.Chart.TempoMap;
            foreach (var e in session.Chart.Events.Between((long)Math.Floor(fromTick), (long)Math.Ceiling(toTick)))
            {
                if (SectionEvents.NameOf(e.Text) is not { } name)
                    continue;
                double along = projection.AlongOf(session.AudioAt(e.Tick));
                context.DrawLine(SectionPen, At(along, across1), At(along, across2));

                // The measure number drawn at its tick, when it starts a measure.
                int measure = _measures.BinarySearch(e.Tick);
                var number = measure >= 0 ? Label((measure + 1).ToString(CultureInfo.CurrentCulture), 10, LabelBrush ?? DefaultLabelBrush) : null;
                if (highway)
                {
                    var text = Label(name, 10, SectionBrush, Math.Max(1, labels.Width - 4));
                    context.DrawText(text, new Point(labels.X + 2, along - (number?.Height ?? 0) - text.Height));
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
                context.DrawText(label, at);
            }
        }

        private void DrawNotes(DrawingContext context, EditorSession session, ChartTrackKey key, EditableTrack track, LaneProjection projection, LaneSlots slots, double fromTick, double toTick, bool isGhost)
        {
            var kind = track.Kind;
            bool drums = kind == ChartInstrumentKind.Drums;
            int laneCount = drums ? slots.Slots.Count - (slots.Across is null ? 1 : 0) : 5;
            var game = isGhost ? null : session.GameTrack(key).Notes;
            long longest = LongestOf(key, track);
            var notes = track.Notes.Between((long)Math.Max(0, Math.Floor(fromTick) - longest), (long)Math.Ceiling(toTick) + 1);
            var accent = AccentBrush ?? DefaultAccentBrush;
            var selectedPen = new ImmutablePen((accent as ISolidColorBrush)?.Color.ToUInt32() ?? 0xFF7CB4FF, 2);
            double size = GemSize(projection);
            double across1 = projection.LaneFrom, across2 = projection.LaneFrom + (projection.LaneSize * slots.Slots.Count);

            // Tails at the back, then the bars across, then the gems; within each, the latest first.
            for (int i = notes.Length - 1; i >= 0; i--)
            {
                var note = notes[i];
                if (note.Length <= EditableNote.PlainLength || (slots.Across == note.Lane))
                    continue;
                int lane = session.ShownLane(key, note);
                var shown = Shown(game, note, drums, lane);
                int column = slots.IndexOf(lane);
                if (column < 0)
                    continue;
                double start = projection.AlongOf(session.AudioAt(note.Tick));
                double end = projection.AlongOf(session.AudioAt(note.Tick + note.Length));
                double width = Math.Max(3, projection.LaneSize * 0.2);
                double center = projection.AcrossOf(column);
                var tail = Box(start, end, center - (width / 2), center + (width / 2));
                var color = HighwayPalette.NoteColor(kind, laneCount, shown);
                if (!isGhost && shown.Length == 0)
                {
                    // Shorter than the sustain cutoff: the games read a plain note, so the tail is only dashed (C5 item 3).
                    context.DrawRectangle(null, new ImmutablePen(HighwayPalette.WithAlpha(color, 0xC0).ToUInt32(), 1, new ImmutableDashStyle([2, 2], 0)), tail.Deflate(0.5));
                }
                else
                {
                    _gems.DrawTail(context, kind, shown, color, tail, outlined: false);
                }
            }

            var flagged = new HashSet<long>();
            foreach (bool bars in (ReadOnlySpan<bool>)[true, false])
            {
                for (int i = notes.Length - 1; i >= 0; i--)
                {
                    var note = notes[i];
                    if (note.Tick > toTick + 1 || note.Tick < fromTick - 1)
                        continue;
                    bool across = slots.Across == note.Lane;
                    if (across != bars)
                        continue;

                    // A GHL barre: the black fret covers the top half of its column's white one, as in the game, so it's
                    // drawn just after it.
                    if (slots.IsShared(note.Lane) && slots.PartnerOf(note.Lane) is { } white && track.TryGetNote(note.Tick, white, false, out _))
                        continue;
                    DrawNote(note, across, barre: false);
                    if (!across && slots.SharedOf(slots.IndexOf(note.Lane)) is { } black && black != note.Lane && track.TryGetNote(note.Tick, black, false, out var shared))
                        DrawNote(shared, across: false, barre: true);
                }
            }

            void DrawNote(EditableNote note, bool across, bool barre)
            {
                // Inside a disco flip section, on the pad the player hits (charter-spec C6 item 7).
                int lane = session.ShownLane(key, note);
                var shown = Shown(game, note, drums, lane);
                var color = HighwayPalette.NoteColor(kind, laneCount, shown);
                double along = projection.AlongOf(session.AudioAt(note.Tick));
                Rect rect;
                if (across)
                {
                    double thickness = Math.Max(4, size * 0.45);
                    rect = Box(along - (thickness / 2), along + (thickness / 2), across1 + 4, across2 - 4);
                    _gems.DrawBar(context, shown, color, rect, outlined: isGhost);
                }
                else
                {
                    int column = slots.IndexOf(lane);
                    if (column < 0)
                        return;
                    rect = GemRect(projection, column, along, size);
                    if (column == slots.Slots.Count - 1 && slots.Across is null && note.Lane == EditorTools.ApartLane(slots))
                    {
                        _gems.DrawBar(context, shown, color, rect, outlined: isGhost);
                    }
                    else
                    {
                        if (barre)
                            rect = rect.WithHeight(rect.Height / 2);
                        _gems.DrawGem(context, kind, shown, color, rect, outlined: isGhost);
                    }
                }

                if (isGhost)
                    return;
                if (session.Selection.Contains(key, note))
                    context.DrawRectangle(null, selectedPen, rect.Inflate(2), 3, 3);
                if (!across && kind != ChartInstrumentKind.Drums && flagged.Add(note.Tick) && track.FlagsAt(note.Tick) is var flags and not ChordFlags.None)
                    DrawForcing(context, projection, slots, along, flags);
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
            context.DrawText(text, at);
        }

        /// <summary>The note the place tool would put down where the pointer is, as it would be drawn.</summary>
        private void DrawHover(DrawingContext context, EditorSession session, ChartTrackKey key, EditableTrack track, LaneProjection projection, LaneSlots slots)
        {
            if (_hover is not { } hover || _isPressed || session.Tool != EditorTool.Place || PointAt(hover) is not { } point || point.Lane is not { } lane)
                return;
            long tick = session.Snap.Nearest(point.Tick, session.TempoMap);
            int column = slots.IndexOf(lane);
            int stored = session.StoredLane(key, tick, lane);
            if (column < 0 || track.TryGetNote(tick, stored, false, out _))
                return;
            double along = projection.AlongOf(session.AudioAt(tick));

            // As it would be placed: numbered and coloured as Shown has it, and on Pro drums a cymbal unless a tom marker
            // covers it. Inside a disco flip section, the red placed in yellow's column is a yellow cymbal, and the yellow
            // placed in red's a red pad (C6 item 7).
            bool drums = track.Kind == ChartInstrumentKind.Drums;
            int laneCount = drums ? slots.Slots.Count - (slots.Across is null ? 1 : 0) : 5;
            bool cymbal = stored != lane
                ? lane == 2
                : drums && session.Chart.DrumsVariant == DrumsVariant.Pro && lane is >= TomMarker.FirstPad and <= TomMarker.LastPad
                    && !DrumCymbals.IsTom(session.Chart.PartOf(key.Instrument)!, lane, tick);
            var placed = new ChartNote(tick, 0, drums ? lane - 1 : lane, cymbal ? NoteFlags.Cymbal : NoteFlags.None);
            var color = HighwayPalette.NoteColor(track.Kind, laneCount, placed);
            var rect = GemRect(projection, column, along, GemSize(projection));

            // A GHL black fret on a white one makes a barre, drawn over the white's top half.
            if (slots.IsShared(lane) && slots.PartnerOf(lane) is { } white && track.TryGetNote(tick, white, false, out _))
                rect = rect.WithHeight(rect.Height / 2);
            using (context.PushOpacity(0.45))
                _gems.DrawGem(context, track.Kind, placed, color, rect, outlined: true);
        }

        /// <summary>The strike line's band, on the cursor, behind the notes: dark across the lanes, as in the games.</summary>
        private void DrawStrikeBand(DrawingContext context, EditorSession session, LaneProjection projection)
        {
            double along = projection.AlongOf(session.Viewport.Cursor);
            double across1 = projection.LaneFrom, across2 = projection.LaneFrom + (projection.LaneSize * projection.Slots.Slots.Count);
            double half = Math.Max(3, GemSize(projection) * 0.6);
            context.FillRectangle(StrikeBandBrush, Box(along - half, along + half, across1, across2));
            context.DrawLine(StrikeEdgePen, At(along - half, across1), At(along - half, across2));
            context.DrawLine(StrikeEdgePen, At(along + half, across1), At(along + half, across2));
        }

        /// <summary>
        /// The columns the song is playing through, lit as the inspector's highway lights them: a beam rising from the
        /// strike line and the column's target filled in, as each note reaches it, fading, and held while it's sustained.
        /// </summary>
        private void DrawGlow(DrawingContext context, EditorSession session, ChartTrackKey key, EditableTrack track, LaneProjection projection, LaneSlots slots)
        {
            var kind = track.Kind;
            bool drums = kind == ChartInstrumentKind.Drums;
            int laneCount = drums ? slots.Slots.Count - (slots.Across is null ? 1 : 0) : 5;
            double now = session.Viewport.Cursor;
            var hits = HitsAt(session, key, track, slots, now);
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
                using (context.PushOpacity(hit * StrikeGlow.BeamOpacity))
                    context.FillRectangle(StrikeGlow.Beam(color, View == LaneView.Highway), Box(strike, strike + reach, edge, edge + projection.LaneSize));

                var rect = GemRect(projection, column, strike, size);
                double radius = GemPainter.CornerOf(rect);
                context.DrawRectangle(_gems.Brush(StrikeGlow.TargetFill(color, hit)), _gems.Pen(StrikeGlow.TargetEdge(color), 2), rect.Deflate(1), radius, radius);
            }
        }

        /// <summary>How brightly each column is lit at <paramref name="now"/>, from 0 to 1: just struck, or held.</summary>
        private double[] HitsAt(EditorSession session, ChartTrackKey key, EditableTrack track, LaneSlots slots, double now)
        {
            int count = slots.Slots.Count;
            if (_hits.Length < count)
                _hits = new double[count];
            var hits = _hits;
            Array.Clear(hits, 0, count);

            bool drums = track.Kind == ChartInstrumentKind.Drums;
            var game = session.GameTrack(key).Notes;
            double from = Math.Max(0, session.TickAt(now - StrikeGlow.Seconds));
            var notes = track.Notes.Between((long)Math.Max(0, Math.Floor(from) - LongestOf(key, track)), (long)Math.Ceiling(Math.Max(0, session.TickAt(now))) + 1);
            foreach (var note in notes)
            {
                double start = session.AudioAt(note.Tick);
                if (start > now)
                    continue;

                // Held while the games read it as a sustain and its tail hasn't passed.
                int lane = session.ShownLane(key, note);
                var shown = Shown(game, note, drums, lane);
                double glow = StrikeGlow.Brightness(now - start, shown.Length > 0 && session.AudioAt(note.Tick + shown.Length) > now);
                if (glow <= 0)
                    continue;

                // An open note or kick across the highway lights every column; a pad, the column it's drawn in.
                if (slots.Across == note.Lane)
                {
                    for (int c = 0; c < count; c++)
                        hits[c] = Math.Max(hits[c], glow * StrikeGlow.SpreadLevel);
                }
                else if (slots.IndexOf(lane) is var column and >= 0)
                {
                    hits[column] = Math.Max(hits[column], glow);
                }
            }

            return hits;
        }

        /// <summary>The cursor's line through the strike band, over the notes and into the labels' margin.</summary>
        private void DrawCursor(DrawingContext context, EditorSession session, LaneProjection projection, Rect lanes)
        {
            double along = projection.AlongOf(session.Viewport.Cursor);
            var pen = new ImmutablePen(((AccentBrush ?? DefaultAccentBrush) as ISolidColorBrush)?.Color.ToUInt32() ?? 0xFF7CB4FF, 1.5);
            var labels = LabelsArea(Bounds.Size, projection);
            context.DrawLine(pen, At(along, View == LaneView.Highway ? labels.Left : labels.Top), At(along, View == LaneView.Highway ? lanes.Right : lanes.Bottom));
        }

        /// <summary>A gem in <paramref name="column"/> at <paramref name="along"/>.</summary>
        private Rect GemRect(LaneProjection projection, int column, double along, double size)
        {
            double center = projection.AcrossOf(column);
            return View == LaneView.Highway
                ? Box(along - (size / 2), along + (size / 2), center - (projection.LaneSize * 0.43), center + (projection.LaneSize * 0.43))
                : Box(along - (size / 2), along + (size / 2), center - (projection.LaneSize * 0.35), center + (projection.LaneSize * 0.35));
        }

        private double GemSize(LaneProjection projection) => View == LaneView.Highway
            ? Math.Clamp(projection.LaneSize * 0.32, 8, 18)
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
        private long LongestOf(ChartTrackKey key, EditableTrack track)
        {
            if (_longest is { } known && known.Track == key)
                return known.Length;
            long longest = 0;
            foreach (var note in track.Notes)
                longest = Math.Max(longest, note.Length);
            _longest = (key, longest);
            return longest;
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
        private FormattedText Label(string text, double size, IBrush brush, double? maxWidth = null)
        {
            string key = string.Create(CultureInfo.InvariantCulture, $"{text}|{size}|{brush.GetHashCode()}|{maxWidth}");
            if (!_labels.TryGetValue(key, out var label))
            {
                if (_labels.Count > 512)
                    _labels.Clear();
                label = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, size, brush);
                if (maxWidth is { } width)
                {
                    label.MaxTextWidth = width;
                    label.MaxLineCount = 1;
                    label.Trimming = TextTrimming.CharacterEllipsis;
                }

                _labels[key] = label;
            }

            return label;
        }
    }
}
