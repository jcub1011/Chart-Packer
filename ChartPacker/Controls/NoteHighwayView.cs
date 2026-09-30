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

        private static readonly IPen LaneDividerPen = new ImmutablePen(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF).ToUInt32(), 1);
        private static readonly IPen BeatPen = new ImmutablePen(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF).ToUInt32(), 1);
        private static readonly IPen MeasurePen = new ImmutablePen(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF).ToUInt32(), 2);
        private static readonly IBrush StrikeBandBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xC0, 0x00, 0x00, 0x00));
        private static readonly IPen StrikeEdgePen = new ImmutablePen(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF).ToUInt32(), 1);
        private static readonly IPen OutlinePen = new ImmutablePen(Color.FromArgb(0xA0, 0x00, 0x00, 0x00).ToUInt32(), 1);
        private static readonly IBrush HopoMarkBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
        private static readonly IBrush TapBodyBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x16, 0x16, 0x1B));
        private static readonly IBrush LabelBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF));

        private static readonly Color StarPowerColor = Color.FromRgb(0x7F, 0xE9, 0xFF);
        private static readonly Color OpenColor = Color.FromRgb(0xB0, 0x4C, 0xE0);
        private static readonly Color KickColor = Color.FromRgb(0xFB, 0x8C, 0x00);
        private static readonly Color WhiteFretColor = Color.FromRgb(0xF2, 0xF2, 0xF4);
        private static readonly Color BlackFretColor = Color.FromRgb(0x2A, 0x2A, 0x32);

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
            const double GapFraction = 0.1;
            double width = Math.Min(height * 0.45, Bounds.Width * 0.96 / (count + ((count - 1) * GapFraction)));
            double gap = width * GapFraction;
            double left = (Bounds.Width - ((count * width) + ((count - 1) * gap))) / 2;

            // What's past the strike line shows for as long as it takes to fall off the bottom.
            double from = now - ((height - strike) / strike * lookAhead);
            double to = now + lookAhead;

            // One gem size for every highway, from the narrowest lanes, leaving room for the names below the strike bar.
            double narrowest = width / scene.Lanes.Max(l => l.LaneCount);
            double gem = Math.Clamp(narrowest * 0.42, 5, Math.Max(5, Math.Min(18, (height - strike - 13) / 0.9)));

            for (int i = 0; i < count; i++)
                DrawHighway(context, scene, scene.Lanes[i], new Rect(left + (i * (width + gap)), 0, width, height), strike, gem, now, from, to, Y);
        }

        private void DrawHighway(DrawingContext context, HighwayScene scene, HighwayLane lane, Rect bounds, double strike, double gem, double now, double from, double to, Func<double, double> y)
        {
            double laneWidth = bounds.Width / lane.LaneCount;
            var hits = HitsAt(lane, now);

            using (context.PushClip(bounds))
            using (context.PushOpacityMask(FadeMask, bounds))
            {
                context.FillRectangle(HighwayBrush, bounds);

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
                DrawNotes(context, lane, bounds, laneWidth, gem, now, from, to, y);
            }

            DrawLabel(context, lane.Label, bounds, strike + (gem * 0.9));
        }

        /// <summary>How brightly each column is lit, from 0 to 1: just struck, or held.</summary>
        private static double[] HitsAt(HighwayLane lane, double now)
        {
            var hits = new double[lane.LaneCount];
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
                    for (int c = 0; c < hits.Length; c++)
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

        private void DrawNotes(DrawingContext context, HighwayLane lane, Rect bounds, double laneWidth, double gem, double now, double from, double to, Func<double, double> y)
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
                    DrawTail(context, lane, note, NoteColor(lane, note), bounds, laneWidth, y(Math.Min(lane.Ends[i], to)), y(Math.Max(lane.Starts[i], now)));
            }

            foreach (bool bars in (ReadOnlySpan<bool>)[true, false])
            {
                for (int i = last - 1; i >= first; i--)
                {
                    // A note is struck, and goes, as it reaches the line.
                    var note = lane.Notes[i];
                    if (lane.Starts[i] < now || (note.Lane == ChartNote.OpenLane) != bars)
                        continue;

                    DrawGem(context, lane, note, NoteColor(lane, note), bounds, laneWidth, y(lane.Starts[i]), gem);
                }
            }
        }

        private void DrawTail(DrawingContext context, HighwayLane lane, ChartNote note, Color color, Rect bounds, double laneWidth, double top, double bottom)
        {
            if (bottom <= top)
                return;

            double center = bounds.X + ((Column(lane, note.Lane) + 0.5) * laneWidth);
            double width = Math.Max(3, laneWidth * 0.2);
            var tailColor = lane.Kind == ChartInstrumentKind.SixFret && note.Lane >= 3 ? Color.FromRgb(0x8A, 0x8A, 0x96) : color;
            context.DrawRectangle(Brush(WithAlpha(tailColor, 0xCC)), OutlinePen, new Rect(center - (width / 2), top, width, bottom - top), 1, 1);
        }

        private void DrawGem(DrawingContext context, HighwayLane lane, ChartNote note, Color color, Rect bounds, double laneWidth, double at, double gem)
        {
            if (note.Lane == ChartNote.OpenLane)
            {
                // An open note or a kick: a flat bar across the highway.
                double barHeight = Math.Max(4, gem * 0.45);
                var bar = new Rect(bounds.X + 4, at - (barHeight / 2), bounds.Width - 8, barHeight);
                DrawFlat(context, bar, color);
                if ((note.Flags & NoteFlags.DoubleKick) != 0)
                    context.DrawRectangle(null, Pen(WhiteFretColor, 1), bar.Deflate(1.5), 1, 1);
                return;
            }

            var rect = GemRect(bounds, laneWidth, Column(lane, note.Lane), at, gem);

            // Six-fret: a black fret played with a white one covers its top half.
            if (lane.Kind == ChartInstrumentKind.SixFret && note.Lane >= 3 && lane.SharesColumn.Contains((note.Tick, note.Lane % 3)))
                rect = rect.WithHeight(rect.Height / 2);

            if ((note.Flags & NoteFlags.Cymbal) != 0)
            {
                DrawCymbal(context, rect, color);
                return;
            }

            if ((note.Flags & NoteFlags.Tap) != 0)
            {
                // A tap: dark, with the lane's colour as its border and a mark in the middle.
                double radius = CornerOf(rect);
                context.DrawRectangle(TapBodyBrush, Pen(color, 2), rect.Deflate(1), radius, radius);
                context.FillRectangle(Brush(color), Mark(rect), 1f);
                return;
            }

            DrawFlat(context, rect, color, lane.Kind == ChartInstrumentKind.SixFret && note.Lane >= 3 ? Color.FromRgb(0xC8, 0xC8, 0xD0) : null);

            // A hammer-on: a white mark across the face.
            if ((note.Flags & NoteFlags.Hopo) != 0)
                context.FillRectangle(HopoMarkBrush, Mark(rect), 1f);
        }

        /// <summary>
        /// A flat gem: its face in the colour over a darker lower edge, like a key seen from the front,
        /// with a thin dark outline so it reads over any background.
        /// </summary>
        private void DrawFlat(DrawingContext context, Rect rect, Color color, Color? outline = null)
        {
            double radius = CornerOf(rect);
            context.DrawRectangle(Brush(Darken(color, 0.38)), null, rect, radius, radius);
            context.DrawRectangle(Brush(color), null, rect.WithHeight(rect.Height * 0.72), radius, radius);
            context.DrawRectangle(null, outline is { } light ? Pen(light, 1) : OutlinePen, rect, radius, radius);
        }

        /// <summary>A cymbal: a flat hexagon, pointed at the sides, in two tones like a gem.</summary>
        private void DrawCymbal(DrawingContext context, Rect rect, Color color)
        {
            double point = Math.Min(rect.Height * 0.6, rect.Width * 0.25);
            var hexagon = new StreamGeometry();
            using (var g = hexagon.Open())
            {
                g.BeginFigure(new Point(rect.X, rect.Center.Y), true);
                g.LineTo(new Point(rect.X + point, rect.Y));
                g.LineTo(new Point(rect.Right - point, rect.Y));
                g.LineTo(new Point(rect.Right, rect.Center.Y));
                g.LineTo(new Point(rect.Right - point, rect.Bottom));
                g.LineTo(new Point(rect.X + point, rect.Bottom));
                g.EndFigure(true);
            }

            using (context.PushGeometryClip(hexagon))
            {
                context.FillRectangle(Brush(Darken(color, 0.38)), rect);
                context.FillRectangle(Brush(color), rect.WithHeight(rect.Height * 0.62));
            }

            context.DrawGeometry(null, OutlinePen, hexagon);
        }

        /// <summary>The mark across the face of a HOPO or tap gem.</summary>
        private static Rect Mark(Rect rect) =>
            new(rect.X + (rect.Width * 0.28), rect.Y + (rect.Height * 0.24), rect.Width * 0.44, Math.Max(1.5, rect.Height * 0.24));

        private static double CornerOf(Rect rect) => Math.Min(3, rect.Height * 0.25);

        private void DrawLabel(DrawingContext context, string label, Rect bounds, double top)
        {
            if (!_labels.TryGetValue(label, out var text))
            {
                text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), 10.5, LabelBrush)
                {
                    MaxTextWidth = Math.Max(1, bounds.Width - 4),
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis,
                };
                _labels[label] = text;
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
