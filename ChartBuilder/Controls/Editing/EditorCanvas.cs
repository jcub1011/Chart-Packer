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
using ChartBuilder.ViewModels;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Editing;
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

        // How near a note's start the pointer hits it, in pixels.
        private const double HitPixels = 7;

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
        private static readonly IBrush BadgeBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xE0, 0x20, 0x22, 0x2E));
        private static readonly IBrush DefaultLabelBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF));
        private static readonly IBrush DefaultAccentBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x7C, 0xB4, 0xFF));
        private static readonly IBrush StrikeBandBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x90, 0x00, 0x00, 0x00));
        private static readonly IPen StrikeEdgePen = new ImmutablePen(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF).ToUInt32(), 1);
        private static readonly Typeface LabelFace = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

        private readonly GemPainter _gems = new();
        private readonly Dictionary<string, FormattedText> _labels = [];

        // How brightly each column is lit as the song plays through the strike line.
        private double[] _hits = [];
        private EditorLaneViewModel? _lane;
        private ChartEditorViewModel? _editor;
        private bool _isFrameRequested;
        private bool _isPressed;
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
        /// seconds beyond the strike line.
        /// </summary>
        protected abstract LaneProjection Project(Size size, LaneSlots slots, EditorViewport viewport, double lookAhead);

        /// <summary>Where the margin is that measure numbers and tempo markers are drawn in: the left rail of a highway, the ruler along a timeline's top.</summary>
        protected abstract Rect LabelsArea(Size size, LaneProjection projection);

        /// <summary>Where the waveform is drawn: behind a highway's lanes, or in a strip over a timeline's.</summary>
        protected abstract Rect WaveformArea(Size size, LaneProjection projection);

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
            var projection = Project(bounds.Size, slots, session.Viewport, _lane.LookAhead);
            var lanes = Box(projection.TimeFrom, projection.TimeFrom + projection.TimeLength, projection.LaneFrom, projection.LaneFrom + (projection.LaneSize * slots.Slots.Count));
            var map = session.TempoMap;
            double fromTick = Math.Max(0, session.TickAt(projection.Start));
            double toTick = Math.Max(0, session.TickAt(projection.End));

            context.FillRectangle(LanesBrush, lanes);
            DrawLaneDividers(context, projection, slots);
            DrawWaveform(context, bounds.Size, projection);
            DrawSpans(context, session, projection);
            DrawBeats(context, session, map, projection, fromTick, toTick, bounds.Size);
            DrawTempoMarkers(context, session, projection, fromTick, toTick, bounds.Size);
            DrawStrikeBand(context, session, projection);

            using (context.PushClip(lanes))
            {
                if (_editor?.Playback.IsPlaying == true)
                    DrawGlow(context, session, key, track, projection, slots);

                if (_lane.Ghost is { } ghost && session.Chart.PartOf(key.Instrument) is { } part)
                {
                    using (context.PushOpacity(0.28))
                        DrawNotes(context, session, new ChartTrackKey(key.Instrument, ghost), part[ghost], projection, slots, fromTick, toTick, isGhost: true);
                }

                DrawNotes(context, session, key, track, projection, slots, fromTick, toTick, isGhost: false);
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
            _isPressed = true;
            _lastPoint = point;

            // A box is drawn from a press that's on no note, with the select tool.
            _boxFrom = session.Tool == EditorTool.Select && session.TrackOf(point.Track) is { } track && EditorTools.HitTest(track, point, point.Slots.Across) is null
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
            if (_isPressed && Session is { } session && PointAt(position) is { } point)
            {
                _lastPoint = point;
                _boxTo = position;
                session.Tools.Drag(point);
            }

            InvalidateVisual();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
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
            if (e.Handled || _editor is not { } editor || _lane is not { } lane || Session is not { } session)
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

        /// <summary>The chart point under <paramref name="position"/>, or null when there's no part to edit.</summary>
        private ChartPoint? PointAt(Point position)
        {
            if (Session is not { } session || _lane?.Track is not { } key || session.TrackOf(key) is null)
                return null;
            var slots = session.SlotsOf(key.Instrument, View);
            var projection = Project(Bounds.Size, slots, session.Viewport, _lane.LookAhead);
            double seconds = projection.SecondsAt(AlongOf(position));
            double tick = session.TickAt(seconds);
            double tolerance = Math.Max(1, projection.TicksOf(HitPixels, Math.Max(0, tick), session.TempoMap));
            return new ChartPoint(key, tick, projection.LaneAt(AcrossOf(position)), tolerance, slots);
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
            }
        }

        private void DrawWaveform(DrawingContext context, Size size, LaneProjection projection)
        {
            if (_editor?.Playback.Waveform is not { } waveform)
                return;
            var area = WaveformArea(size, projection);
            if (area.Width <= 0 || area.Height <= 0)
                return;

            // The painter's position is evaluated at two times and must be linear, as the projection is.
            double scaling = 1 / (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
            using (context.PushClip(area))
            {
                LaneWaveformPainter.Draw(context, waveform, area, isVertical: View == LaneView.Highway, projection.Start, projection.End, projection.AlongOf,
                    WaveformPeakBrush, WaveformLoudBrush, LaneWaveformPainter.FullScaleGain(waveform), scaling);
            }
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
                bool anchored = !tempoMap.Anchors.At(marker.Tick).IsEmpty;
                context.DrawLine(TempoPen, At(along, across1), At(along, across2));

                string bpm = EditableTempoMap.BpmOf(marker.MicrosecondsPerQuarter).ToString("0.###", CultureInfo.CurrentCulture);
                var signature = tempoMap.TimeSignatures.At(marker.Tick);
                if (!signature.IsEmpty)
                    bpm += string.Create(CultureInfo.CurrentCulture, $" {signature[^1].Numerator}/{signature[^1].Denominator}");
                var text = Label((anchored ? "⚓ " : "") + bpm, 10, selected ? accent : TempoBrush);
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
                var text = Label(string.Create(CultureInfo.CurrentCulture, $"{signature.Numerator}/{signature.Denominator}"), 10, TempoBrush);
                context.DrawText(text, View == LaneView.Highway ? new Point(labels.X + 2, along + 1) : new Point(along + 3, labels.Y + 1));
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
                var shown = Shown(game, note, drums);
                int column = slots.IndexOf(note.Lane);
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

                    var shown = Shown(game, note, drums);
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
                        int column = slots.IndexOf(note.Lane);
                        if (column < 0)
                            continue;
                        rect = GemRect(projection, column, along, size);
                        if (column == slots.Slots.Count - 1 && slots.Across is null && note.Lane == EditorTools.ApartLane(slots))
                            _gems.DrawBar(context, shown, color, rect, outlined: isGhost);
                        else
                            _gems.DrawGem(context, kind, shown, color, rect, outlined: isGhost);
                    }

                    if (isGhost)
                        continue;
                    if (session.Selection.Contains(key, note))
                        context.DrawRectangle(null, selectedPen, rect.Inflate(2), 3, 3);
                    if (!bars && kind != ChartInstrumentKind.Drums && flagged.Add(note.Tick) && track.FlagsAt(note.Tick) is var flags and not ChordFlags.None)
                        DrawForcing(context, projection, slots, along, flags);
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
            context.DrawText(text, at);
        }

        /// <summary>The note the place tool would put down where the pointer is.</summary>
        private void DrawHover(DrawingContext context, EditorSession session, ChartTrackKey key, EditableTrack track, LaneProjection projection, LaneSlots slots)
        {
            if (_hover is not { } hover || _isPressed || session.Tool != EditorTool.Place || PointAt(hover) is not { } point || point.Lane is not { } lane)
                return;
            long tick = session.Snap.Nearest(point.Tick, session.TempoMap);
            int column = slots.IndexOf(lane);
            if (column < 0 || track.TryGetNote(tick, lane, false, out _))
                return;
            double along = projection.AlongOf(session.AudioAt(tick));
            var color = HighwayPalette.NoteColor(track.Kind, 5, new ChartNote(tick, 0, track.Kind == ChartInstrumentKind.Drums ? lane - 1 : lane, NoteFlags.None));
            using (context.PushOpacity(0.45))
                _gems.DrawGem(context, track.Kind, new ChartNote(tick, 0, lane, NoteFlags.None), color, GemRect(projection, column, along, GemSize(projection)), outlined: true);
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
                var shown = Shown(game, note, drums);
                double glow = StrikeGlow.Brightness(now - start, shown.Length > 0 && session.AudioAt(note.Tick + shown.Length) > now);
                if (glow <= 0)
                    continue;

                // An open note or kick across the highway lights every column.
                if (slots.Across == note.Lane)
                {
                    for (int c = 0; c < count; c++)
                        hits[c] = Math.Max(hits[c], glow * StrikeGlow.SpreadLevel);
                }
                else if (slots.IndexOf(note.Lane) is var column and >= 0)
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
        /// How the games read <paramref name="note"/>: its game-view note, with its natural HOPO, star power and sustain
        /// cutoff; or, when they don't read it as written, the note as it is. Its lane is numbered as the game view's are,
        /// which the palette colours by: a drum pad's from red, and the kick as an open note.
        /// </summary>
        private static ChartNote Shown(IReadOnlyList<ChartNote>? game, EditableNote note, bool drums)
        {
            int lane = drums ? (note.Lane == 0 ? ChartNote.OpenLane : note.Lane - 1) : note.Lane;
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

            return new ChartNote(note.Tick, note.Length, lane, note.DoubleKick ? NoteFlags.DoubleKick : NoteFlags.None);
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

        private FormattedText Label(string text, double size, IBrush brush)
        {
            string key = string.Create(CultureInfo.InvariantCulture, $"{text}|{size}|{brush.GetHashCode()}");
            if (!_labels.TryGetValue(key, out var label))
            {
                if (_labels.Count > 512)
                    _labels.Clear();
                label = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, size, brush);
                _labels[key] = label;
            }

            return label;
        }
    }
}
