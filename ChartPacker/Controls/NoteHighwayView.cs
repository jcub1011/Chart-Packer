using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ChartPacker.Audio.Waveforms;
using ChartPacker.Core.Charts;

namespace ChartPacker.Controls
{
    /// <summary>One highway: an instrument difficulty's notes with their song times worked out.</summary>
    public sealed class HighwayLane
    {
        /// <param name="delay">How much later than its ticks say the chart plays against the audio.</param>
        public HighwayLane(ChartInstrumentKind kind, int laneCount, string label, IReadOnlyList<ChartNote> notes, TempoMap tempo, TimeSpan delay)
        {
            Kind = kind;
            LaneCount = laneCount;
            Label = label;
            Notes = [.. notes];
            Starts = new double[Notes.Length];
            Ends = new double[Notes.Length];
            for (int i = 0; i < Notes.Length; i++)
            {
                Starts[i] = tempo.SecondsAt(Notes[i].Tick) + delay.TotalSeconds;
                Ends[i] = Notes[i].Length > 0 ? tempo.SecondsAt(Notes[i].Tick + Notes[i].Length) + delay.TotalSeconds : Starts[i];
                MaxLength = Math.Max(MaxLength, Ends[i] - Starts[i]);
            }

            if (kind == ChartInstrumentKind.SixFret)
            {
                var whites = Notes.Where(n => n.Lane is >= 0 and < 3).Select(n => (n.Tick, n.Lane)).ToHashSet();
                SharesColumn = Notes.Where(n => n.Lane >= 3 && whites.Contains((n.Tick, n.Lane - 3))).Select(n => (n.Tick, n.Lane - 3)).ToHashSet();
            }
        }

        public ChartInstrumentKind Kind { get; }

        /// <summary>How many lanes the highway has: five frets, four or five pads, or three six-fret columns.</summary>
        public int LaneCount { get; }

        /// <summary>The instrument and difficulty, shown at the top of the highway.</summary>
        public string Label { get; }

        public ChartNote[] Notes { get; }

        /// <summary>Each note's song time, in seconds.</summary>
        public double[] Starts { get; }

        /// <summary>When each note's sustain ends, in seconds; its start when it has none.</summary>
        public double[] Ends { get; }

        /// <summary>The longest sustain, in seconds, so notes whose tails still show can be found.</summary>
        public double MaxLength { get; }

        /// <summary>For six-fret, the ticks and columns where a black and a white fret are played together.</summary>
        public IReadOnlySet<(long Tick, int Column)> SharesColumn { get; } = new HashSet<(long, int)>();
    }

    /// <summary>What <see cref="NoteHighwayView"/> draws: its highways, left to right, and the beat lines they share.</summary>
    public sealed class HighwayScene
    {
        public HighwayScene(IReadOnlyList<HighwayLane> lanes, TempoMap tempo, TimeSpan delay)
        {
            Lanes = lanes;
            long last = lanes.SelectMany(l => l.Notes).Select(n => n.Tick + n.Length).DefaultIfEmpty().Max() + (tempo.Resolution * 8L);
            var beats = tempo.BeatLines(0, last).ToList();
            BeatTimes = [.. beats.Select(b => tempo.SecondsAt(b.Tick) + delay.TotalSeconds)];
            IsMeasure = [.. beats.Select(b => b.IsMeasure)];
            MaxLaneCount = lanes.Select(l => l.LaneCount).DefaultIfEmpty(1).Max();
            EndSeconds = Math.Max(0, lanes.SelectMany(l => l.Ends).DefaultIfEmpty().Max());
        }

        /// <summary>When the last note of any highway ends, sustain included, in seconds.</summary>
        public double EndSeconds { get; }

        public IReadOnlyList<HighwayLane> Lanes { get; }

        /// <summary>The most lanes any of the highways has.</summary>
        public int MaxLaneCount { get; }

        public double[] BeatTimes { get; }

        public bool[] IsMeasure { get; }
    }

    /// <summary>
    /// Draws up to four flat note highways side by side, notes falling toward a strike line just
    /// above the bottom. Each frame the screen is drawn it reads <see cref="Clock"/>, so the notes
    /// follow the audio whether it's playing, paused or being dragged. Behind each highway's notes it
    /// can draw a track's <see cref="Waveforms"/>, falling with them.
    /// <para>While the pointer is over a highway, that one shows its whole chart instead, squeezed into its height from the
    /// start at the bottom to <see cref="LengthSeconds"/> at the top. Pressing on it and dragging scrubs: from
    /// <see cref="ScrubStarted"/> to <see cref="ScrubEnded"/>, <see cref="Scrubbed"/> says where to, and the owner seeks
    /// when it ends, as it does for <see cref="WaveformView"/>.</para>
    /// <para>While Ctrl is held the highway goes on showing the notes falling, and a drag is fine: down moves the audio
    /// later, as if pulling the notes down, by <see cref="FineSecondsPerPixel"/> for each pixel, rather than following the
    /// pointer up the whole chart, with the pointer held still. A press with Ctrl held leaves the audio where it is.</para>
    /// </summary>
    /// <example><c>&lt;ctl:NoteHighwayView Scene="{Binding Highway.Scene}" Clock="{Binding Highway.Clock}" LookAhead="{Binding Highway.LookAhead}" Waveforms="{Binding Highway.LaneWaveforms}" /&gt;</c></example>
    public sealed class NoteHighwayView : Control
    {
        public static readonly StyledProperty<HighwayScene?> SceneProperty =
            AvaloniaProperty.Register<NoteHighwayView, HighwayScene?>(nameof(Scene));

        public static readonly StyledProperty<Func<TimeSpan>?> ClockProperty =
            AvaloniaProperty.Register<NoteHighwayView, Func<TimeSpan>?>(nameof(Clock));

        public static readonly StyledProperty<double> LookAheadProperty =
            AvaloniaProperty.Register<NoteHighwayView, double>(nameof(LookAhead), 1.4);

        public static readonly StyledProperty<IReadOnlyList<Waveform?>?> WaveformsProperty =
            AvaloniaProperty.Register<NoteHighwayView, IReadOnlyList<Waveform?>?>(nameof(Waveforms));

        public static readonly StyledProperty<bool> AreNotesOutlinedProperty =
            AvaloniaProperty.Register<NoteHighwayView, bool>(nameof(AreNotesOutlined));

        public static readonly StyledProperty<double> LengthSecondsProperty =
            AvaloniaProperty.Register<NoteHighwayView, double>(nameof(LengthSeconds));

        public static readonly StyledProperty<double> FineSecondsPerPixelProperty =
            AvaloniaProperty.Register<NoteHighwayView, double>(nameof(FineSecondsPerPixel), LaneScrub.DefaultFineSecondsPerPixel);

        // Where the strike line sits, as a fraction of the height from the top; the instrument's name sits below it.
        private const double StrikeFraction = 0.87;

        // The top of the highway fades out over this fraction of its height, so notes come in rather than pop in.
        private const double FadeFraction = 0.2;

        // How long a lane lights up after a note reaches it, in seconds.
        private const double HitGlow = 0.16;

        private static readonly IBrush HighwayBrush = VerticalGradient(
            (0, Color.FromArgb(0x80, 0x06, 0x06, 0x0C)),
            (0.5, Color.FromArgb(0xCC, 0x08, 0x08, 0x10)),
            (1, Color.FromArgb(0xE6, 0x05, 0x05, 0x0A)));

        // Opaque below the fade, so everything drawn on the highway fades in at the top.
        private static readonly IBrush FadeMask = VerticalGradient((0, Colors.Transparent), (FadeFraction, Colors.White), (1, Colors.White));

        private static readonly IBrush RailBrush = VerticalGradient(
            (0, Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF)),
            (0.7, Color.FromArgb(0x70, 0xC8, 0xD2, 0xFF)),
            (1, Color.FromArgb(0xA0, 0xE0, 0xE6, 0xFF)));

        // A highway shown as its whole chart: evenly dark, with no fade, so the marks read the same all the way up.
        private static readonly IBrush OverviewBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xE6, 0x06, 0x06, 0x0C));
        private static readonly IPen OverviewEdgePen = new ImmutablePen(Color.FromArgb(0x50, 0xC8, 0xD2, 0xFF).ToUInt32(), 1);

        private static readonly IPen LaneDividerPen =new ImmutablePen(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF).ToUInt32(), 1);
        private static readonly IPen BeatPen = new ImmutablePen(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF).ToUInt32(), 1);
        private static readonly IPen MeasurePen = new ImmutablePen(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF).ToUInt32(), 2);
        private static readonly IBrush StrikeBandBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xC0, 0x00, 0x00, 0x00));
        private static readonly IPen StrikeEdgePen = new ImmutablePen(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF).ToUInt32(), 1);
        private static readonly IPen OutlinePen = new ImmutablePen(Color.FromArgb(0xA0, 0x00, 0x00, 0x00).ToUInt32(), 1);
        private static readonly IBrush HopoMarkBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
        private static readonly IBrush TapBodyBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x16, 0x16, 0x1B));
        private static readonly IBrush LabelBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF));

        // The track's waveform behind the notes: faint, so the gems stand out in front of it.
        private static readonly IBrush WaveformPeakBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x24, 0xC8, 0xD2, 0xFF));
        private static readonly IBrush WaveformLoudBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x48, 0xC8, 0xD2, 0xFF));

        private static readonly Color StarPowerColor = Color.FromRgb(0x7F, 0xE9, 0xFF);
        private static readonly Color OpenColor = Color.FromRgb(0xB0, 0x4C, 0xE0);
        private static readonly Color KickColor = Color.FromRgb(0xFB, 0x8C, 0x00);
        private static readonly Color WhiteFretColor = Color.FromRgb(0xF2, 0xF2, 0xF4);
        private static readonly Color BlackFretColor = Color.FromRgb(0x2A, 0x2A, 0x32);

        // A black fret's tail, and its edge when outlined, which its own colour would leave unseen on the dark highway.
        private static readonly Color BlackFretEdgeColor = Color.FromRgb(0x8A, 0x8A, 0x96);

        private static readonly Color Green = Color.FromRgb(0x2E, 0xC4, 0x4E);
        private static readonly Color Red = Color.FromRgb(0xEE, 0x3B, 0x3B);
        private static readonly Color Yellow = Color.FromRgb(0xFF, 0xD8, 0x2E);
        private static readonly Color Blue = Color.FromRgb(0x25, 0x8C, 0xF0);
        private static readonly Color Orange = Color.FromRgb(0xFF, 0x8A, 0x10);

        private static readonly Color[] FretColors = [Green, Red, Yellow, Blue, Orange];
        private static readonly Color[] FourPadColors = [Red, Yellow, Blue, Green];
        private static readonly Color[] FivePadColors = [Red, Yellow, Blue, Orange, Green];

        private readonly Dictionary<Color, IBrush> _brushes = [];
        private readonly Dictionary<Color, IBrush> _beams = [];
        private readonly Dictionary<(Color, double), IPen> _pens = [];
        private readonly Dictionary<string, FormattedText> _labels = [];
        private bool _isFrameRequested;
        private Rect? _viewport;
        private TimeSpan _drawnAt = TimeSpan.MinValue;

        // How brightly each column of the highway being drawn is lit; one for them all, as they're drawn one at a time.
        private double[] _hits = [];

        // A cymbal's shape for each size of gem, at the origin, moved to each gem drawn.
        private readonly Dictionary<Size, StreamGeometry> _cymbals = [];

        // The highway the pointer is over, drawn as its whole chart, and how far down it the pointer is; -1 and NaN for
        // none. While scrubbing it stays the highway the press was on.
        private int _hoveredLane = -1;
        private double _hoverY = double.NaN;
        private bool _isScrubbing;

        // Ctrl is held over a highway, or while scrubbing: it shows the notes falling, and a drag is fine, moving the audio by
        // how far the pointer goes, holding it still.
        private bool _isFine;
        private readonly FineDrag _fine;

        // The window whose keys say when Ctrl is pressed or let go with the pointer still.
        private TopLevel? _keysFrom;

        // The whole-chart marks of the highway last drawn that way, and what they were laid out for.
        private OverviewMarks? _overview;

        static NoteHighwayView()
        {
            AffectsRender<NoteHighwayView>(SceneProperty, LookAheadProperty, WaveformsProperty, AreNotesOutlinedProperty, LengthSecondsProperty);
        }

        /// <summary>Raised when a highway is pressed, before the first <see cref="Scrubbed"/>.</summary>
        public event EventHandler? ScrubStarted;

        /// <summary>Raised as the pointer moves while a highway is pressed, with the time it's at.</summary>
        public event EventHandler<ScrubEventArgs>? Scrubbed;

        /// <summary>Raised when the highway is let go, or the press is lost.</summary>
        public event EventHandler? ScrubEnded;

        /// <summary>The highways to draw, or <see langword="null"/> for none.</summary>
        public HighwayScene? Scene
        {
            get => GetValue(SceneProperty);
            set => SetValue(SceneProperty, value);
        }

        /// <summary>Where the audio is; zero when not set.</summary>
        public Func<TimeSpan>? Clock
        {
            get => GetValue(ClockProperty);
            set => SetValue(ClockProperty, value);
        }

        /// <summary>How far ahead of the strike line the highway shows, in seconds: less is faster.</summary>
        public double LookAhead
        {
            get => GetValue(LookAheadProperty);
            set => SetValue(LookAheadProperty, value);
        }

        /// <summary>
        /// The audio drawn behind each of the <see cref="Scene"/>'s highways, in order, at the time it's heard; a
        /// <see langword="null"/> one, or the whole list, draws none.
        /// </summary>
        public IReadOnlyList<Waveform?>? Waveforms
        {
            get => GetValue(WaveformsProperty);
            set => SetValue(WaveformsProperty, value);
        }

        /// <summary>
        /// Whether the notes are drawn as outlines in their colours, with nothing filled in, so the waveform behind
        /// them shows through and a note can be lined up with the sound it's played to.
        /// </summary>
        public bool AreNotesOutlined
        {
            get => GetValue(AreNotesOutlinedProperty);
            set => SetValue(AreNotesOutlinedProperty, value);
        }

        /// <summary>
        /// How long the song is, in seconds: what a highway shows of it while the pointer is over it, and scrubs through. At
        /// zero, the chart's own end is taken instead.
        /// </summary>
        public double LengthSeconds
        {
            get => GetValue(LengthSecondsProperty);
            set => SetValue(LengthSecondsProperty, value);
        }

        /// <summary>How far a pixel dragged with Ctrl held moves the audio, in seconds, whatever the song's length.</summary>
        public double FineSecondsPerPixel
        {
            get => GetValue(FineSecondsPerPixelProperty);
            set => SetValue(FineSecondsPerPixelProperty, value);
        }

        public NoteHighwayView()
        {
            _fine = new FineDrag(this);
            EffectiveViewportChanged += OnEffectiveViewportChanged;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == SceneProperty)
                _overview = null;
            if (change.Property == SceneProperty || change.Property == IsVisibleProperty)
                RequestFrame();

            // Shown again, it shows the notes falling until the pointer is over a highway again.
            if (change.Property == IsVisibleProperty && !IsVisible && !_isScrubbing)
                SetHover(-1, double.NaN);
        }

        // Whether any of the control is in view: scrolled out of sight, it isn't drawn as the clock moves. Not yet laid
        // out (hidden, say), it has no size to check, so it's taken to be, lest it's left undrawn once shown in place.
        private bool IsInViewport =>
            _viewport is not { } viewport || Bounds.Width <= 0 || Bounds.Height <= 0 || viewport.Intersects(new Rect(Bounds.Size));

        private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
        {
            bool wasInViewport = IsInViewport;
            _viewport = e.EffectiveViewport;
            if (IsInViewport && !wasInViewport)
                InvalidateVisual();
        }


        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            RequestFrame();

            // Tunnelled, and handled ones too, so Ctrl is seen whatever has focus.
            _keysFrom = TopLevel.GetTopLevel(this);
            _keysFrom?.AddHandler(KeyDownEvent, OnModifierKey, RoutingStrategies.Tunnel, handledEventsToo: true);
            _keysFrom?.AddHandler(KeyUpEvent, OnModifierKey, RoutingStrategies.Tunnel, handledEventsToo: true);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _keysFrom?.RemoveHandler(KeyDownEvent, OnModifierKey);
            _keysFrom?.RemoveHandler(KeyUpEvent, OnModifierKey);
            _keysFrom = null;
        }

        /// <summary>Ctrl pressed or let go with the pointer over a highway, or while scrubbing, shows the notes falling or the whole chart at once.</summary>
        private void OnModifierKey(object? sender, KeyEventArgs e)
        {
            if (_hoveredLane >= 0 || _isScrubbing)
                SetFine(LaneScrub.IsFine(e));
        }

        private void SetFine(bool isFine)
        {
            if (isFine == _isFine)
                return;

            _isFine = isFine;
            if (!isFine)
                _fine.Stop();
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            if (Scene is not { Lanes.Count: > 0 } scene || Bounds.Width <= 0 || Bounds.Height <= 0)
                return;

            // The time read for this frame, rather than reading the clock again.
            double now = (_drawnAt != TimeSpan.MinValue ? _drawnAt : Clock?.Invoke() ?? TimeSpan.Zero).TotalSeconds;
            double lookAhead = Math.Max(0.1, LookAhead);
            double height = Bounds.Height;
            double strike = height * StrikeFraction;
            double Y(double time) => strike - ((time - now) / lookAhead * strike);

            int count = scene.Lanes.Count;

            // What's past the strike line shows for as long as it takes to fall off the bottom.
            double from = now - ((height - strike) / strike * lookAhead);
            double to = now + lookAhead;

            // One gem size for every highway, from the narrowest lanes, leaving room for the names below the strike bar.
            double narrowest = HighwayRect(scene, 0).Width / scene.MaxLaneCount;
            double gem = Math.Clamp(narrowest * 0.42, 5, Math.Max(5, Math.Min(18, (height - strike - 13) / 0.9)));

            var waveforms = Waveforms;
            for (int i = 0; i < count; i++)
            {
                var bounds = HighwayRect(scene, i);

                // Transparent, so the whole highway takes the pointer, and the gaps between them don't.
                context.DrawRectangle(Brushes.Transparent, null, bounds);
                if (i == _hoveredLane && !_isFine)
                {
                    DrawOverview(context, scene.Lanes[i], bounds, now);
                    continue;
                }

                var waveform = waveforms is not null && i < waveforms.Count ? waveforms[i] : null;
                DrawHighway(context, scene, scene.Lanes[i], waveform, bounds, strike, gem, now, from, to, Y);
            }
        }

        /// <summary>
        /// Where the <paramref name="index"/>th of <paramref name="scene"/>'s highways is drawn: as wide as the height allows,
        /// narrower when they don't all fit, with small gaps, centred.
        /// </summary>
        private Rect HighwayRect(HighwayScene scene, int index)
        {
            const double GapFraction = 0.1;
            int count = scene.Lanes.Count;
            double width = Math.Min(Bounds.Height * 0.45, Bounds.Width * 0.96 / (count + ((count - 1) * GapFraction)));
            double gap = width * GapFraction;
            double left = (Bounds.Width - ((count * width) + ((count - 1) * gap))) / 2;
            return new Rect(left + (index * (width + gap)), 0, width, Bounds.Height);
        }

        /// <summary>The highway at <paramref name="point"/>, or -1 when it's over none.</summary>
        private int HighwayAt(Point point)
        {
            if (Scene is not { Lanes.Count: > 0 } scene || Bounds.Width <= 0 || Bounds.Height <= 0)
                return -1;

            for (int i = 0; i < scene.Lanes.Count; i++)
            {
                if (HighwayRect(scene, i).Contains(point))
                    return i;
            }

            return -1;
        }

        /// <summary>How much of the song a highway's whole chart covers, in seconds: the song's length, or else the chart's.</summary>
        private double TimelineOf(HighwayScene scene) => LengthSeconds > 0 ? LengthSeconds : scene.EndSeconds + 1;

        /// <summary>
        /// A highway as its whole chart, from the start at the bottom to the end of the song at the top: the notes as small
        /// marks in their lanes, what's been played shaded, a line where the audio is, and one where the pointer is.
        /// </summary>
        private void DrawOverview(DrawingContext context, HighwayLane lane, Rect bounds, double now)
        {
            double timeline = Math.Max(0.1, Scene is { } scene ? TimelineOf(scene) : 0);
            double Y(double time) => bounds.Bottom - (time / timeline * bounds.Height);

            using (context.PushClip(bounds))
            {
                context.FillRectangle(OverviewBrush, bounds);
                double laneWidth = bounds.Width / lane.LaneCount;
                for (int i = 1; i < lane.LaneCount; i++)
                {
                    double x = bounds.X + (i * laneWidth);
                    context.DrawLine(LaneDividerPen, new Point(x, bounds.Top), new Point(x, bounds.Bottom));
                }

                using (context.PushTransform(Matrix.CreateTranslation(bounds.X, bounds.Y)))
                {
                    foreach (var (rect, color) in OverviewMarksOf(lane, bounds.Size, timeline))
                        context.FillRectangle(Brush(color), rect);
                }

                double playhead = Math.Clamp(Y(now), bounds.Top, bounds.Bottom);
                context.FillRectangle(LaneScrub.PlayedBrush, new Rect(bounds.X, playhead, bounds.Width, bounds.Bottom - playhead));
                context.DrawLine(LaneScrub.PlayheadPen, new Point(bounds.X, playhead), new Point(bounds.Right, playhead));
                context.DrawRectangle(null, OverviewEdgePen, bounds.Deflate(0.5));

                if (!double.IsNaN(_hoverY))
                {
                    double at = Math.Clamp(_hoverY, bounds.Top, bounds.Bottom);
                    context.DrawLine(LaneScrub.HoverPen, new Point(bounds.X, at), new Point(bounds.Right, at));
                    LaneScrub.DrawTimeTag(context, TimeAt(at), bounds, at, isAcross: true);
                }
            }

            // At the top, out of the way of the time.
            DrawLabel(context, lane.Label, bounds, bounds.Bottom);
        }

        /// <summary>
        /// <paramref name="lane"/>'s notes as marks in a highway of <paramref name="size"/> covering <paramref name="timeline"/>
        /// seconds, from its top left: kick and open bars, then tails, then gems, each a run of the notes in a lane that fall
        /// on the same pixel row or the next one in the same colour, so a dense chart is a few hundred rectangles. Laid out
        /// again only when one of those changes.
        /// </summary>
        private List<(Rect Rect, Color Color)> OverviewMarksOf(HighwayLane lane, Size size, double timeline)
        {
            if (_overview is { } known && ReferenceEquals(known.Lane, lane) && known.Size == size && known.Timeline == timeline)
                return known.Marks;

            const double GemHeight = 2;
            double laneWidth = size.Width / lane.LaneCount;
            double Y(double time) => size.Height - (time / timeline * size.Height);

            var bars = new List<(Rect, Color)>();
            var tails = new List<(Rect, Color)>();
            var gems = new List<(Rect, Color)>();

            // The run being built in each lane, by its column (-1 for the bars across), for gems and for tails apart.
            var gemRuns = new Dictionary<int, (double Top, double Bottom, Color Color)>();
            var tailRuns = new Dictionary<int, (double Top, double Bottom, Color Color)>();

            Rect GemMark(int column, double top, double bottom) => column < 0
                ? new Rect(2, top, size.Width - 4, bottom - top)
                : new Rect((column * laneWidth) + (laneWidth * 0.14), top, laneWidth * 0.72, bottom - top);

            Rect TailMark(int column, double top, double bottom)
            {
                double width = Math.Max(1.5, laneWidth * 0.2);
                return new Rect(((column + 0.5) * laneWidth) - (width / 2), top, width, bottom - top);
            }

            // Notes are in time order, so each is at or above the one before in its lane.
            static void Add(Dictionary<int, (double Top, double Bottom, Color Color)> runs, List<(Rect, Color)> marks, Func<int, double, double, Rect> rectOf, int column, double top, double bottom, Color color)
            {
                if (runs.TryGetValue(column, out var run))
                {
                    if (run.Color == color && bottom >= run.Top - 1)
                    {
                        runs[column] = (Math.Min(run.Top, top), run.Bottom, color);
                        return;
                    }

                    marks.Add((rectOf(column, run.Top, run.Bottom), run.Color));
                }

                runs[column] = (top, bottom, color);
            }

            for (int i = 0; i < lane.Notes.Length; i++)
            {
                var note = lane.Notes[i];
                int column = note.Lane == ChartNote.OpenLane ? -1 : Column(lane, note.Lane);

                // A black fret's own colour would be lost on the dark highway.
                var color = IsBlackFret(lane, note) ? BlackFretEdgeColor : NoteColor(lane, note);
                double bottom = Y(lane.Starts[i]);
                Add(gemRuns, column < 0 ? bars : gems, GemMark, column, bottom - GemHeight, bottom, color);
                if (column >= 0 && lane.Ends[i] > lane.Starts[i])
                    Add(tailRuns, tails, TailMark, column, Y(lane.Ends[i]), bottom, WithAlpha(color, 0xA0));
            }

            foreach (var (column, run) in tailRuns)
                tails.Add((TailMark(column, run.Top, run.Bottom), run.Color));
            foreach (var (column, run) in gemRuns)
                (column < 0 ? bars : gems).Add((GemMark(column, run.Top, run.Bottom), run.Color));

            List<(Rect, Color)> marks = [.. bars, .. tails, .. gems];
            _overview = new OverviewMarks(lane, size, timeline, marks);
            return marks;
        }

        /// <summary>The song time at <paramref name="y"/> down a highway's whole chart.</summary>
        private double TimeAt(double y) =>
            Scene is { } scene && Bounds.Height > 0 ? Math.Clamp((Bounds.Height - y) / Bounds.Height, 0, 1) * TimelineOf(scene) : 0;

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var point = e.GetPosition(this);
            SetFine(LaneScrub.IsFine(e.KeyModifiers));
            if (_isScrubbing)
            {
                SetHover(_hoveredLane, point.Y);
                Scrubbed?.Invoke(this, new ScrubEventArgs(ScrubTimeAt(point)));
                return;
            }

            SetHover(HighwayAt(point), point.Y);
        }

        /// <summary>
        /// Where a drag to <paramref name="point"/> moves the audio: the time there up the whole chart, or while it's fine, on
        /// from where the audio is by how far down the pointer has gone since it last moved, holding the pointer still.
        /// </summary>
        private double ScrubTimeAt(Point point)
        {
            if (!_isFine || Scene is not { } scene)
            {
                _fine.Stop();
                return TimeAt(point.Y);
            }

            _fine.Start(point);
            double now = (Clock?.Invoke() ?? TimeSpan.Zero).TotalSeconds;
            return Math.Clamp(now + (_fine.Move(point).Y * FineSecondsPerPixel), 0, TimelineOf(scene));
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            if (!_isScrubbing)
                SetHover(-1, double.NaN);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var point = e.GetPosition(this);
            int lane = HighwayAt(point);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || lane < 0)
                return;

            SetHover(lane, point.Y);
            SetFine(LaneScrub.IsFine(e.KeyModifiers));
            _isScrubbing = true;
            e.Pointer.Capture(this);
            ScrubStarted?.Invoke(this, EventArgs.Empty);

            // A press with Ctrl held starts a fine drag from where the audio is.
            if (_isFine)
                _fine.Start(point);
            else
                Scrubbed?.Invoke(this, new ScrubEventArgs(TimeAt(point.Y)));
            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (!_isScrubbing)
                return;

            var point = e.GetPosition(this);
            SetFine(LaneScrub.IsFine(e.KeyModifiers));
            Scrubbed?.Invoke(this, new ScrubEventArgs(ScrubTimeAt(point)));
            e.Pointer.Capture(null);
            EndScrub();

            // Let go off the highway, it goes back to the notes falling.
            SetHover(HighwayAt(point), point.Y);
            e.Handled = true;
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            EndScrub();
        }

        private void EndScrub()
        {
            if (!_isScrubbing)
                return;

            _isScrubbing = false;
            _fine.Stop();
            ScrubEnded?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Shows <paramref name="lane"/>'s whole chart, with the pointer <paramref name="y"/> down it; -1 shows none.</summary>
        private void SetHover(int lane, double y)
        {
            if (lane < 0)
                y = double.NaN;
            if (lane == _hoveredLane && (y == _hoverY || (double.IsNaN(y) && double.IsNaN(_hoverY))))
                return;

            if (lane != _hoveredLane)
                Cursor = lane >= 0 ? LaneScrub.HandCursor : null;
            _hoveredLane = lane;
            _hoverY = y;
            InvalidateVisual();
        }

        private void DrawHighway(DrawingContext context, HighwayScene scene, HighwayLane lane, Waveform? waveform, Rect bounds, double strike, double gem, double now, double from, double to, Func<double, double> y)
        {
            double laneWidth = bounds.Width / lane.LaneCount;
            var hits = HitsAt(lane, now);

            using (context.PushClip(bounds))
            using (context.PushOpacityMask(FadeMask, bounds))
            {
                context.FillRectangle(HighwayBrush, bounds);

                // The track as it's heard, inside the rails, so a note can be lined up with the sound it's played to. Scaled
                // so its loudest peak fills the highway, so a quiet track's beats show as clearly as a loud one's.
                if (waveform is not null)
                {
                    LaneWaveformPainter.Draw(context, waveform, bounds.Deflate(new Thickness(4, 0)), isVertical: true, from, to, y,
                        WaveformPeakBrush, WaveformLoudBrush, LaneWaveformPainter.FullScaleGain(waveform), 1 / (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1));
                }

                // A beam of light rises from a lane as it's struck.
                for (int column = 0; column < lane.LaneCount; column++)
                {
                    if (hits[column] > 0)
                    {
                        using (context.PushOpacity(hits[column] * 0.8))
                            context.FillRectangle(Beam(HitColor(lane, column)), new Rect(bounds.X + (column * laneWidth), strike * 0.45, laneWidth, strike * 0.55));
                    }
                }

                for (int i = 1; i < lane.LaneCount; i++)
                {
                    double x = bounds.X + (i * laneWidth);
                    context.DrawLine(LaneDividerPen, new Point(x, 0), new Point(x, strike));
                }

                for (int i = LowerBound(scene.BeatTimes, from); i < scene.BeatTimes.Length && scene.BeatTimes[i] <= to; i++)
                {
                    double at = y(scene.BeatTimes[i]);
                    if (at < strike)
                        context.DrawLine(scene.IsMeasure[i] ? MeasurePen : BeatPen, new Point(bounds.X, at), new Point(bounds.Right, at));
                }

                // The rails down each side, as far as the strike bar.
                double railEnd = strike + (gem * 0.85);
                context.FillRectangle(RailBrush, new Rect(bounds.X, 0, 2, railEnd));
                context.FillRectangle(RailBrush, new Rect(bounds.Right - 2, 0, 2, railEnd));

                DrawStrikeBar(context, lane, bounds, laneWidth, strike, gem, hits);
                DrawNotes(context, lane, bounds, laneWidth, gem, now, from, to, y, AreNotesOutlined);
            }

            DrawLabel(context, lane.Label, bounds, strike + (gem * 0.9));
        }

        /// <summary>How brightly each column is lit, from 0 to 1: just struck, or held.</summary>
        private double[] HitsAt(HighwayLane lane, double now)
        {
            if (_hits.Length < lane.LaneCount)
                _hits = new double[lane.LaneCount];
            var hits = _hits;
            Array.Clear(hits, 0, lane.LaneCount);
            for (int i = LowerBound(lane.Starts, now - Math.Max(HitGlow, lane.MaxLength)); i < lane.Starts.Length && lane.Starts[i] <= now; i++)
            {
                var note = lane.Notes[i];
                double since = now - lane.Starts[i];
                double glow = since < HitGlow ? 1 - (since / HitGlow) : 0;
                if (lane.Ends[i] > now && lane.Ends[i] > lane.Starts[i])
                    glow = Math.Max(glow, 0.55);
                if (glow <= 0)
                    continue;

                // An open note or kick lights every lane.
                if (note.Lane == ChartNote.OpenLane)
                {
                    for (int c = 0; c < lane.LaneCount; c++)
                        hits[c] = Math.Max(hits[c], glow * 0.6);
                }
                else
                {
                    int column = Column(lane, note.Lane);
                    hits[column] = Math.Max(hits[column], glow);
                }
            }

            return hits;
        }

        /// <summary>The dark bar across the strike line, with a target per lane that lights as it's struck.</summary>
        private void DrawStrikeBar(DrawingContext context, HighwayLane lane, Rect bounds, double laneWidth, double strike, double gem, double[] hits)
        {
            var band = new Rect(bounds.X, strike - (gem * 0.85), bounds.Width, gem * 1.7);
            context.FillRectangle(StrikeBandBrush, band);
            context.DrawLine(StrikeEdgePen, band.TopLeft, band.TopRight);
            context.DrawLine(StrikeEdgePen, band.BottomLeft, band.BottomRight);

            for (int column = 0; column < lane.LaneCount; column++)
            {
                var color = TargetColor(lane, column);
                var rect = GemRect(bounds, laneWidth, column, strike, gem);
                double radius = CornerOf(rect);
                double hit = hits[column];

                // Hollow until struck, then filled in the lane's colour.
                var fill = hit > 0 ? WithAlpha(color, (byte)(0x50 + (hit * 0xAF))) : WithAlpha(Darken(color, 0.72), 0xC8);
                context.DrawRectangle(Brush(fill), Pen(hit > 0 ? Lighten(color, 0.4) : color, 2), rect.Deflate(1), radius, radius);
            }
        }

        /// <param name="outlined">Whether each note is drawn as its outline only, so what's behind it shows through.</param>
        private void DrawNotes(DrawingContext context, HighwayLane lane, Rect bounds, double laneWidth, double gem, double now, double from, double to, Func<double, double> y, bool outlined)
        {
            int first = LowerBound(lane.Starts, from - lane.MaxLength);
            int last = LowerBound(lane.Starts, to + 0.001);

            // In layers: tails at the back, then kick and open bars, then the gems in front of them.
            // Within a layer, latest first, so an earlier note is drawn over a later one.
            for (int i = last - 1; i >= first; i--)
            {
                // A sustain's tail is eaten from the strike line as it's held.
                var note = lane.Notes[i];
                if (lane.Ends[i] > lane.Starts[i] && lane.Ends[i] > now && note.Lane >= 0)
                    DrawTail(context, lane, note, NoteColor(lane, note), bounds, laneWidth, y(Math.Min(lane.Ends[i], to)), y(Math.Max(lane.Starts[i], now)), outlined);
            }

            foreach (bool bars in (ReadOnlySpan<bool>)[true, false])
            {
                for (int i = last - 1; i >= first; i--)
                {
                    // A note is struck, and goes, as it reaches the line.
                    var note = lane.Notes[i];
                    if (lane.Starts[i] < now || (note.Lane == ChartNote.OpenLane) != bars)
                        continue;

                    DrawGem(context, lane, note, NoteColor(lane, note), bounds, laneWidth, y(lane.Starts[i]), gem, outlined);
                }
            }
        }

        private void DrawTail(DrawingContext context, HighwayLane lane, ChartNote note, Color color, Rect bounds, double laneWidth, double top, double bottom, bool outlined)
        {
            if (bottom <= top)
                return;

            double center = bounds.X + ((Column(lane, note.Lane) + 0.5) * laneWidth);
            double width = Math.Max(3, laneWidth * 0.2);
            var tailColor = IsBlackFret(lane, note) ? BlackFretEdgeColor : color;
            var tail = new Rect(center - (width / 2), top, width, bottom - top);
            if (outlined)
                context.DrawRectangle(null, Pen(tailColor, 1), tail.Deflate(0.5), 1, 1);
            else
                context.DrawRectangle(Brush(WithAlpha(tailColor, 0xCC)), OutlinePen, tail, 1, 1);
        }

        private void DrawGem(DrawingContext context, HighwayLane lane, ChartNote note, Color color, Rect bounds, double laneWidth, double at, double gem, bool outlined)
        {
            if (note.Lane == ChartNote.OpenLane)
            {
                // An open note or a kick: a flat bar across the highway.
                double barHeight = Math.Max(4, gem * 0.45);
                var bar = new Rect(bounds.X + 4, at - (barHeight / 2), bounds.Width - 8, barHeight);
                DrawFlat(context, bar, color, outlined: outlined);
                if ((note.Flags & NoteFlags.DoubleKick) != 0)
                    context.DrawRectangle(null, Pen(WhiteFretColor, 1), bar.Deflate(1.5), 1, 1);
                return;
            }

            var rect = GemRect(bounds, laneWidth, Column(lane, note.Lane), at, gem);

            // Six-fret: a black fret played with a white one covers its top half.
            if (IsBlackFret(lane, note) && lane.SharesColumn.Contains((note.Tick, note.Lane % 3)))
                rect = rect.WithHeight(rect.Height / 2);

            // Outlined, a black fret's own colour would be lost on the dark highway, so it's drawn in grey.
            var edge = outlined && IsBlackFret(lane, note) ? BlackFretEdgeColor : color;

            if ((note.Flags & NoteFlags.Cymbal) != 0)
            {
                DrawCymbal(context, rect, edge, outlined);
                return;
            }

            if ((note.Flags & NoteFlags.Tap) != 0)
            {
                // A tap: dark, with the lane's colour as its border and a mark in the middle.
                double radius = CornerOf(rect);
                context.DrawRectangle(outlined ? null : TapBodyBrush, Pen(edge, 2), rect.Deflate(1), radius, radius);
                if (outlined)
                    context.DrawRectangle(null, Pen(edge, 1), Mark(rect).Deflate(0.5), 1, 1);
                else
                    context.FillRectangle(Brush(color), Mark(rect), 1f);
                return;
            }

            DrawFlat(context, rect, edge, IsBlackFret(lane, note) ? Color.FromRgb(0xC8, 0xC8, 0xD0) : null, outlined);

            // A hammer-on: a white mark across the face.
            if ((note.Flags & NoteFlags.Hopo) != 0)
            {
                if (outlined)
                    context.DrawRectangle(null, Pen(WhiteFretColor, 1), Mark(rect).Deflate(0.5), 1, 1);
                else
                    context.FillRectangle(HopoMarkBrush, Mark(rect), 1f);
            }
        }

        /// <summary>
        /// A flat gem: its face in the colour over a darker lower edge, like a key seen from the front,
        /// with a thin dark outline so it reads over any background. Outlined, only its edge is drawn, in the colour.
        /// </summary>
        private void DrawFlat(DrawingContext context, Rect rect, Color color, Color? outline = null, bool outlined = false)
        {
            double radius = CornerOf(rect);
            if (outlined)
            {
                context.DrawRectangle(null, Pen(color, 1.5), rect.Deflate(0.75), radius, radius);
                return;
            }

            context.DrawRectangle(Brush(Darken(color, 0.38)), null, rect, radius, radius);
            context.DrawRectangle(Brush(color), null, rect.WithHeight(rect.Height * 0.72), radius, radius);
            context.DrawRectangle(null, outline is { } light ? Pen(light, 1) : OutlinePen, rect, radius, radius);
        }

        /// <summary>A cymbal: a flat hexagon, pointed at the sides, in two tones like a gem; outlined, only its edge in the colour.</summary>
        private void DrawCymbal(DrawingContext context, Rect rect, Color color, bool outlined)
        {
            var hexagon = Cymbal(rect.Size);
            using (context.PushTransform(Matrix.CreateTranslation(rect.X, rect.Y)))
            {
                if (outlined)
                {
                    context.DrawGeometry(null, Pen(color, 1.5), hexagon);
                    return;
                }

                var face = new Rect(rect.Size);
                using (context.PushGeometryClip(hexagon))
                {
                    context.FillRectangle(Brush(Darken(color, 0.38)), face);
                    context.FillRectangle(Brush(color), face.WithHeight(face.Height * 0.62));
                }

                context.DrawGeometry(null, OutlinePen, hexagon);
            }
        }

        /// <summary>A cymbal's hexagon filling <paramref name="size"/> from the origin, made once for each size.</summary>
        private StreamGeometry Cymbal(Size size)
        {
            if (_cymbals.TryGetValue(size, out var hexagon))
                return hexagon;

            // Resized often (a window being dragged larger), the sizes no longer drawn go.
            if (_cymbals.Count > 16)
                _cymbals.Clear();

            double point = Math.Min(size.Height * 0.6, size.Width * 0.25);
            hexagon = new StreamGeometry();
            using (var g = hexagon.Open())
            {
                g.BeginFigure(new Point(0, size.Height / 2), true);
                g.LineTo(new Point(point, 0));
                g.LineTo(new Point(size.Width - point, 0));
                g.LineTo(new Point(size.Width, size.Height / 2));
                g.LineTo(new Point(size.Width - point, size.Height));
                g.LineTo(new Point(point, size.Height));
                g.EndFigure(true);
            }

            _cymbals[size] = hexagon;
            return hexagon;
        }

        /// <summary>The mark across the face of a HOPO or tap gem.</summary>
        private static Rect Mark(Rect rect) =>
            new(rect.X + (rect.Width * 0.28), rect.Y + (rect.Height * 0.24), rect.Width * 0.44, Math.Max(1.5, rect.Height * 0.24));

        private static double CornerOf(Rect rect) => Math.Min(3, rect.Height * 0.25);

        private void DrawLabel(DrawingContext context, string label, Rect bounds, double top)
        {
            double width = Math.Max(1, bounds.Width - 4);
            if (!_labels.TryGetValue(label, out var text))
            {
                text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), 10.5, LabelBrush)
                {
                    MaxTextWidth = width,
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis,
                };
                _labels[label] = text;
            }
            else if (text.MaxTextWidth != width)
            {
                // The highway is as wide as the view and the number of lanes allow, which both change.
                text.MaxTextWidth = width;
            }

            // Below the strike bar when there's room, otherwise at the top.
            double y = top + 2 + text.Height <= bounds.Bottom ? top + 1 : 4;
            context.DrawText(text, new Point(bounds.X + ((bounds.Width - Math.Min(text.Width, bounds.Width)) / 2), y));
        }

        private static Rect GemRect(Rect bounds, double laneWidth, int column, double at, double gem)
        {
            double width = laneWidth * 0.86;
            return new Rect(bounds.X + (column * laneWidth) + ((laneWidth - width) / 2), at - (gem / 2), width, gem);
        }

        /// <summary>The column a lane is drawn in: six-fret's black and white frets share three.</summary>
        private static int Column(HighwayLane lane, int noteLane) =>
            lane.Kind == ChartInstrumentKind.SixFret ? noteLane % 3 : Math.Min(noteLane, lane.LaneCount - 1);

        private static bool IsBlackFret(HighwayLane lane, ChartNote note) => lane.Kind == ChartInstrumentKind.SixFret && note.Lane >= 3;

        private static Color TargetColor(HighwayLane lane, int column) => lane.Kind switch
        {
            ChartInstrumentKind.FiveFret => FretColors[column],
            ChartInstrumentKind.Drums => (lane.LaneCount == 5 ? FivePadColors : FourPadColors)[column],
            _ => Color.FromRgb(0xB8, 0xB8, 0xC4),
        };

        private static Color HitColor(HighwayLane lane, int column) =>
            lane.Kind == ChartInstrumentKind.SixFret ? WhiteFretColor : TargetColor(lane, column);

        private static Color NoteColor(HighwayLane lane, ChartNote note)
        {
            if ((note.Flags & NoteFlags.StarPower) != 0)
                return StarPowerColor;
            if (note.Lane == ChartNote.OpenLane)
                return lane.Kind == ChartInstrumentKind.Drums ? KickColor : OpenColor;
            if (lane.Kind == ChartInstrumentKind.SixFret)
                return note.Lane >= 3 ? BlackFretColor : WhiteFretColor;
            return TargetColor(lane, Column(lane, note.Lane));
        }

        private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

        private static Color Lighten(Color color, double amount) => Color.FromArgb(
            color.A,
            (byte)(color.R + ((255 - color.R) * amount)),
            (byte)(color.G + ((255 - color.G) * amount)),
            (byte)(color.B + ((255 - color.B) * amount)));

        private static Color Darken(Color color, double amount) => Color.FromArgb(
            color.A, (byte)(color.R * (1 - amount)), (byte)(color.G * (1 - amount)), (byte)(color.B * (1 - amount)));

        private static IBrush VerticalGradient(params (double Offset, Color Color)[] stops) => new ImmutableLinearGradientBrush(
            [.. stops.Select(s => new ImmutableGradientStop(s.Offset, s.Color))],
            startPoint: new RelativePoint(0.5, 0, RelativeUnit.Relative),
            endPoint: new RelativePoint(0.5, 1, RelativeUnit.Relative));

        private IBrush Brush(Color color)
        {
            if (!_brushes.TryGetValue(color, out var brush))
                _brushes[color] = brush = new ImmutableSolidColorBrush(color);
            return brush;
        }

        /// <summary>A struck lane's beam: clear at the top, the lane's colour at the strike line.</summary>
        private IBrush Beam(Color color)
        {
            if (!_beams.TryGetValue(color, out var brush))
                _beams[color] = brush = VerticalGradient((0, WithAlpha(color, 0)), (1, WithAlpha(color, 0x70)));
            return brush;
        }

        private IPen Pen(Color color, double thickness)
        {
            if (!_pens.TryGetValue((color, thickness), out var pen))
                _pens[(color, thickness)] = pen = new ImmutablePen(color.ToUInt32(), thickness);
            return pen;
        }

        /// <summary>The index of the first of <paramref name="times"/>, which are in order, at or after <paramref name="time"/>.</summary>
        private static int LowerBound(double[] times, double time)
        {
            int lo = 0, hi = times.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (times[mid] < time)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            return lo;
        }

        /// <summary>Asks to be called back when the screen is next drawn, while there's a highway to draw.</summary>
        private void RequestFrame()
        {
            if (_isFrameRequested || Scene is not { Lanes.Count: > 0 } || !IsVisible || TopLevel.GetTopLevel(this) is not { } topLevel)
                return;

            _isFrameRequested = true;
            topLevel.RequestAnimationFrame(OnFrame);
        }

        private void OnFrame(TimeSpan _)
        {
            _isFrameRequested = false;
            if (TopLevel.GetTopLevel(this) is null)
                return;

            // Paused, nothing moves, so nothing is redrawn; nor is it while it's scrolled out of view.
            if (!IsEffectivelyVisible || !IsInViewport)
            {
                RequestFrame();
                return;
            }

            var clock = Clock?.Invoke() ?? TimeSpan.Zero;
            if (clock != _drawnAt)
            {
                _drawnAt = clock;
                InvalidateVisual();
            }

            RequestFrame();
        }

        /// <summary>A highway's whole-chart marks, and the highway, size and length of song they were laid out for.</summary>
        private sealed record OverviewMarks(HighwayLane Lane, Size Size, double Timeline, List<(Rect Rect, Color Color)> Marks);
    }
}
