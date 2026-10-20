using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ChartShared.Core.Charting.Difficulty;

namespace ChartBuilder.Controls
{
    /// <summary>
    /// The spider graph of the five difficulty attributes (docs/difficulty-model.md §9.2): an axis each, a marked ring at
    /// 100, the reference, and the values as a polygon, unclamped, so the graph grows to fit what's past it. A second polygon,
    /// faint, can be drawn behind for comparison.
    /// </summary>
    public sealed class DifficultySpiderView : Control
    {
        /// <summary>
        /// The axes, clockwise from the top. Fixed, and round a loop, so note and strum sit side by side: the physical
        /// attributes together, the reading ones opposite.
        /// </summary>
        public static IReadOnlyList<DifficultyAttribute> AxisOrder { get; } =
            [DifficultyAttribute.Strum, DifficultyAttribute.Rhythm, DifficultyAttribute.Endurance, DifficultyAttribute.Pattern, DifficultyAttribute.Note];

        private const double LabelRoom = 30;

        public static readonly StyledProperty<DifficultyAttributes?> ValuesProperty =
            AvaloniaProperty.Register<DifficultySpiderView, DifficultyAttributes?>(nameof(Values));

        public static readonly StyledProperty<DifficultyAttributes?> ComparisonProperty =
            AvaloniaProperty.Register<DifficultySpiderView, DifficultyAttributes?>(nameof(Comparison));

        public static readonly StyledProperty<IReadOnlyList<string>?> LabelsProperty =
            AvaloniaProperty.Register<DifficultySpiderView, IReadOnlyList<string>?>(nameof(Labels));

        public static readonly StyledProperty<IBrush?> StrokeBrushProperty =
            AvaloniaProperty.Register<DifficultySpiderView, IBrush?>(nameof(StrokeBrush));

        public static readonly StyledProperty<IBrush?> FillBrushProperty =
            AvaloniaProperty.Register<DifficultySpiderView, IBrush?>(nameof(FillBrush));

        public static readonly StyledProperty<IBrush?> ComparisonBrushProperty =
            AvaloniaProperty.Register<DifficultySpiderView, IBrush?>(nameof(ComparisonBrush));

        public static readonly StyledProperty<IBrush?> RingBrushProperty =
            AvaloniaProperty.Register<DifficultySpiderView, IBrush?>(nameof(RingBrush));

        public static readonly StyledProperty<IBrush?> ReferenceBrushProperty =
            AvaloniaProperty.Register<DifficultySpiderView, IBrush?>(nameof(ReferenceBrush));

        public static readonly StyledProperty<IBrush?> TextBrushProperty =
            AvaloniaProperty.Register<DifficultySpiderView, IBrush?>(nameof(TextBrush));

        static DifficultySpiderView()
        {
            AffectsRender<DifficultySpiderView>(ValuesProperty, ComparisonProperty, LabelsProperty, StrokeBrushProperty, FillBrushProperty, ComparisonBrushProperty, RingBrushProperty, ReferenceBrushProperty, TextBrushProperty);
        }

        /// <summary>The values drawn; null for none, which draws only the axes.</summary>
        public DifficultyAttributes? Values
        {
            get => GetValue(ValuesProperty);
            set => SetValue(ValuesProperty, value);
        }

        /// <summary>The values drawn faintly behind, for comparison; null for none.</summary>
        public DifficultyAttributes? Comparison
        {
            get => GetValue(ComparisonProperty);
            set => SetValue(ComparisonProperty, value);
        }

        /// <summary>The axes' names, in <see cref="AxisOrder"/>.</summary>
        public IReadOnlyList<string>? Labels
        {
            get => GetValue(LabelsProperty);
            set => SetValue(LabelsProperty, value);
        }

        public IBrush? StrokeBrush
        {
            get => GetValue(StrokeBrushProperty);
            set => SetValue(StrokeBrushProperty, value);
        }

        public IBrush? FillBrush
        {
            get => GetValue(FillBrushProperty);
            set => SetValue(FillBrushProperty, value);
        }

        public IBrush? ComparisonBrush
        {
            get => GetValue(ComparisonBrushProperty);
            set => SetValue(ComparisonBrushProperty, value);
        }

        /// <summary>The axes and the rings at 25, 50 and 75.</summary>
        public IBrush? RingBrush
        {
            get => GetValue(RingBrushProperty);
            set => SetValue(RingBrushProperty, value);
        }

        /// <summary>The ring at 100, the reference.</summary>
        public IBrush? ReferenceBrush
        {
            get => GetValue(ReferenceBrushProperty);
            set => SetValue(ReferenceBrushProperty, value);
        }

        public IBrush? TextBrush
        {
            get => GetValue(TextBrushProperty);
            set => SetValue(TextBrushProperty, value);
        }

        public override void Render(DrawingContext context)
        {
            var size = Bounds.Size;
            double radius = (Math.Min(size.Width, size.Height) / 2) - LabelRoom;
            if (radius <= 8)
                return;
            var centre = new Point(size.Width / 2, size.Height / 2);

            // The reference sits at the edge until a value goes past it.
            double scale = 100;
            foreach (var values in (ReadOnlySpan<DifficultyAttributes?>)[Values, Comparison])
            {
                if (values is { } v)
                    scale = Math.Max(scale, v.Max);
            }

            if (RingBrush is { } ring)
            {
                var pen = new ImmutablePen(ring.ToImmutable(), 1);
                foreach (double level in (ReadOnlySpan<double>)[25, 50, 75])
                    context.DrawGeometry(null, pen, Polygon(centre, radius * level / scale, _ => 1));
                for (int axis = 0; axis < AxisOrder.Count; axis++)
                    context.DrawLine(pen, centre, PointOf(centre, radius, axis));
            }

            if (ReferenceBrush is { } reference)
                context.DrawGeometry(null, new ImmutablePen(reference.ToImmutable(), 1.5), Polygon(centre, radius * 100 / scale, _ => 1));
            if (Comparison is { } comparison && ComparisonBrush is { } faint)
                context.DrawGeometry(null, new ImmutablePen(faint.ToImmutable(), 1.5, new ImmutableDashStyle([3, 2], 0)), Polygon(centre, radius / scale, a => Math.Max(0, comparison[a])));
            if (Values is { } values2)
            {
                var stroke = StrokeBrush is { } s ? new ImmutablePen(s.ToImmutable(), 2) : null;
                context.DrawGeometry(FillBrush, stroke, Polygon(centre, radius / scale, a => Math.Max(0, values2[a])));
            }

            DrawLabels(context, centre, radius);
        }

        // Each axis's name and value just past its end.
        private void DrawLabels(DrawingContext context, Point centre, double radius)
        {
            if (TextBrush is not { } brush)
                return;
            for (int axis = 0; axis < AxisOrder.Count; axis++)
            {
                string name = Labels is { } labels && axis < labels.Count ? labels[axis] : AxisOrder[axis].ToString();
                string text = Values is { } values ? string.Format(CultureInfo.CurrentCulture, "{0} {1:0}", name, values[AxisOrder[axis]]) : name;
                var label = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11, brush);
                var at = PointOf(centre, radius + 6, axis);
                double x = at.X - (label.Width / 2) + ((at.X - centre.X) / radius * label.Width / 2);
                double y = at.Y - (label.Height / 2) + ((at.Y - centre.Y) / radius * label.Height / 2);
                context.DrawText(label, new Point(Math.Clamp(x, 0, Math.Max(0, Bounds.Width - label.Width)), Math.Clamp(y, 0, Math.Max(0, Bounds.Height - label.Height))));
            }
        }

        // A closed polygon with a corner on each axis, at value times scale from the centre.
        private static StreamGeometry Polygon(Point centre, double scale, Func<DifficultyAttribute, double> value)
        {
            var geometry = new StreamGeometry();
            using var g = geometry.Open();
            for (int axis = 0; axis < AxisOrder.Count; axis++)
            {
                var point = PointOf(centre, scale * value(AxisOrder[axis]), axis);
                if (axis == 0)
                    g.BeginFigure(point, true);
                else
                    g.LineTo(point);
            }

            g.EndFigure(true);
            return geometry;
        }

        // The point at distance from the centre along an axis, the first straight up and the rest clockwise.
        private static Point PointOf(Point centre, double distance, int axis)
        {
            double angle = (-Math.PI / 2) + (axis * 2 * Math.PI / AxisOrder.Count);
            return new Point(centre.X + (distance * Math.Cos(angle)), centre.Y + (distance * Math.Sin(angle)));
        }
    }
}
