using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using ChartShared.Core.Charts;

namespace ChartShared.UI.Controls.Highway
{
    /// <summary>
    /// Draws notes standing up on a tilted highway, as YARG and Clone Hero show them: each a low box with rounded corners, its
    /// top face lighter than its sides. A host gives it the rectangle a note occupies on the flat highway, as it gives the
    /// <see cref="GemPainter"/>, and the <see cref="Camera"/> says where that is on the screen and how large.
    /// </summary>
    /// <remarks>
    /// A note is no more than about fifty pixels across and twenty deep, so across it the perspective is a scale, and a gem
    /// is drawn as rounded rectangles stacked up from its footprint, which allocates nothing: a busy highway draws hundreds
    /// of them each frame. The tails are not drawn here; they lie flat on the plane, where the <see cref="GemPainter"/> draws them.
    /// </remarks>
    public sealed class UprightGemPainter : IGemPainter
    {
        // How tall a gem stands, as a share of its depth on the plane: about a quarter of its width, which is a little under
        // three times its depth. A bar across the lanes is lower, and stands as tall as it is deep.
        private const double GemRise = 0.72;
        private const double BarRise = 1.0;

        // How many rounded rectangles make up a box's sides, from its base to its top.
        private const int SideLayers = 4;

        // How far a box's sides are darkened at its base and at its top, and how far its top is lightened.
        private const double BaseDarken = 0.62;
        private const double TopDarken = 0.22;
        private const double TopLighten = 0.18;

        // A bar's width is more than this many times its depth; nothing else is that wide.
        private const double BarAspect = 6;

        private static readonly IBrush ShadowBrush = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromArgb(0x70, 0x00, 0x00, 0x00));

        private readonly GemPainter _flat;
        private readonly Dictionary<(int, int), StreamGeometry> _hexagons = [];

        // What was drawn last, in the order it was, from the back: where each note stands on the screen, and where on the plane.
        private readonly List<(Rect Silhouette, Point Floor)> _scene = [];

        /// <param name="flat">The flat painter, whose brushes and pens are shared and which draws the tails.</param>
        public UprightGemPainter(GemPainter flat)
        {
            _flat = flat;
        }

        /// <summary>The camera the notes are placed by. With none, the notes are drawn flat.</summary>
        public PerspectiveCamera? Camera { get; set; }

        /// <summary>Forgets the notes drawn so far, as a host does before it draws its notes again.</summary>
        public void BeginScene() => _scene.Clear();

        /// <summary>
        /// The centre of the floor, on the plane, of the note standing at <paramref name="host"/> on the screen, as of the last drawing:
        /// the nearest one when they overlap, as the eye sees it. A note's box rises above its floor, so the pointer over its top is
        /// not over its floor. Null when there is no note there. Only the notes drawn whole are held, not outlines or marks.
        /// </summary>
        public Point? PlaneAt(Point host)
        {
            for (int i = _scene.Count - 1; i >= 0; i--)
            {
                if (_scene[i].Silhouette.Contains(host))
                    return _scene[i].Floor;
            }

            return null;
        }

        /// <summary>Where a note stands: its footprint, as it is on the screen, and how tall it is and how round its corners.</summary>
        private readonly struct Stand
        {
            public Stand(Rect floor, double rise, double radiusX, double radiusY)
            {
                Floor = floor;
                Rise = rise;
                RadiusX = radiusX;
                RadiusY = radiusY;
            }

            public Rect Floor { get; }

            public double Rise { get; }

            public double RadiusX { get; }

            public double RadiusY { get; }

            public Rect Top => Floor.Translate(new Vector(0, -Rise));

            /// <summary>Everything it covers: from its top face to the near edge of its floor.</summary>
            public Rect Silhouette => new(Floor.X, Floor.Y - Rise, Floor.Width, Floor.Height + Rise);
        }

        /// <inheritdoc />
        public void DrawGem(DrawingContext context, ChartInstrumentKind kind, ChartNote note, Color color, Rect rect, bool outlined)
        {
            if (Camera is null)
            {
                _flat.DrawGem(context, kind, note, color, rect, outlined);
                return;
            }

            var stand = StandOf(rect, GemRise);
            if (!IsShown(stand))
                return;
            if (!outlined)
                _scene.Add((stand.Silhouette, rect.Center));

            // A ghost: faded, with a dashed edge in its colour.
            if (!outlined && (note.Flags & NoteFlags.Ghost) != 0)
            {
                using (context.PushOpacity(GemPainter.GhostOpacity))
                    DrawBody(context, kind, note, color, stand, outlined);
                if ((note.Flags & NoteFlags.Cymbal) == 0)
                    DashedOutline.DrawRectangle(context, _flat.Pen(color, 1), stand.Top.Deflate(0.5), stand.RadiusX, GemPainter.GhostDash, GemPainter.GhostGap);
            }
            else
            {
                DrawBody(context, kind, note, color, stand, outlined);
            }

            // An accent: a bright ring around it.
            if ((note.Flags & NoteFlags.Accent) != 0)
                DrawEdge(context, stand.Silhouette.Inflate(2), stand, _flat.Pen(HighwayPalette.WhiteFret, 1.5));
        }

        /// <inheritdoc />
        public void DrawBar(DrawingContext context, ChartInstrumentKind kind, ChartNote note, Color color, Rect bar, bool outlined)
        {
            if (Camera is null)
            {
                _flat.DrawBar(context, kind, note, color, bar, outlined);
                return;
            }

            var stand = StandOf(bar, BarRise);
            if (!IsShown(stand))
                return;
            if (!outlined)
                _scene.Add((stand.Silhouette, bar.Center));

            var top = stand.Top;
            bool hopo = (note.Flags & NoteFlags.Hopo) != 0;
            if (outlined)
            {
                var edge = hopo ? HighwayPalette.Lighten(color, GemPainter.HopoOpenLighten) : color;
                DrawOutlinedBox(context, stand, _flat.Pen(edge, 1.5));
            }
            else if (!hopo)
            {
                DrawSides(context, stand, color);
                DrawTop(context, stand, HighwayPalette.Lighten(color, TopLighten), color, null);
            }
            else
            {
                // The lane's own colour, before star power, so a star-powered HOPO keeps its hue.
                var edge = kind == ChartInstrumentKind.Drums ? HighwayPalette.Kick : HighwayPalette.Open;
                var lightened = HighwayPalette.Lighten(color, GemPainter.HopoOpenLighten);
                DrawSides(context, stand, lightened);
                DrawTop(context, stand, lightened, lightened, null);
                context.DrawRectangle(null, _flat.Pen(edge, 2), top, stand.RadiusX, stand.RadiusY);
            }

            if ((note.Flags & NoteFlags.DoubleKick) != 0)
                context.DrawRectangle(null, _flat.Pen(HighwayPalette.WhiteFret, 1), top.Deflate(1.5), 1, 1);
        }

        /// <summary>A tail lies flat on the plane, so it is drawn as it is on a flat highway.</summary>
        public void DrawTail(DrawingContext context, ChartInstrumentKind kind, ChartNote note, Color color, Rect tail, bool outlined) =>
            _flat.DrawTail(context, kind, note, color, tail, outlined);

        /// <summary>A tail lies flat on the plane, so it is drawn as it is on a flat highway.</summary>
        public void DrawWideTail(DrawingContext context, Color color, Rect tail, bool outlined) =>
            _flat.DrawWideTail(context, color, tail, outlined);

        /// <inheritdoc />
        public void DrawRing(DrawingContext context, ChartNote note, Rect rect, Color color)
        {
            if (Camera is null)
            {
                _flat.DrawRing(context, note, rect, color);
                return;
            }

            var stand = StandOf(rect, RiseOf(rect));
            if (!IsShown(stand))
                return;

            var ring = stand.Silhouette.Inflate(3.5);
            DrawEdge(context, ring, stand, GemPainter.RingShadowPen);
            DrawEdge(context, ring, stand, _flat.Pen(color, 2));
        }

        /// <summary>Draws a ring in <paramref name="pen"/> round the note that stands in <paramref name="rect"/>, as the editor marks one it has selected.</summary>
        public void DrawSelection(DrawingContext context, Rect rect, IPen pen)
        {
            if (Camera is null)
            {
                context.DrawRectangle(null, pen, rect.Inflate(2), 3, 3);
                return;
            }

            var stand = StandOf(rect, RiseOf(rect));
            if (IsShown(stand))
                DrawEdge(context, stand.Silhouette.Inflate(2), stand, pen);
        }

        /// <summary>
        /// Marks over the note that stands in <paramref name="rect"/>, in <paramref name="brush"/> and <paramref name="pen"/>: what the
        /// erase tool would take away, in red, or the notes a synchronized region syncs, in orange. It covers all of the note, box and floor.
        /// </summary>
        public void DrawMark(DrawingContext context, Rect rect, IBrush? brush, IPen? pen)
        {
            if (Camera is null)
            {
                context.DrawRectangle(brush, pen, rect.Inflate(1), 3, 3);
                return;
            }

            var stand = StandOf(rect, RiseOf(rect));
            if (IsShown(stand))
                context.DrawRectangle(brush, pen, stand.Silhouette.Inflate(1), stand.RadiusX, stand.RadiusY);
        }

        /// <summary>
        /// Where on the screen the note drawn in <paramref name="rect"/> is, from the top of its box to the near edge of its floor,
        /// or null with no camera or when it is off the screen.
        /// </summary>
        public Rect? SilhouetteOf(Rect rect)
        {
            if (Camera is null)
                return null;
            var stand = StandOf(rect, RiseOf(rect));
            return IsShown(stand) ? stand.Silhouette : null;
        }

        private static double RiseOf(Rect rect) => rect.Width > rect.Height * BarAspect ? BarRise : GemRise;

        private Stand StandOf(Rect plane, double riseShare)
        {
            var camera = Camera!;
            var middle = camera.Project(plane.Center);
            double scale = camera.ScaleAt(plane.Center.Y);
            double far = camera.Project(new Point(plane.Center.X, plane.Top)).Y;
            double near = camera.Project(new Point(plane.Center.X, plane.Bottom)).Y;
            double width = plane.Width * scale;
            var floor = new Rect(middle.X - (width / 2), far, width, Math.Max(0.5, near - far));

            // Round as the flat gem is, in plane pixels, so it is as round as it is wide and as flat as it is deep.
            double corner = GemPainter.CornerOf(plane);
            double depth = floor.Height / Math.Max(plane.Height, 1e-6);
            return new Stand(floor, riseShare * plane.Height * scale, corner * scale, corner * depth);
        }

        private bool IsShown(Stand stand)
        {
            var camera = Camera!;
            var seen = stand.Silhouette;
            return seen.Right > 0 && seen.X < camera.HostWidth && seen.Bottom > 0 && seen.Y < camera.HostHeight;
        }

        /// <summary>Draws the gem as its flags say: a cymbal, a tap, or a box with a HOPO's mark.</summary>
        private void DrawBody(DrawingContext context, ChartInstrumentKind kind, ChartNote note, Color color, Stand stand, bool outlined)
        {
            bool black = HighwayPalette.IsBlackFret(kind, note.Lane);

            // Outlined, a black fret's own colour would be lost on the dark highway, so it's drawn in grey.
            var edge = outlined && black ? HighwayPalette.BlackFretEdge : color;
            var top = stand.Top;

            if ((note.Flags & NoteFlags.Cymbal) != 0)
            {
                DrawCymbal(context, stand, color, edge, outlined);
                return;
            }

            if ((note.Flags & NoteFlags.Tap) != 0)
            {
                // A tap: dark, with the lane's colour as its border and a mark in the middle.
                if (outlined)
                {
                    DrawOutlinedBox(context, stand, _flat.Pen(edge, 1.5));
                    context.DrawRectangle(null, _flat.Pen(edge, 1), GemPainter.Mark(top).Deflate(0.5), 1, 1);
                }
                else
                {
                    DrawSides(context, stand, color);
                    context.DrawRectangle(GemPainter.TapBodyBrush, _flat.Pen(edge, 2), top.Deflate(1), stand.RadiusX, stand.RadiusY);
                    context.FillRectangle(_flat.Brush(color), GemPainter.Mark(top), 1f);
                }

                return;
            }

            if (outlined)
            {
                DrawOutlinedBox(context, stand, _flat.Pen(edge, 1.5));
            }
            else
            {
                DrawSides(context, stand, edge);
                DrawTop(context, stand, HighwayPalette.Lighten(edge, TopLighten), edge, black ? HighwayPalette.BlackFretOutline : null);
            }

            // A hammer-on: a white mark across the face.
            if ((note.Flags & NoteFlags.Hopo) != 0)
            {
                if (outlined)
                    context.DrawRectangle(null, _flat.Pen(HighwayPalette.WhiteFret, 1), GemPainter.Mark(top).Deflate(0.5), 1, 1);
                else
                    context.FillRectangle(GemPainter.HopoMarkBrush, GemPainter.Mark(top), 1f);
            }
        }

        /// <summary>
        /// Only the box's edges, so what's behind shows through: round all it covers, from its top to the near edge of its floor, and
        /// round its top face, which together show it is a box and where it stands.
        /// </summary>
        private static void DrawOutlinedBox(DrawingContext context, Stand stand, IPen pen)
        {
            context.DrawRectangle(null, pen, stand.Silhouette.Deflate(0.75), stand.RadiusX, stand.RadiusY);
            context.DrawRectangle(null, pen, stand.Top.Deflate(0.75), stand.RadiusX, stand.RadiusY);
        }

        /// <summary>The box's shadow on the plane, then its sides: its floor's rounded rectangle stacked up to its top, darker at the base.</summary>
        private void DrawSides(DrawingContext context, Stand stand, Color color)
        {
            context.DrawRectangle(ShadowBrush, null, stand.Floor.Inflate(1), stand.RadiusX, stand.RadiusY);

            var dark = HighwayPalette.Darken(color, BaseDarken);
            var light = HighwayPalette.Darken(color, TopDarken);
            for (int i = 0; i < SideLayers; i++)
            {
                var layer = stand.Floor.Translate(new Vector(0, -stand.Rise * i / SideLayers));
                var brush = _flat.Brush(Blend(dark, light, i / (double)(SideLayers - 1)));
                context.DrawRectangle(brush, i == 0 ? GemPainter.OutlinePen : null, layer, stand.RadiusX, stand.RadiusY);
            }
        }

        /// <summary>The box's top face, filled in <paramref name="fill"/>, edged in <paramref name="edge"/> darkened, or in <paramref name="outline"/> when there is one.</summary>
        private void DrawTop(DrawingContext context, Stand stand, Color fill, Color edge, Color? outline) =>
            context.DrawRectangle(_flat.Brush(fill), outline is { } light ? _flat.Pen(light, 1) : _flat.Pen(HighwayPalette.Darken(edge, 0.45), 1), stand.Top, stand.RadiusX, stand.RadiusY);

        private void DrawCymbal(DrawingContext context, Stand stand, Color color, Color edge, bool outlined)
        {
            var hexagon = Hexagon(stand.Floor.Size);
            if (outlined)
            {
                using (context.PushTransform(Matrix.CreateTranslation(stand.Top.X, stand.Top.Y)))
                    context.DrawGeometry(null, _flat.Pen(edge, 1.5), hexagon);
                return;
            }

            var dark = HighwayPalette.Darken(color, BaseDarken);
            var light = HighwayPalette.Darken(color, TopDarken);
            using (context.PushTransform(Matrix.CreateTranslation(stand.Floor.X, stand.Floor.Y)))
                context.DrawGeometry(ShadowBrush, null, hexagon);
            for (int i = 0; i < SideLayers; i++)
            {
                var layer = stand.Floor.Translate(new Vector(0, -stand.Rise * i / SideLayers));
                using (context.PushTransform(Matrix.CreateTranslation(layer.X, layer.Y)))
                    context.DrawGeometry(_flat.Brush(Blend(dark, light, i / (double)(SideLayers - 1))), i == 0 ? GemPainter.OutlinePen : null, hexagon);
            }

            using (context.PushTransform(Matrix.CreateTranslation(stand.Top.X, stand.Top.Y)))
                context.DrawGeometry(_flat.Brush(HighwayPalette.Lighten(color, TopLighten)), _flat.Pen(HighwayPalette.Darken(color, 0.45), 1), hexagon);
        }

        /// <summary>The edge of a note's shape in <paramref name="rect"/>: a rounded rectangle, as round as the note's box is.</summary>
        private static void DrawEdge(DrawingContext context, Rect rect, Stand stand, IPen pen) =>
            context.DrawRectangle(null, pen, rect, stand.RadiusX, stand.RadiusY);

        /// <summary>A cymbal's hexagon filling <paramref name="size"/> from the origin, made once for each size to the half pixel.</summary>
        private StreamGeometry Hexagon(Size size)
        {
            var key = ((int)Math.Round(size.Width * 2), (int)Math.Round(size.Height * 2));
            if (_hexagons.TryGetValue(key, out var hexagon))
                return hexagon;

            // Sizes change with every depth a cymbal is at: those no longer drawn go.
            if (_hexagons.Count > 128)
                _hexagons.Clear();

            double width = key.Item1 / 2.0;
            double height = key.Item2 / 2.0;
            double point = Math.Min(height * 0.6, width * 0.25);
            hexagon = new StreamGeometry();
            using (var g = hexagon.Open())
            {
                g.BeginFigure(new Point(0, height / 2), true);
                g.LineTo(new Point(point, 0));
                g.LineTo(new Point(width - point, 0));
                g.LineTo(new Point(width, height / 2));
                g.LineTo(new Point(width - point, height));
                g.LineTo(new Point(point, height));
                g.EndFigure(true);
            }

            _hexagons[key] = hexagon;
            return hexagon;
        }

        private static Color Blend(Color from, Color to, double amount) => Color.FromArgb(
            from.A,
            (byte)(from.R + ((to.R - from.R) * amount)),
            (byte)(from.G + ((to.G - from.G) * amount)),
            (byte)(from.B + ((to.B - from.B) * amount)));
    }
}
