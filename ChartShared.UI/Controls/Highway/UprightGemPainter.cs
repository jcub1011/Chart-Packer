using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
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
    /// is drawn as a few rounded rectangles over its footprint (its floor, its sides swept up from it, its top), which allocates
    /// nothing: a busy highway draws hundreds of them each frame. The tails are not drawn here; they lie flat on the plane, where
    /// the <see cref="GemPainter"/> draws them.
    /// </remarks>
    public sealed class UprightGemPainter : IGemPainter
    {
        // How tall a gem stands, as a share of its depth on the plane: about a quarter of its width, which is a little under
        // three times its depth. A bar across the lanes is lower, and stands as tall as it is deep.
        private const double GemRise = 0.72;
        private const double BarRise = 1.0;

        // How many steps a box's sides are shaded in, from its base to its top.
        private const int SideLayers = 4;

        // How far a box's sides are darkened at its base and at its top, and how far its top is lightened.
        private const double BaseDarken = 0.62;
        private const double TopDarken = 0.22;
        private const double TopLighten = 0.18;

        // A bar's width is more than this many times its depth; nothing else is that wide.
        private const double BarAspect = 6;

        private static readonly IBrush ShadowBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x70, 0x00, 0x00, 0x00));

        // How many outlined cymbals' hexagons of a size are kept before they are all let go: sizes change with every depth a note is
        // at, and a shape is cheap to make again, so the few in view are made again the next frame.
        private const int MaxShapes = 512;

        // A note is drawn in plain fills alone, a few rounded rectangles over one another, which the graphics card draws as simple shapes
        // and batches: its outline and edges are each a shape a little larger behind it, not a stroke. A stroke round a rounded rectangle
        // as small as a far note's, through the tilt, is a path it works out again each frame, and a gradient breaks the batch; drawn
        // so, a busy highway's notes took 10 ms a frame. (Nor is a note a picture, drawn stretched: as cheap to draw, but each drawn
        // picture is an object with a finalizer, made each frame for each note, for the collector to clear.)
        private static readonly IBrush OutlineBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xA0, 0x00, 0x00, 0x00));

        // The least a corner is rounded by, in pixels, to be drawn round; less, and it's drawn square, which is cheaper and looks the same.
        private const double MinCorner = 0.5;

        // A cymbal's hexagon, filled, is one of a few shapes a unit across and down, stretched to the cymbal: how far in its points
        // are, and how far down it's slid, in steps of these, so the same few shapes serve every cymbal at every depth.
        private const int PointSteps = 64;
        private const int SweepSteps = 32;
        private const int MaxUnitHexagons = 1024;
        private readonly LruCache<(int Point, int Sweep), StreamGeometry> _unitHexagons = new(MaxUnitHexagons);

        // The same for a band of a cymbal's sides between two of its steps: the hexagon's lower edge, swept down by the band's height.
        private readonly LruCache<(int Point, int Thickness), StreamGeometry> _unitBands = new(MaxUnitHexagons);

        // A box's sides are shaded in steps from dark at the floor to light under the top. Each step's colour would be a brush of its own
        // for each note colour, and Avalonia lists each distinct brush a drawing uses, afresh for each frame's drawing; so the sides
        // are filled light, and the lower steps darkened by these, the same few for every colour: black, as faint as takes a step from
        // the one above it to its own shade, laid over it and every step below. Darken is a multiply, so black over a colour at an
        // opacity is that colour darkened by as much, exactly.
        private static readonly IBrush FloorShade = Shade(1 - ((1 - BaseDarken) / (1 - TopDarken)));
        private static readonly IBrush[] StepShades = MakeStepShades();

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
        /// How far past the plane's near end, which is the host's bottom edge, in pixels of the plane, a note's floor can have its middle
        /// and still have some of it on the screen: its box stands up from the floor, so it is seen well after the floor has gone. A note
        /// is culled by when it is, so one drawn only while its middle is on the plane would vanish with most of it still showing.
        /// </summary>
        /// <param name="depth">How deep a note is on the plane, as <see cref="GemDepth"/> gives it.</param>
        /// <returns>0 with no camera.</returns>
        public double ReachBelow(double depth)
        {
            if (Camera is not { } camera)
                return 0;

            // The tallest box, at the plane's near end, where the scale is 1: it is gone when its top has fallen to the host's bottom.
            double rise = Math.Max(GemRise, BarRise) * depth;
            return Camera.Unproject(new Point(camera.HostWidth / 2, camera.HostHeight + rise)) is { } far
                ? Math.Max(0, far.Y + (depth / 2) - camera.PlaneHeight)
                : 0;
        }

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
                    DashedOutline.DrawRectangleNear(context, _flat.Pen(color, 1), stand.Top.Deflate(0.5), stand.RadiusX, GemPainter.GhostDash, GemPainter.GhostGap);
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
                FillEdged(context, _flat.Brush(lightened), _flat.Brush(edge), 2, top, stand.RadiusX, stand.RadiusY);
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

        /// <summary>An X across the face of the note standing in <paramref name="rect"/>: a playtest's missed note.</summary>
        public void DrawCross(DrawingContext context, Rect rect, IPen pen)
        {
            if (FaceOf(rect) is not { } face)
                return;

            context.DrawLine(pen, face.TopLeft, face.BottomRight);
            context.DrawLine(pen, face.TopRight, face.BottomLeft);
        }

        /// <summary>A slash across the face of the note standing in <paramref name="rect"/>: a playtest's dropped sustain.</summary>
        public void DrawSlash(DrawingContext context, Rect rect, IPen pen)
        {
            if (FaceOf(rect) is { } face)
                context.DrawLine(pen, face.BottomLeft, face.TopRight);
        }

        /// <summary>The square, on the screen, a mark is drawn in across the note standing in <paramref name="rect"/>: as wide as its box is tall, for it is seen from the front and above.</summary>
        private Rect? FaceOf(Rect rect)
        {
            if (Camera is null)
                return null;

            var stand = StandOf(rect, RiseOf(rect));
            if (!IsShown(stand))
                return null;

            var seen = stand.Silhouette;
            double side = Math.Min(seen.Width, seen.Height);
            return new Rect(seen.Center.X - (side / 2), seen.Center.Y - (side / 2), side, side).Deflate(1);
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
                    FillEdged(context, GemPainter.TapBodyBrush, _flat.Brush(edge), 2, top.Deflate(1), stand.RadiusX, stand.RadiusY);
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

        /// <summary>The box's top face, filled in <paramref name="fill"/>, edged in <paramref name="edge"/> darkened, or in <paramref name="outline"/> when there is one.</summary>
        private void DrawTop(DrawingContext context, Stand stand, Color fill, Color edge, Color? outline)
        {
            var rim = outline ?? HighwayPalette.Darken(edge, 0.45);
            FillEdged(context, _flat.Brush(fill), _flat.Brush(rim), 1, stand.Top, stand.RadiusX, stand.RadiusY);
        }

        /// <summary>
        /// The box's shadow on the plane, then its sides: its floor, dark and edged, and over it the sides in
        /// <see cref="SideLayers"/> steps from dark to light, each the floor's rounded rectangle slid up from where its step starts to the
        /// top, which sweeps out a rounded rectangle as round, only taller. However shallow the floor, the steps leave no gap.
        /// </summary>
        private void DrawSides(DrawingContext context, Stand stand, Color color)
        {
            var floor = stand.Floor;
            double radiusX = stand.RadiusX, radiusY = stand.RadiusY;
            FillRounded(context, ShadowBrush, floor.Inflate(1), radiusX + 1, radiusY + 1);

            // The floor, edged, light, then darkened to the darkest step's shade.
            var light = _flat.Brush(HighwayPalette.Darken(color, TopDarken));
            FillEdged(context, light, OutlineBrush, 1, floor, radiusX, radiusY);
            FillRounded(context, FloorShade, Shrunk(floor, 0.5), Math.Max(0, radiusX - 0.5), Math.Max(0, radiusY - 0.5));

            // The sides above it, all light, from the top down to where the floor's step begins; then each step from the one under the
            // top downwards darkened, with all those below it, by as much as it is darker than the step above.
            double top = floor.Y - stand.Rise;
            double lowest = StepBottom(stand, 1);
            FillRounded(context, light, new Rect(floor.X, top, floor.Width, Math.Max(0, lowest - top)), radiusX, radiusY);
            for (int step = SideLayers - 2; step >= 1; step--)
            {
                double from = StepBottom(stand, step + 1);
                FillRounded(context, StepShades[step], new Rect(floor.X, from, floor.Width, Math.Max(0, lowest - from)), radiusX, radiusY);
            }
        }

        /// <summary>Where the <paramref name="step"/>th of a box's steps, counted up from its floor, ends at the bottom: the floor's bottom, raised by as many steps.</summary>
        private static double StepBottom(Stand stand, int step) => stand.Floor.Bottom - (stand.Rise * step / SideLayers);

        /// <summary>Black, as opaque as <paramref name="darken"/>: laid over a colour, that colour darkened by as much.</summary>
        private static IBrush Shade(double darken) => new ImmutableSolidColorBrush(Color.FromArgb((byte)Math.Round(darken * 255), 0, 0, 0));

        /// <summary>
        /// For each of a box's steps but the top one and the floor, the black that darkens the step above's shade to its own: the step
        /// shades run from <see cref="BaseDarken"/> at the floor to <see cref="TopDarken"/> under the top, as <see cref="Blend"/>ed.
        /// </summary>
        private static IBrush[] MakeStepShades()
        {
            static double DarkenOf(int step) => BaseDarken + ((TopDarken - BaseDarken) * step / (SideLayers - 1));
            var shades = new IBrush[SideLayers];
            for (int step = 1; step <= SideLayers - 2; step++)
                shades[step] = Shade(1 - ((1 - DarkenOf(step)) / (1 - DarkenOf(step + 1))));
            return shades;
        }

        /// <summary><paramref name="rect"/> filled, its corners rounded as said unless that's under half a pixel, when they're square.</summary>
        private static void FillRounded(DrawingContext context, IBrush brush, Rect rect, double radiusX, double radiusY)
        {
            if (rect.Width <= 0 || rect.Height <= 0)
                return;

            if (radiusX < MinCorner || radiusY < MinCorner)
                context.FillRectangle(brush, rect);
            else
                context.DrawRectangle(brush, null, rect, radiusX, radiusY);
        }

        /// <summary>
        /// <paramref name="rect"/> filled in <paramref name="fill"/> with an edge <paramref name="thickness"/> wide in <paramref name="edge"/>,
        /// on its outline, as a pen would draw it: the edge's colour filled out to the edge's outside, then the fill inside it.
        /// </summary>
        private static void FillEdged(DrawingContext context, IBrush fill, IBrush edge, double thickness, Rect rect, double radiusX, double radiusY)
        {
            double half = thickness / 2;
            FillRounded(context, edge, rect.Inflate(half), radiusX + half, radiusY + half);
            var inner = new Rect(rect.X + half, rect.Y + half, Math.Max(0, rect.Width - thickness), Math.Max(0, rect.Height - thickness));
            FillRounded(context, fill, inner, Math.Max(0, radiusX - half), Math.Max(0, radiusY - half));
        }

        /// <summary>
        /// A cymbal, as a box is drawn (see <see cref="DrawSides"/>): its shadow, its floor dark and edged, its sides in
        /// steps, and its top face, lighter and edged; each hexagon one of the few unit shapes stretched to fit.
        /// </summary>
        private void FillCymbal(DrawingContext context, Stand stand, Color color)
        {
            var floor = stand.Floor;
            var top = stand.Top;
            double point = Math.Min(floor.Height * 0.6, floor.Width * 0.25);
            var light = _flat.Brush(HighwayPalette.Darken(color, TopDarken));

            // As a box's (see DrawSides): the floor edged, light, darkened; the sides light down to the floor's step; then each band of a
            // step and those below it darkened, its upper edge the lower edge of the step above, as the hexagon's.
            FillHexagon(context, ShadowBrush, floor, point, 0);
            FillHexagon(context, OutlineBrush, floor.Inflate(0.5), point, 0);
            FillHexagon(context, light, Shrunk(floor, 0.5), point, 0);
            FillHexagon(context, FloorShade, Shrunk(floor, 0.5), point, 0);
            double lowest = SweepTo(stand, 1);
            FillHexagon(context, light, top, point, lowest);
            for (int step = SideLayers - 2; step >= 1; step--)
            {
                double from = SweepTo(stand, step + 1);
                FillHexagonBand(context, StepShades[step], top.Translate(new Vector(0, from)), point, lowest - from);
            }

            FillHexagon(context, _flat.Brush(HighwayPalette.Darken(color, 0.45)), top.Inflate(0.5), point, 0);
            FillHexagon(context, _flat.Brush(HighwayPalette.Lighten(color, TopLighten)), Shrunk(top, 0.5), point, 0);
        }

        /// <summary>How far down from a cymbal's top its hexagon is when it's at the <paramref name="step"/>th of its steps, counted up from its floor.</summary>
        private static double SweepTo(Stand stand, int step) => stand.Rise - (stand.Rise * step / SideLayers);

        /// <summary>
        /// The band under a cymbal's hexagon filling <paramref name="rect"/>, its points <paramref name="point"/> in from its sides: the
        /// hexagon's lower edge swept down by <paramref name="thickness"/>, all it covers on the way. A unit shape, kept, stretched to it.
        /// </summary>
        private void FillHexagonBand(DrawingContext context, IBrush brush, Rect rect, double point, double thickness)
        {
            if (rect.Width <= 0 || rect.Height <= 0 || thickness <= 0)
                return;

            var key = ((int)Math.Round(Math.Min(point / rect.Width, 0.5) * PointSteps), (int)Math.Round(thickness / rect.Height * SweepSteps));
            if (!_unitBands.TryGetValue(key, out var unit))
                unit = _unitBands.Add(key, HexagonBand(key.Item1 / (double)PointSteps, key.Item2 / (double)SweepSteps));

            using (context.PushTransform(Matrix.CreateScale(rect.Width, rect.Height) * Matrix.CreateTranslation(rect.X, rect.Y)))
                context.DrawGeometry(brush, null, unit);
        }

        /// <summary>A unit hexagon's lower edge, its points <paramref name="point"/> in, swept down by <paramref name="thickness"/>.</summary>
        private static StreamGeometry HexagonBand(double point, double thickness)
        {
            var band = new StreamGeometry();
            using (var g = band.Open())
            {
                g.BeginFigure(new Point(0, 0.5), true);
                g.LineTo(new Point(point, 1));
                g.LineTo(new Point(1 - point, 1));
                g.LineTo(new Point(1, 0.5));
                g.LineTo(new Point(1, 0.5 + thickness));
                g.LineTo(new Point(1 - point, 1 + thickness));
                g.LineTo(new Point(point, 1 + thickness));
                g.LineTo(new Point(0, 0.5 + thickness));
                g.EndFigure(true);
            }

            return band;
        }

        private static Rect Shrunk(Rect rect, double by) =>
            new(rect.X + by, rect.Y + by, Math.Max(0, rect.Width - (2 * by)), Math.Max(0, rect.Height - (2 * by)));

        /// <summary>
        /// A cymbal's hexagon filling <paramref name="rect"/>, its points <paramref name="point"/> in from its sides, slid down by
        /// <paramref name="sweep"/> with all it covers on the way: a unit shape, kept, stretched to it.
        /// </summary>
        private void FillHexagon(DrawingContext context, IBrush brush, Rect rect, double point, double sweep)
        {
            if (rect.Width <= 0 || rect.Height <= 0)
                return;

            var key = ((int)Math.Round(Math.Min(point / rect.Width, 0.5) * PointSteps), (int)Math.Round(sweep / rect.Height * SweepSteps));
            if (!_unitHexagons.TryGetValue(key, out var unit))
                unit = _unitHexagons.Add(key, SweptHexagon(1, 1, key.Item2 / (double)SweepSteps, key.Item1 / (double)PointSteps));

            using (context.PushTransform(Matrix.CreateScale(rect.Width, rect.Height) * Matrix.CreateTranslation(rect.X, rect.Y)))
                context.DrawGeometry(brush, null, unit);
        }

        private void DrawCymbal(DrawingContext context, Stand stand, Color color, Color edge, bool outlined)
        {
            if (outlined)
            {
                // Made at the nearest of a few sizes and stretched to the note's, which changes every frame as it comes nearer.
                var size = stand.Floor.Size;
                bool near = size.Width >= DashedOutline.MinBucketed && size.Height >= DashedOutline.MinBucketed;
                var made = near ? new Size(HalfPixel(DashedOutline.Bucket(size.Width)), HalfPixel(DashedOutline.Bucket(size.Height))) : size;
                var place = Matrix.CreateTranslation(stand.Top.X, stand.Top.Y);
                using (context.PushTransform(near ? Matrix.CreateScale(size.Width / made.Width, size.Height / made.Height) * place : place))
                    context.DrawGeometry(null, _flat.Pen(edge, 1.5), Hexagon(made));
                return;
            }

            FillCymbal(context, stand, color);
        }

        /// <summary>The edge of a note's shape in <paramref name="rect"/>: a rounded rectangle, as round as the note's box is.</summary>
        private static void DrawEdge(DrawingContext context, Rect rect, Stand stand, IPen pen) =>
            context.DrawRectangle(null, pen, rect, stand.RadiusX, stand.RadiusY);

        // A size to the half pixel, as Hexagon makes its shapes.
        private static double HalfPixel(double pixels) => Math.Round(pixels * 2) / 2;

        /// <summary>A cymbal's hexagon filling <paramref name="size"/> from the origin, made once for each size to the half pixel.</summary>
        private StreamGeometry Hexagon(Size size)
        {
            var key = ((int)Math.Round(size.Width * 2), (int)Math.Round(size.Height * 2));
            if (_hexagons.TryGetValue(key, out var hexagon))
                return hexagon;

            // Sizes change with every depth a cymbal is at: those no longer drawn go.
            if (_hexagons.Count > MaxShapes)
                _hexagons.Clear();

            double width = key.Item1 / 2.0;
            double height = key.Item2 / 2.0;
            hexagon = SweptHexagon(width, height, 0);
            _hexagons[key] = hexagon;
            return hexagon;
        }

        /// <summary>A cymbal's hexagon of <paramref name="width"/> and <paramref name="height"/> from the origin, slid down by <paramref name="sweep"/>: the hull of where it starts and ends.</summary>
        /// <param name="point">How far in from the sides its points are; by default as a cymbal's are for its size.</param>
        private static StreamGeometry SweptHexagon(double width, double height, double sweep, double? point = null)
        {
            double inset = point ?? Math.Min(height * 0.6, width * 0.25);
            var hexagon = new StreamGeometry();
            using (var g = hexagon.Open())
            {
                g.BeginFigure(new Point(0, height / 2), true);
                g.LineTo(new Point(inset, 0));
                g.LineTo(new Point(width - inset, 0));
                g.LineTo(new Point(width, height / 2));
                if (sweep > 0)
                    g.LineTo(new Point(width, (height / 2) + sweep));
                g.LineTo(new Point(width - inset, height + sweep));
                g.LineTo(new Point(inset, height + sweep));
                if (sweep > 0)
                    g.LineTo(new Point(0, (height / 2) + sweep));
                g.EndFigure(true);
            }

            return hexagon;
        }

        private static Color Blend(Color from, Color to, double amount) => Color.FromArgb(
            from.A,
            (byte)(from.R + ((to.R - from.R) * amount)),
            (byte)(from.G + ((to.G - from.G) * amount)),
            (byte)(from.B + ((to.B - from.B) * amount)));
    }
}
