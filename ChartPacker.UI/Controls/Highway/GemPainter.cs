using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ChartPacker.Core.Charts;

namespace ChartPacker.Controls
{
    /// <summary>
    /// Draws notes as the games show them: two-tone gems, HOPO and tap marks, cymbals, open-note and kick bars, and
    /// sustain tails. Each control that draws notes keeps one, for its brushes, pens and cymbal shapes.
    /// </summary>
    public sealed class GemPainter
    {
        private static readonly IPen OutlinePen = new ImmutablePen(Color.FromArgb(0xA0, 0x00, 0x00, 0x00).ToUInt32(), 1);
        private static readonly IBrush HopoMarkBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
        private static readonly IBrush TapBodyBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x16, 0x16, 0x1B));

        private readonly Dictionary<Color, IBrush> _brushes = [];
        private readonly Dictionary<(Color, double), IPen> _pens = [];

        // A cymbal's shape for each size of gem, at the origin, moved to each gem drawn.
        private readonly Dictionary<Size, StreamGeometry> _cymbals = [];

        /// <summary>
        /// Draws a gem in <paramref name="rect"/>: a cymbal, a tap, or a flat gem with a HOPO's mark, as its flags say.
        /// </summary>
        /// <param name="kind">How the part's notes are laid out, which says whether the note is a black fret.</param>
        /// <param name="outlined">Whether only its outline is drawn, so what's behind it shows through.</param>
        public void DrawGem(DrawingContext context, ChartInstrumentKind kind, ChartNote note, Color color, Rect rect, bool outlined)
        {
            bool black = HighwayPalette.IsBlackFret(kind, note.Lane);

            // Outlined, a black fret's own colour would be lost on the dark highway, so it's drawn in grey.
            var edge = outlined && black ? HighwayPalette.BlackFretEdge : color;

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

            DrawFlat(context, rect, edge, black ? HighwayPalette.BlackFretOutline : null, outlined);

            // A hammer-on: a white mark across the face.
            if ((note.Flags & NoteFlags.Hopo) != 0)
            {
                if (outlined)
                    context.DrawRectangle(null, Pen(HighwayPalette.WhiteFret, 1), Mark(rect).Deflate(0.5), 1, 1);
                else
                    context.FillRectangle(HopoMarkBrush, Mark(rect), 1f);
            }
        }

        /// <summary>Draws an open note or a kick: a flat bar across the lanes, with an inner edge for a 2× kick.</summary>
        public void DrawBar(DrawingContext context, ChartNote note, Color color, Rect bar, bool outlined)
        {
            DrawFlat(context, bar, color, outlined: outlined);
            if ((note.Flags & NoteFlags.DoubleKick) != 0)
                context.DrawRectangle(null, Pen(HighwayPalette.WhiteFret, 1), bar.Deflate(1.5), 1, 1);
        }

        /// <summary>Draws a sustain's tail filling <paramref name="tail"/>.</summary>
        public void DrawTail(DrawingContext context, ChartInstrumentKind kind, ChartNote note, Color color, Rect tail, bool outlined)
        {
            if (tail.Width <= 0 || tail.Height <= 0)
                return;
            var tailColor = HighwayPalette.IsBlackFret(kind, note.Lane) ? HighwayPalette.BlackFretEdge : color;
            if (outlined)
                context.DrawRectangle(null, Pen(tailColor, 1), tail.Deflate(0.5), 1, 1);
            else
                context.DrawRectangle(Brush(HighwayPalette.WithAlpha(tailColor, 0xCC)), OutlinePen, tail, 1, 1);
        }

        /// <summary>
        /// A flat gem: its face in the colour over a darker lower edge, like a key seen from the front, with a thin dark
        /// outline so it reads over any background. Outlined, only its edge is drawn, in the colour.
        /// </summary>
        public void DrawFlat(DrawingContext context, Rect rect, Color color, Color? outline = null, bool outlined = false)
        {
            double radius = CornerOf(rect);
            if (outlined)
            {
                context.DrawRectangle(null, Pen(color, 1.5), rect.Deflate(0.75), radius, radius);
                return;
            }

            context.DrawRectangle(Brush(HighwayPalette.Darken(color, 0.38)), null, rect, radius, radius);
            context.DrawRectangle(Brush(color), null, rect.WithHeight(rect.Height * 0.72), radius, radius);
            context.DrawRectangle(null, outline is { } light ? Pen(light, 1) : OutlinePen, rect, radius, radius);
        }

        /// <summary>A cymbal: a flat hexagon, pointed at the sides, in two tones like a gem; outlined, only its edge in the colour.</summary>
        public void DrawCymbal(DrawingContext context, Rect rect, Color color, bool outlined)
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
                    context.FillRectangle(Brush(HighwayPalette.Darken(color, 0.38)), face);
                    context.FillRectangle(Brush(color), face.WithHeight(face.Height * 0.62));
                }

                context.DrawGeometry(null, OutlinePen, hexagon);
            }
        }

        /// <summary>A brush of <paramref name="color"/>, made once.</summary>
        public IBrush Brush(Color color)
        {
            if (!_brushes.TryGetValue(color, out var brush))
                _brushes[color] = brush = new ImmutableSolidColorBrush(color);
            return brush;
        }

        /// <summary>A pen of <paramref name="color"/> and <paramref name="thickness"/>, made once.</summary>
        public IPen Pen(Color color, double thickness)
        {
            if (!_pens.TryGetValue((color, thickness), out var pen))
                _pens[(color, thickness)] = pen = new ImmutablePen(color.ToUInt32(), thickness);
            return pen;
        }

        /// <summary>The mark across the face of a HOPO or tap gem.</summary>
        public static Rect Mark(Rect rect) =>
            new(rect.X + (rect.Width * 0.28), rect.Y + (rect.Height * 0.24), rect.Width * 0.44, Math.Max(1.5, rect.Height * 0.24));

        /// <summary>How round a gem's corners are.</summary>
        public static double CornerOf(Rect rect) => Math.Min(3, rect.Height * 0.25);

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
    }
}
