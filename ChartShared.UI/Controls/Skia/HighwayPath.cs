using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;

namespace ChartShared.UI.Controls.Skia
{
    /// <summary>
    /// A shape made of straight-edged figures, for SkiaSharp: its <see cref="SKPath"/>s are made from the figures the first
    /// time they're asked for. Made once and kept, as the geometries it stands for were; never changed once made.
    /// </summary>
    public sealed class HighwayPath
    {
        private readonly Figure[] _figures;

        // Whether every figure is filled, so the filled path covers what the edged one would.
        private readonly bool _allFilled;
        private SKPath? _fill;
        private SKPath? _stroke;
        private bool _madeFill;

        // Where the shape reaches drawn with each pen it's been drawn with: few, as a shape keeps to a pen or two.
        private Dictionary<PenKey, Rect>? _reach;

        // For working out where an edge reaches, on the UI thread: one paint and one path, used and cleared.
        private static readonly SKPaint s_stroker = new() { IsStroke = true };
        private static readonly SKPath s_stroked = new();

        private HighwayPath(Figure[] figures)
        {
            _figures = figures;
            Bounds = BoundsOf(figures);
            _allFilled = Array.TrueForAll(figures, figure => figure.Filled);
        }

        /// <summary>The smallest rectangle round every corner.</summary>
        public Rect Bounds { get; }

        /// <summary>A closed figure through <paramref name="corners"/>, filled or only edged.</summary>
        public static HighwayPath Polygon(IReadOnlyList<Point> corners, bool filled = true, bool closed = true)
        {
            var points = new Point[corners.Count];
            for (int i = 0; i < points.Length; i++)
                points[i] = corners[i];
            return new HighwayPath([new Figure(points, filled, closed)]);
        }

        /// <summary>For SkiaSharp: the filled figures, or none, as Avalonia's Skia geometry keeps them apart from the edged.</summary>
        internal SKPath? FillPath
        {
            get
            {
                if (!_madeFill)
                {
                    _fill = MakeSkia(filledOnly: true);
                    _madeFill = true;
                }

                return _fill;
            }
        }

        /// <summary>For SkiaSharp: every figure, for an edge drawn along it. Made only when an edge is, as a filled shape needs none.</summary>
        internal SKPath StrokePath => _stroke ??= MakeSkia(filledOnly: false) ?? new SKPath { FillType = SKPathFillType.EvenOdd };

        /// <summary>A path of the figures, or of only the filled ones; none if there are none.</summary>
        private SKPath? MakeSkia(bool filledOnly)
        {
            SKPath? path = null;
            foreach (var figure in _figures)
            {
                if (figure.Points.Length == 0 || (filledOnly && !figure.Filled))
                    continue;
                Add(path ??= new SKPath { FillType = SKPathFillType.EvenOdd }, figure);
            }

            return path;

            static void Add(SKPath path, Figure figure)
            {
                path.MoveTo((float)figure.Points[0].X, (float)figure.Points[0].Y);
                for (int i = 1; i < figure.Points.Length; i++)
                    path.LineTo((float)figure.Points[i].X, (float)figure.Points[i].Y);
                if (figure.Closed)
                    path.Close();
            }
        }

        /// <summary>
        /// Where the shape reaches drawn with <paramref name="pen"/>, or with none, as Avalonia's Skia geometry works it out for the
        /// bounds of a drawing: the outline its edge makes, mitred corners and all, with what its filled figures cover.
        /// </summary>
        internal Rect ReachWith(IPen? pen)
        {
            var key = pen is null ? default : new PenKey(pen.Thickness, pen.LineCap, pen.LineJoin, pen.MiterLimit, pen.DashStyle);
            if (_reach?.TryGetValue(key, out var known) == true)
                return known;

            Rect reach;
            if (pen is null || pen.Thickness == 0)
            {
                // With every figure filled, the filled path is every figure, and the edged one needn't be made.
                reach = _allFilled ? (FillPath is { } filled ? ToRect(filled.TightBounds) : default) : ToRect(StrokePath.TightBounds);
            }
            else
            {
                var stroke = StrokePath;
                s_stroker.StrokeWidth = (float)pen.Thickness;
                s_stroker.StrokeCap = pen.LineCap switch { PenLineCap.Round => SKStrokeCap.Round, PenLineCap.Square => SKStrokeCap.Square, _ => SKStrokeCap.Butt };
                s_stroker.StrokeJoin = pen.LineJoin switch { PenLineJoin.Bevel => SKStrokeJoin.Bevel, PenLineJoin.Round => SKStrokeJoin.Round, _ => SKStrokeJoin.Miter };
                s_stroker.StrokeMiter = (float)pen.MiterLimit;
                s_stroker.PathEffect = SkiaShaders.DashOf(pen);
                s_stroked.Reset();
                s_stroker.GetFillPath(stroke, s_stroked);
                reach = ToRect(s_stroked.TightBounds);
                s_stroked.Reset();
                s_stroker.PathEffect = null;
            }

            if (FillPath is { } fill)
                reach = reach.Union(ToRect(fill.TightBounds));

            (_reach ??= [])[key] = reach;
            return reach;

            static Rect ToRect(SKRect r) => new(r.Left, r.Top, r.Width, r.Height);
        }

        private static Rect BoundsOf(Figure[] figures)
        {
            double left = double.PositiveInfinity, top = double.PositiveInfinity, right = double.NegativeInfinity, bottom = double.NegativeInfinity;
            foreach (var figure in figures)
            {
                foreach (var point in figure.Points)
                {
                    left = Math.Min(left, point.X);
                    top = Math.Min(top, point.Y);
                    right = Math.Max(right, point.X);
                    bottom = Math.Max(bottom, point.Y);
                }
            }

            return right >= left && bottom >= top ? new Rect(left, top, right - left, bottom - top) : default;
        }

        /// <summary>Makes a <see cref="HighwayPath"/> figure by figure, as a <c>StreamGeometryContext</c> makes a geometry.</summary>
        public sealed class Builder
        {
            private readonly List<Figure> _figures = [];
            private readonly List<Point> _points = [];
            private bool _filled;
            private bool _open;

            public void BeginFigure(Point start, bool isFilled)
            {
                if (_open)
                    EndFigure(false);
                _points.Clear();
                _points.Add(start);
                _filled = isFilled;
                _open = true;
            }

            public void LineTo(Point point) => _points.Add(point);

            public void EndFigure(bool isClosed)
            {
                if (!_open)
                    return;
                _figures.Add(new Figure([.. _points], _filled, isClosed));
                _open = false;
            }

            public HighwayPath Build()
            {
                if (_open)
                    EndFigure(false);
                return new HighwayPath([.. _figures]);
            }
        }

        private readonly record struct Figure(Point[] Points, bool Filled, bool Closed);

        private readonly record struct PenKey(double Thickness, PenLineCap Cap, PenLineJoin Join, double Miter, IDashStyle? Dashes);
    }
}
