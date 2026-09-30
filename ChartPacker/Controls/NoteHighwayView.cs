using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
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
        }

        public IReadOnlyList<HighwayLane> Lanes { get; }

        public double[] BeatTimes { get; }

        public bool[] IsMeasure { get; }
    }

    /// <summary>
    /// Draws up to four flat note highways side by side, notes falling toward a strike line just
    /// above the bottom. Each frame the screen is drawn it reads <see cref="Clock"/>, so the notes
    /// follow the audio whether it's playing, paused or being dragged.
    /// </summary>
    /// <example><c>&lt;ctl:NoteHighwayView Scene="{Binding Highway.Scene}" Clock="{Binding Highway.Clock}" LookAhead="{Binding Highway.LookAhead}" /&gt;</c></example>
    public sealed class NoteHighwayView : Control
    {
        public static readonly StyledProperty<HighwayScene?> SceneProperty =
            AvaloniaProperty.Register<NoteHighwayView, HighwayScene?>(nameof(Scene));

        public static readonly StyledProperty<Func<TimeSpan>?> ClockProperty =
            AvaloniaProperty.Register<NoteHighwayView, Func<TimeSpan>?>(nameof(Clock));

        public static readonly StyledProperty<double> LookAheadProperty =
            AvaloniaProperty.Register<NoteHighwayView, double>(nameof(LookAhead), 1.4);

        // Where the strike line sits, as a fraction of the height from the top.
        private const double StrikeFraction = 0.9;

        // How long a lane's target glows after a note reaches it, in seconds.
        private const double HitGlow = 0.12;

        private static readonly IBrush HighwayBrush = new SolidColorBrush(Color.FromArgb(0xA6, 0x08, 0x08, 0x0C));
        private static readonly IPen LaneDividerPen = new Pen(new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), 1);
        private static readonly IPen BeatPen = new Pen(new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)), 1);
        private static readonly IPen MeasurePen = new Pen(new SolidColorBrush(Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF)), 2);
        private static readonly IPen StrikePen = new Pen(new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)), 2);
        private static readonly IBrush LabelBackBrush = new SolidColorBrush(Color.FromArgb(0x99, 0x00, 0x00, 0x00));
        private static readonly IBrush LabelBrush = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
        private static readonly IBrush HopoBrush = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
        private static readonly IBrush TapFillBrush = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1F));
        private static readonly Color StarPowerColor = Color.FromRgb(0x8F, 0xEE, 0xFF);
        private static readonly Color OpenColor = Color.FromRgb(0xB0, 0x4C, 0xE0);
        private static readonly Color KickColor = Color.FromRgb(0xFB, 0x8C, 0x00);
        private static readonly Color WhiteFretColor = Color.FromRgb(0xEE, 0xEE, 0xEE);
        private static readonly Color BlackFretColor = Color.FromRgb(0x24, 0x24, 0x28);

        private static readonly Color Green = Color.FromRgb(0x2E, 0xB8, 0x4E);
        private static readonly Color Red = Color.FromRgb(0xE5, 0x39, 0x35);
        private static readonly Color Yellow = Color.FromRgb(0xFD, 0xD8, 0x35);
        private static readonly Color Blue = Color.FromRgb(0x1E, 0x88, 0xE5);
        private static readonly Color Orange = Color.FromRgb(0xFB, 0x8C, 0x00);

        private static readonly Color[] FretColors = [Green, Red, Yellow, Blue, Orange];
        private static readonly Color[] FourPadColors = [Red, Yellow, Blue, Green];
        private static readonly Color[] FivePadColors = [Red, Yellow, Blue, Orange, Green];

        private readonly Dictionary<Color, IBrush> _brushes = [];
        private readonly Dictionary<(Color, double), IPen> _pens = [];
        private readonly Dictionary<string, FormattedText> _labels = [];
        private bool _isFrameRequested;
        private TimeSpan _drawnAt = TimeSpan.MinValue;

        static NoteHighwayView()
        {
            AffectsRender<NoteHighwayView>(SceneProperty, LookAheadProperty);
        }

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

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == SceneProperty || change.Property == IsVisibleProperty)
                RequestFrame();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            RequestFrame();
        }

        public override void Render(DrawingContext context)
        {
            if (Scene is not { Lanes.Count: > 0 } scene || Bounds.Width <= 0 || Bounds.Height <= 0)
                return;

            double now = (Clock?.Invoke() ?? TimeSpan.Zero).TotalSeconds;
            double lookAhead = Math.Max(0.1, LookAhead);
            double height = Bounds.Height;
            double strike = height * StrikeFraction;
            double Y(double time) => strike - ((time - now) / lookAhead * strike);

            // Highways as wide as the height allows, narrower when four don't fit, with small gaps.
            int count = scene.Lanes.Count;
            const double GapFraction = 0.08;
            double width = Math.Min(height * 0.45, Bounds.Width * 0.96 / (count + ((count - 1) * GapFraction)));
            double gap = width * GapFraction;
            double left = (Bounds.Width - ((count * width) + ((count - 1) * gap))) / 2;

            // What's past the strike line shows for as long as it takes to fall off the bottom.
            double from = now - ((height - strike) / strike * lookAhead);
            double to = now + lookAhead;

            for (int i = 0; i < count; i++)
                DrawHighway(context, scene, scene.Lanes[i], new Rect(left + (i * (width + gap)), 0, width, height), strike, now, from, to, Y);
        }

        private void DrawHighway(DrawingContext context, HighwayScene scene, HighwayLane lane, Rect bounds, double strike, double now, double from, double to, Func<double, double> y)
        {
            context.FillRectangle(HighwayBrush, bounds);
            double laneWidth = bounds.Width / lane.LaneCount;
            for (int i = 1; i < lane.LaneCount; i++)
            {
                double x = bounds.X + (i * laneWidth);
                context.DrawLine(LaneDividerPen, new Point(x, 0), new Point(x, bounds.Height));
            }

            for (int i = LowerBound(scene.BeatTimes, from); i < scene.BeatTimes.Length && scene.BeatTimes[i] <= to; i++)
            {
                double at = y(scene.BeatTimes[i]);
                context.DrawLine(scene.IsMeasure[i] ? MeasurePen : BeatPen, new Point(bounds.X, at), new Point(bounds.Right, at));
            }

            double gem = Math.Clamp(laneWidth * 0.36, 5, 14);
            using (context.PushClip(bounds))
            {
                DrawTargets(context, lane, bounds, laneWidth, strike, gem, now);
                DrawNotes(context, lane, bounds, laneWidth, gem, now, from, to, y);
            }

            context.DrawLine(StrikePen, new Point(bounds.X, strike), new Point(bounds.Right, strike));
            DrawLabel(context, lane.Label, bounds);
        }

        /// <summary>Each lane's target on the strike line, glowing briefly as a note reaches it.</summary>
        private void DrawTargets(DrawingContext context, HighwayLane lane, Rect bounds, double laneWidth, double strike, double gem, double now)
        {
            var hit = new double[lane.LaneCount];
            for (int i = LowerBound(lane.Starts, now - HitGlow); i < lane.Starts.Length && lane.Starts[i] <= now; i++)
            {
                if (lane.Notes[i].Lane >= 0)
                    hit[Column(lane, lane.Notes[i].Lane)] = Math.Max(hit[Column(lane, lane.Notes[i].Lane)], 1 - ((now - lane.Starts[i]) / HitGlow));
            }

            for (int column = 0; column < lane.LaneCount; column++)
            {
                var color = TargetColor(lane, column);
                var rect = GemRect(bounds, laneWidth, column, strike, gem);
                if (hit[column] > 0)
                    context.FillRectangle(Brush(WithAlpha(color, (byte)(hit[column] * 0xCC))), rect.Inflate(3), 4f);
                context.DrawRectangle(null, Pen(WithAlpha(color, 0xCC), 2), rect, 3, 3);
            }
        }

        private void DrawNotes(DrawingContext context, HighwayLane lane, Rect bounds, double laneWidth, double gem, double now, double from, double to, Func<double, double> y)
        {
            // Latest first, so earlier notes are drawn over the tails of later ones.
            int first = LowerBound(lane.Starts, from - lane.MaxLength);
            int last = LowerBound(lane.Starts, to + 0.001);
            for (int i = last - 1; i >= first; i--)
            {
                var note = lane.Notes[i];
                double start = lane.Starts[i];
                double end = lane.Ends[i];
                var color = NoteColor(lane, note);

                // A sustain's tail is eaten from the strike line as it's held.
                if (end > start && end > now && note.Lane >= 0)
                {
                    double tailWidth = Math.Max(3, laneWidth * 0.2);
                    double x = bounds.X + ((Column(lane, note.Lane) + 0.5) * laneWidth) - (tailWidth / 2);
                    double top = y(Math.Min(end, to));
                    double bottom = y(Math.Max(start, now));
                    if (bottom > top)
                        context.FillRectangle(Brush(WithAlpha(color, 0xC0)), new Rect(x, top, tailWidth, bottom - top), 2f);
                }

                // A note is struck, and goes, as it reaches the line.
                if (start < now)
                    continue;

                DrawGem(context, lane, note, color, bounds, laneWidth, y(start), gem);
            }
        }

        private void DrawGem(DrawingContext context, HighwayLane lane, ChartNote note, Color color, Rect bounds, double laneWidth, double at, double gem)
        {
            if (note.Lane == ChartNote.OpenLane)
            {
                // An open note or a kick: a thin bar across the highway.
                double barHeight = Math.Max(3, gem * 0.45);
                var bar = new Rect(bounds.X + 2, at - (barHeight / 2), bounds.Width - 4, barHeight);
                context.FillRectangle(Brush(color), bar, (float)(barHeight / 2));
                if ((note.Flags & NoteFlags.DoubleKick) != 0)
                    context.DrawRectangle(null, Pen(Colors.White, 1), bar, barHeight / 2, barHeight / 2);
                return;
            }

            var rect = GemRect(bounds, laneWidth, Column(lane, note.Lane), at, gem);
            if ((note.Flags & NoteFlags.Cymbal) != 0)
            {
                context.DrawEllipse(Brush(color), Pen(Colors.White, 1.5), rect.Center, rect.Width / 2, rect.Height * 0.62);
                return;
            }

            // Six-fret: black frets are dark with a light rim, and cover the top half when a white one shares the column.
            if (lane.Kind == ChartInstrumentKind.SixFret && note.Lane >= 3 && lane.SharesColumn.Contains((note.Tick, note.Lane % 3)))
                rect = rect.WithHeight(rect.Height / 2);

            if ((note.Flags & NoteFlags.Tap) != 0)
            {
                context.DrawRectangle(TapFillBrush, Pen(color, 2.5), rect, 3, 3);
                return;
            }

            context.DrawRectangle(Brush(color), lane.Kind == ChartInstrumentKind.SixFret && note.Lane >= 3 ? Pen(WhiteFretColor, 1) : null, rect, 3, 3);
            if ((note.Flags & NoteFlags.Hopo) != 0)
            {
                // A HOPO has a light core, like the games' smaller hammer-on gems.
                var core = rect.Deflate(new Thickness(rect.Width * 0.22, rect.Height * 0.3));
                context.FillRectangle(HopoBrush, core, 2f);
            }
        }

        private void DrawLabel(DrawingContext context, string label, Rect bounds)
        {
            if (!_labels.TryGetValue(label, out var text))
            {
                text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11, LabelBrush);
                _labels[label] = text;
            }

            var back = new Rect(bounds.X + 4, 4, Math.Min(text.Width + 10, bounds.Width - 8), text.Height + 4);
            context.FillRectangle(LabelBackBrush, back, 3f);
            using (context.PushClip(back))
                context.DrawText(text, new Point(back.X + 5, back.Y + 2));
        }

        private static Rect GemRect(Rect bounds, double laneWidth, int column, double at, double gem)
        {
            double width = laneWidth * 0.84;
            return new Rect(bounds.X + (column * laneWidth) + ((laneWidth - width) / 2), at - (gem / 2), width, gem);
        }

        /// <summary>The column a lane is drawn in: six-fret's black and white frets share three.</summary>
        private static int Column(HighwayLane lane, int noteLane) =>
            lane.Kind == ChartInstrumentKind.SixFret ? noteLane % 3 : Math.Min(noteLane, lane.LaneCount - 1);

        private static Color TargetColor(HighwayLane lane, int column) => lane.Kind switch
        {
            ChartInstrumentKind.FiveFret => FretColors[column],
            ChartInstrumentKind.Drums => (lane.LaneCount == 5 ? FivePadColors : FourPadColors)[column],
            _ => WhiteFretColor,
        };

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

        private IBrush Brush(Color color)
        {
            if (!_brushes.TryGetValue(color, out var brush))
                _brushes[color] = brush = new SolidColorBrush(color).ToImmutable();
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

            // Paused, nothing moves, so nothing is redrawn.
            var clock = Clock?.Invoke() ?? TimeSpan.Zero;
            if (clock != _drawnAt && IsEffectivelyVisible)
            {
                _drawnAt = clock;
                InvalidateVisual();
            }

            RequestFrame();
        }
    }
}
