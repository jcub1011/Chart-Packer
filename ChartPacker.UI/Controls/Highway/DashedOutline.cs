using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;

namespace ChartPacker.UI.Controls.Highway
{
    /// <summary>
    /// Dashed outlines drawn as shapes of their dashes, with a plain pen: a ghost gem's edge, a short sustain's tail.
    /// </summary>
    /// <remarks>
    /// A dashed pen has its dashes worked out again every time it's drawn, a new native object each time, and these are
    /// drawn every frame the song plays: the garbage of that brought the collections that dropped frames. The dashes are
    /// worked out here once for each shape and size instead. Each dash is as long, and each gap as wide, as a dashed pen's
    /// of thickness 1, and like a dashed pen's, a dash carries on round a corner.
    /// </remarks>
    public static class DashedOutline
    {
        // Sizes are rounded to this, in pixels, so a shape moving across the screen, whose size varies by rounding between
        // frames, is the same shape each frame; and more shapes than this kept, and they're all made again.
        private const double SizeStep = 0.25;
        private const int MaxShapes = 512;

        // How many pieces each quarter-circle corner of a rounded rectangle is drawn in, enough at a gem's few pixels.
        private const int CornerPieces = 4;

        private static readonly Dictionary<(Size Size, double Radius, double Dash, double Gap), Geometry> Rectangles = [];

        /// <summary>
        /// Draws <paramref name="rect"/>'s outline, its corners rounded by <paramref name="radius"/>, dashed in
        /// <paramref name="pen"/>: <paramref name="dash"/> pixels drawn, then <paramref name="gap"/> left, from its top.
        /// </summary>
        public static void DrawRectangle(DrawingContext context, IPen pen, Rect rect, double radius, double dash, double gap)
        {
            using (context.PushTransform(Matrix.CreateTranslation(rect.X, rect.Y)))
                context.DrawGeometry(null, pen, Rectangle(rect.Size, radius, dash, gap));
        }

        /// <summary>The dashes of a rectangle's outline from the origin, as <see cref="DrawRectangle"/> draws them, made once for each size.</summary>
        public static Geometry Rectangle(Size size, double radius, double dash, double gap)
        {
            var key = (new Size(Rounded(size.Width), Rounded(size.Height)), radius, dash, gap);
            if (Rectangles.TryGetValue(key, out var dashes))
                return dashes;

            if (Rectangles.Count >= MaxShapes)
                Rectangles.Clear();
            dashes = Along(RoundedRectangle(key.Item1, radius), dash, gap);
            Rectangles[key] = dashes;
            return dashes;
        }

        /// <summary>The dashes along the closed <paramref name="outline"/>, from its first point, for the caller to keep.</summary>
        public static Geometry Along(IReadOnlyList<Point> outline, double dash, double gap)
        {
            var geometry = new StreamGeometry();
            using var context = geometry.Open();
            foreach (var points in Dashes(outline, dash, gap))
            {
                context.BeginFigure(points[0], false);
                for (int i = 1; i < points.Count; i++)
                    context.LineTo(points[i]);
                context.EndFigure(false);
            }

            return geometry;
        }

        /// <summary>
        /// The dashes along the closed <paramref name="outline"/>, from its first point, each as the points it runs through:
        /// its ends, and any corner it turns on the way.
        /// </summary>
        public static IReadOnlyList<IReadOnlyList<Point>> Dashes(IReadOnlyList<Point> outline, double dash, double gap)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dash);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gap);
            const double Epsilon = 1e-9;

            var dashes = new List<IReadOnlyList<Point>>();

            // How far into the current dash and its gap the walk round the outline is, and the dash being drawn, if one is.
            double into = 0;
            List<Point>? drawing = null;
            for (int i = 0; i < outline.Count; i++)
            {
                var from = outline[i];
                var to = outline[(i + 1) % outline.Count];
                double length = Point.Distance(from, to);
                double at = 0;
                while (length - at > Epsilon)
                {
                    bool on = into < dash;
                    double step = Math.Min((on ? dash : dash + gap) - into, length - at);
                    if (on)
                    {
                        if (drawing is null)
                        {
                            drawing = [Lerp(from, to, at / length)];
                            dashes.Add(drawing);
                        }

                        drawing.Add(Lerp(from, to, (at + step) / length));
                    }

                    at += step;
                    into += step;
                    if (on && into >= dash - Epsilon)
                    {
                        drawing = null;
                        into = dash;
                    }
                    else if (!on && into >= dash + gap - Epsilon)
                    {
                        into = 0;
                    }
                }
            }

            return dashes;
        }

        /// <summary>A rectangle's outline from the origin, its corners rounded by <paramref name="radius"/>, as points round it.</summary>
        private static List<Point> RoundedRectangle(Size size, double radius)
        {
            double width = size.Width, height = size.Height;
            double r = Math.Clamp(radius, 0, Math.Min(width, height) / 2);
            if (r <= 0)
                return [new Point(0, 0), new Point(width, 0), new Point(width, height), new Point(0, height)];

            // Clockwise from the top edge's right end, each corner from where one edge ends to where the next starts.
            var points = new List<Point>(4 * (CornerPieces + 1));
            Corner(width - r, r, -Math.PI / 2);
            Corner(width - r, height - r, 0);
            Corner(r, height - r, Math.PI / 2);
            Corner(r, r, Math.PI);
            return points;

            void Corner(double x, double y, double start)
            {
                for (int piece = 0; piece <= CornerPieces; piece++)
                {
                    double angle = start + (Math.PI / 2 * piece / CornerPieces);
                    points.Add(new Point(x + (r * Math.Cos(angle)), y + (r * Math.Sin(angle))));
                }
            }
        }

        private static double Rounded(double pixels) => Math.Round(pixels / SizeStep) * SizeStep;

        private static Point Lerp(Point from, Point to, double share) =>
            new(from.X + ((to.X - from.X) * share), from.Y + ((to.Y - from.Y) * share));
    }
}
