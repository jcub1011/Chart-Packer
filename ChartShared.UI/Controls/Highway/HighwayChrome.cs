using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ChartShared.Core.Charting.Editing;

namespace ChartShared.UI.Controls.Highway
{
    /// <summary>A phrase to tint behind a lane's notes (see <see cref="HighwayChrome.DrawBands"/>), placed along the time axis.</summary>
    public struct BandGlyph
    {
        /// <summary>Where the band starts and ends along the time axis, in pixels, in either order. The edge lines are drawn there.</summary>
        public double Start;

        public double End;

        /// <summary>The band's colour, which its tint and its edges are made of.</summary>
        public Color Color;

        /// <summary>How opaque the tint is, out of 255.</summary>
        public byte TintAlpha;

        /// <summary>Whether the band has an end, to mark by a line; one still going on has only its start.</summary>
        public bool HasEnd;

        /// <summary>Whether the band is outlined as the one picked.</summary>
        public bool IsSelected;
    }

    /// <summary>
    /// What a highway draws around its notes, the same on Chart Packer's highway and in Chart Builder's editor, in either
    /// orientation: the lane dividers, the beat and measure lines, the strike band and its targets, and a struck column's
    /// beam. Where a drawing is across the lanes, <c>along</c> is a position along the time axis and <c>across</c> one across
    /// the lanes, which <see cref="At"/> turns into a point: a highway's time axis is vertical, a timeline's horizontal.
    /// </summary>
    public static class HighwayChrome
    {
        /// <summary>The line between two lanes.</summary>
        public static readonly IPen LaneDividerPen = new ImmutablePen(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF).ToUInt32(), 1);

        /// <summary>A beat's line across the lanes.</summary>
        public static readonly IPen BeatPen = new ImmutablePen(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF).ToUInt32(), 1);

        /// <summary>A measure's line across the lanes, stronger than a beat's.</summary>
        public static readonly IPen MeasurePen = new ImmutablePen(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF).ToUInt32(), 2);

        /// <summary>The edges of the strike band.</summary>
        public static readonly IPen StrikeEdgePen = new ImmutablePen(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF).ToUInt32(), 1);

        /// <summary>The strike band's dark, as the games have it.</summary>
        public static readonly IBrush StrikeBandBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xC0, 0x00, 0x00, 0x00));

        /// <summary>How opaque a band's edge lines are, out of 255.</summary>
        public const byte BandEdgeAlpha = 0xA0;

        /// <summary>How opaque a band's tint is, out of 255, unless a host asks for fainter.</summary>
        public const byte BandTintAlpha = 0x22;

        /// <summary>The outline of a selected band.</summary>
        public static readonly IPen BandSelectedPen = new ImmutablePen(Colors.White.ToUInt32(), 2);

        /// <summary>The point <paramref name="along"/> the time axis and <paramref name="across"/> the lanes, in <paramref name="view"/>.</summary>
        public static Point At(LaneView view, double along, double across) =>
            view == LaneView.Highway ? new Point(across, along) : new Point(along, across);

        /// <summary>
        /// The rectangle between two positions along the time axis and two across the lanes, in <paramref name="view"/>, cut to
        /// <paramref name="alongFrom"/> and <paramref name="alongTo"/> along the time axis, so something reaching far past what's
        /// drawn doesn't make a picture bigger than it.
        /// </summary>
        public static Rect Box(LaneView view, double along1, double along2, double across1, double across2, double alongFrom = double.NegativeInfinity, double alongTo = double.PositiveInfinity)
        {
            double first = Math.Clamp(Math.Min(along1, along2), alongFrom, alongTo);
            double last = Math.Clamp(Math.Max(along1, along2), alongFrom, alongTo);
            return new Rect(At(view, first, Math.Min(across1, across2)), At(view, last, Math.Max(across1, across2)));
        }

        /// <summary>
        /// A straight line in <paramref name="pen"/> from <paramref name="from"/> to <paramref name="to"/>; with <paramref name="asRect"/>, one
        /// along either axis, in a plain pen, is filled as the rectangle it covers instead. For a highway tilted back in perspective: the
        /// graphics card draws a line through the tilt as a path it works out again each frame, and a rectangle as a simple shape, and a
        /// lane's beat, grid and divider lines are hundreds a frame. Flat, a line is drawn as a line.
        /// </summary>
        public static void DrawStraight(DrawingContext context, IPen pen, Point from, Point to, bool asRect)
        {
            if (asRect && pen.DashStyle is null && pen.Brush is { } brush && (from.X == to.X || from.Y == to.Y))
            {
                double thickness = pen.Thickness, half = thickness / 2;
                context.FillRectangle(brush, from.X == to.X
                    ? new Rect(from.X - half, Math.Min(from.Y, to.Y), thickness, Math.Abs(to.Y - from.Y))
                    : new Rect(Math.Min(from.X, to.X), from.Y - half, Math.Abs(to.X - from.X), thickness));
                return;
            }

            context.DrawLine(pen, from, to);
        }

        /// <summary>A beat's line, or a measure's, at <paramref name="along"/> across the lanes from <paramref name="across1"/> to <paramref name="across2"/>.</summary>
        /// <param name="asRect">Whether it's filled as a rectangle, for a highway tilted back (see <see cref="DrawStraight"/>).</param>
        public static void DrawBeatLine(DrawingContext context, LaneView view, double along, double across1, double across2, bool isMeasure, bool asRect = false) =>
            DrawStraight(context, isMeasure ? MeasurePen : BeatPen, At(view, along, across1), At(view, along, across2), asRect);

        /// <summary>
        /// The lines between <paramref name="projection"/>'s lanes, from <paramref name="alongFrom"/> to <paramref name="alongTo"/>
        /// along the time axis.
        /// </summary>
        /// <param name="alternateRows">A brush to shade every other lane with, for rows of a timeline that are hard to tell apart; null for none.</param>
        /// <param name="halves">A pen for the line down the middle of a lane two share (GHL's white and black frets); null for none.</param>
        /// <param name="asRect">Whether the lines are filled as rectangles, for a highway tilted back (see <see cref="DrawStraight"/>).</param>
        public static void DrawLaneDividers(DrawingContext context, LaneProjection projection, double alongFrom, double alongTo, IBrush? alternateRows = null, IPen? halves = null, bool asRect = false)
        {
            var view = projection.View;
            for (int i = 0; i < projection.Slots.Slots.Count; i++)
            {
                double edge = projection.LaneFrom + (i * projection.LaneSize);
                if (alternateRows is not null && i % 2 == 1)
                    context.FillRectangle(alternateRows, Box(view, alongFrom, alongTo, edge, edge + projection.LaneSize));
                if (i > 0)
                    DrawStraight(context, LaneDividerPen, At(view, alongFrom, edge), At(view, alongTo, edge), asRect);

                if (halves is not null && projection.Slots.SharedOf(i) is not null)
                    DrawStraight(context, halves, At(view, alongFrom, edge + (projection.LaneSize / 2)), At(view, alongTo, edge + (projection.LaneSize / 2)), asRect);
            }
        }

        /// <summary>The strike line's band across the lanes, filled with <paramref name="brush"/>, with a line along each of its edges across them.</summary>
        /// <param name="asRect">Whether the edges are filled as rectangles, for a highway tilted back (see <see cref="DrawStraight"/>).</param>
        public static void DrawStrikeBand(DrawingContext context, LaneView view, Rect band, IBrush brush, bool asRect = false)
        {
            context.FillRectangle(brush, band);
            if (view == LaneView.Highway)
            {
                DrawStraight(context, StrikeEdgePen, band.TopLeft, band.TopRight, asRect);
                DrawStraight(context, StrikeEdgePen, band.BottomLeft, band.BottomRight, asRect);
            }
            else
            {
                DrawStraight(context, StrikeEdgePen, band.TopLeft, band.BottomLeft, asRect);
                DrawStraight(context, StrikeEdgePen, band.TopRight, band.BottomRight, asRect);
            }
        }

        /// <summary>
        /// Draws <paramref name="bands"/> across the lanes from <paramref name="across1"/> to <paramref name="across2"/>, behind
        /// the notes: a faint tint of each, then a stronger line along its start and, where it has one, its end, over every
        /// tint so none is hidden where bands overlap, then an outline round the selected.
        /// </summary>
        /// <param name="alongFrom">The nearest along the time axis anything is drawn, in pixels; see <see cref="Box"/>.</param>
        /// <param name="asRect">Whether the edge lines are filled as rectangles, for a highway tilted back (see <see cref="DrawStraight"/>).</param>
        public static void DrawBands(DrawingContext context, GemPainter gems, LaneView view, ReadOnlySpan<BandGlyph> bands, double across1, double across2, double alongFrom = double.NegativeInfinity, double alongTo = double.PositiveInfinity, bool asRect = false)
        {
            foreach (var band in bands)
                context.FillRectangle(gems.Brush(HighwayPalette.WithAlpha(band.Color, band.TintAlpha)), Box(view, band.Start, band.End, across1, across2, alongFrom, alongTo));

            foreach (var band in bands)
            {
                var pen = gems.Pen(HighwayPalette.WithAlpha(band.Color, BandEdgeAlpha), 1);
                double start = Math.Clamp(band.Start, alongFrom, alongTo);
                DrawStraight(context, pen, At(view, start, across1), At(view, start, across2), asRect);
                if (band.HasEnd)
                {
                    double end = Math.Clamp(band.End, alongFrom, alongTo);
                    DrawStraight(context, pen, At(view, end, across1), At(view, end, across2), asRect);
                }
            }

            foreach (var band in bands)
            {
                if (band.IsSelected)
                    context.DrawRectangle(null, BandSelectedPen, Box(view, band.Start, band.End, across1, across2, alongFrom, alongTo).Deflate(1));
            }
        }

        /// <summary>A lane's target on the strike line, dark until it's struck: its outline in <paramref name="color"/>.</summary>
        public static void DrawTarget(DrawingContext context, GemPainter gems, Rect rect, Color color)
        {
            double radius = GemPainter.CornerOf(rect);
            context.DrawRectangle(gems.Brush(HighwayPalette.WithAlpha(HighwayPalette.Darken(color, 0.72), 0xC8)), gems.Pen(color, 2), rect.Deflate(1), radius, radius);
        }

        /// <summary>A lane's target filled in <paramref name="color"/> as brightly as <paramref name="lit"/> says, from 0 to 1.</summary>
        public static void DrawLitTarget(DrawingContext context, GemPainter gems, Rect rect, Color color, double lit)
        {
            if (lit <= 0)
                return;

            double radius = GemPainter.CornerOf(rect);
            using (context.PushOpacity(lit))
                context.DrawRectangle(gems.Brush(StrikeGlow.TargetFill(color)), gems.Pen(StrikeGlow.TargetEdge(color), 2), rect.Deflate(1), radius, radius);
        }

        /// <summary>A struck lane's beam over <paramref name="rect"/>, as brightly as <paramref name="lit"/> says, from 0 to 1.</summary>
        public static void DrawLitBeam(DrawingContext context, LaneView view, Color color, double lit, Rect rect)
        {
            if (lit <= 0)
                return;

            using (context.PushOpacity(lit * StrikeGlow.BeamOpacity))
                StrikeGlow.DrawBeam(context, color, view == LaneView.Highway, rect);
        }
    }
}
