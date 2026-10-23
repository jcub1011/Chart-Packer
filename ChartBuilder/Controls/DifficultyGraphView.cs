using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ChartBuilder.ViewModels;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Difficulty;

namespace ChartBuilder.Controls
{
    /// <summary>
    /// What the <see cref="DifficultyGraphView"/> draws, in seconds of audio: the peak envelope at evenly spaced points from
    /// <see cref="FromSeconds"/> to <see cref="ToSeconds"/>, the sections and solos, the windows that count most towards the
    /// chart's score, and the score each rank from 1 starts at.
    /// </summary>
    public sealed record DifficultyGraph(
        double FromSeconds,
        double ToSeconds,
        IReadOnlyList<DifficultyMoment> Points,
        IReadOnlyList<OverviewSection> Sections,
        IReadOnlyList<(double From, double To)> Solos,
        IReadOnlyList<(double From, double To)> Contributors,
        IReadOnlyList<double> RankThresholds);

    /// <summary>The colours of the difficulty attributes, on the graph and beside its toggles.</summary>
    public static class DifficultyPalette
    {
        public static readonly IBrush Strum = new ImmutableSolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8));
        public static readonly IBrush Note = new ImmutableSolidColorBrush(Color.FromRgb(0xF4, 0x72, 0xB6));
        public static readonly IBrush Rhythm = new ImmutableSolidColorBrush(Color.FromRgb(0xFA, 0xCC, 0x15));
        public static readonly IBrush Pattern = new ImmutableSolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
        public static readonly IBrush Endurance = new ImmutableSolidColorBrush(Color.FromRgb(0xFB, 0x92, 0x3C));

        public static IBrush Of(DifficultyAttribute attribute) => attribute switch
        {
            DifficultyAttribute.Strum => Strum,
            DifficultyAttribute.Note => Note,
            DifficultyAttribute.Rhythm => Rhythm,
            DifficultyAttribute.Pattern => Pattern,
            _ => Endurance,
        };
    }

    /// <summary>
    /// The difficulty over the song (docs/difficulty-model.md §9.1): the overall difficulty's peak envelope, and any
    /// attribute's, against the rank lines, with the sections, the solos, the selection and the stretches that count most
    /// towards the score. Clicking or dragging moves the playhead; hovering says the difficulty there.
    /// </summary>
    /// <remarks>The playhead is drawn by a layer of its own over the rest, so it moves without the lines being drawn again.</remarks>
    public sealed class DifficultyGraphView : Control
    {
        private const double GutterWidth = 22;
        private const double RulerHeight = 14;
        private const double SectionHeight = 14;
        private const double MinLabelSpacing = 56;

        private static readonly IBrush ContributorBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x30, 0xF4, 0x3F, 0x5E));
        private static readonly IBrush SoloBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x24, 0x38, 0xBD, 0xF8));

        private readonly PlayheadLayer _playhead;
        private bool _frameRequested;
        private double _hovered = double.NaN;

        public static readonly StyledProperty<DifficultyGraph?> GraphProperty =
            AvaloniaProperty.Register<DifficultyGraphView, DifficultyGraph?>(nameof(Graph));

        public static readonly StyledProperty<IReadOnlyList<DifficultyAttribute>?> VisibleLinesProperty =
            AvaloniaProperty.Register<DifficultyGraphView, IReadOnlyList<DifficultyAttribute>?>(nameof(VisibleLines));

        public static readonly StyledProperty<double> SelectionStartProperty =
            AvaloniaProperty.Register<DifficultyGraphView, double>(nameof(SelectionStart), double.NaN);

        public static readonly StyledProperty<double> SelectionEndProperty =
            AvaloniaProperty.Register<DifficultyGraphView, double>(nameof(SelectionEnd), double.NaN);

        public static readonly StyledProperty<double> PositionProperty =
            AvaloniaProperty.Register<DifficultyGraphView, double>(nameof(Position), defaultBindingMode: BindingMode.TwoWay);

        public static readonly StyledProperty<bool> IsPlayingProperty =
            AvaloniaProperty.Register<DifficultyGraphView, bool>(nameof(IsPlaying));

        public static readonly StyledProperty<Func<TimeSpan>?> ClockProperty =
            AvaloniaProperty.Register<DifficultyGraphView, Func<TimeSpan>?>(nameof(Clock));

        public static readonly StyledProperty<Func<double, string?>?> DescribeProperty =
            AvaloniaProperty.Register<DifficultyGraphView, Func<double, string?>?>(nameof(Describe));

        public static readonly StyledProperty<IBrush?> LineBrushProperty =
            AvaloniaProperty.Register<DifficultyGraphView, IBrush?>(nameof(LineBrush));

        public static readonly StyledProperty<IBrush?> TickBrushProperty =
            AvaloniaProperty.Register<DifficultyGraphView, IBrush?>(nameof(TickBrush));

        public static readonly StyledProperty<IBrush?> SpanBrushProperty =
            AvaloniaProperty.Register<DifficultyGraphView, IBrush?>(nameof(SpanBrush));

        public static readonly StyledProperty<IBrush?> PlayheadBrushProperty =
            AvaloniaProperty.Register<DifficultyGraphView, IBrush?>(nameof(PlayheadBrush));

        static DifficultyGraphView()
        {
            AffectsRender<DifficultyGraphView>(GraphProperty, VisibleLinesProperty, SelectionStartProperty, SelectionEndProperty, LineBrushProperty, TickBrushProperty, SpanBrushProperty);
            ClipToBoundsProperty.OverrideDefaultValue<DifficultyGraphView>(true);
        }

        public DifficultyGraphView()
        {
            _playhead = new PlayheadLayer(this);
            VisualChildren.Add(_playhead);
        }

        public DifficultyGraph? Graph
        {
            get => GetValue(GraphProperty);
            set => SetValue(GraphProperty, value);
        }

        /// <summary>The attributes whose lines are drawn under the overall one.</summary>
        public IReadOnlyList<DifficultyAttribute>? VisibleLines
        {
            get => GetValue(VisibleLinesProperty);
            set => SetValue(VisibleLinesProperty, value);
        }

        /// <summary>Where the selection starts, in seconds of audio; NaN for none.</summary>
        public double SelectionStart
        {
            get => GetValue(SelectionStartProperty);
            set => SetValue(SelectionStartProperty, value);
        }

        public double SelectionEnd
        {
            get => GetValue(SelectionEndProperty);
            set => SetValue(SelectionEndProperty, value);
        }

        /// <summary>The playhead while stopped, and where scrubbing puts it, in seconds of audio.</summary>
        public double Position
        {
            get => GetValue(PositionProperty);
            set => SetValue(PositionProperty, value);
        }

        /// <summary>Whether the song is playing, so the playhead follows <see cref="Clock"/> every frame.</summary>
        public bool IsPlaying
        {
            get => GetValue(IsPlayingProperty);
            set => SetValue(IsPlayingProperty, value);
        }

        /// <summary>Where the audio is heard to be now: read every frame while playing.</summary>
        public Func<TimeSpan>? Clock
        {
            get => GetValue(ClockProperty);
            set => SetValue(ClockProperty, value);
        }

        /// <summary>What the tooltip says at a time, in seconds of audio.</summary>
        public Func<double, string?>? Describe
        {
            get => GetValue(DescribeProperty);
            set => SetValue(DescribeProperty, value);
        }

        /// <summary>The overall difficulty's line.</summary>
        public IBrush? LineBrush
        {
            get => GetValue(LineBrushProperty);
            set => SetValue(LineBrushProperty, value);
        }

        /// <summary>The ruler, its times, and the section names.</summary>
        public IBrush? TickBrush
        {
            get => GetValue(TickBrushProperty);
            set => SetValue(TickBrushProperty, value);
        }

        /// <summary>The selection's band.</summary>
        public IBrush? SpanBrush
        {
            get => GetValue(SpanBrushProperty);
            set => SetValue(SpanBrushProperty, value);
        }

        public IBrush? PlayheadBrush
        {
            get => GetValue(PlayheadBrushProperty);
            set => SetValue(PlayheadBrushProperty, value);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _playhead.Arrange(new Rect(finalSize));
            return finalSize;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == GraphProperty || change.Property == PlayheadBrushProperty || change.Property == PositionProperty)
                _playhead.InvalidateVisual();
            if (change.Property == IsPlayingProperty)
            {
                _playhead.InvalidateVisual();
                RequestFrame();
            }
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            RequestFrame();
        }

        public override void Render(DrawingContext context)
        {
            var bounds = new Rect(Bounds.Size);
            context.FillRectangle(Brushes.Transparent, bounds); // Hit-testable everywhere, for scrubbing.
            if (Graph is not { Points.Count: >= 2 } graph || graph.ToSeconds <= graph.FromSeconds || bounds.Width <= GutterWidth + 2 || bounds.Height <= RulerHeight + SectionHeight + 8)
                return;

            var plot = PlotOf(bounds);
            double top = TopOf(graph);
            foreach (var (from, to) in graph.Solos)
                FillSpan(context, SoloBrush, from, to, plot);
            foreach (var (from, to) in graph.Contributors)
                FillSpan(context, ContributorBrush, from, to, plot);
            if (SpanBrush is { } span && !double.IsNaN(SelectionStart) && !double.IsNaN(SelectionEnd))
                FillSpan(context, span, SelectionStart, SelectionEnd, plot);

            DrawRankLines(context, graph, plot, top);
            DrawSections(context, graph, plot);
            if (VisibleLines is { } lines)
            {
                foreach (var attribute in lines)
                    DrawLine(context, graph, plot, top, new ImmutablePen(DifficultyPalette.Of(attribute).ToImmutable(), 1), m => m.Attributes[attribute]);
            }

            if (LineBrush is { } brush)
                DrawLine(context, graph, plot, top, new ImmutablePen(brush.ToImmutable(), 2), static m => m.Overall);
            DrawRuler(context, graph, bounds, plot);
        }

        // The area the lines are drawn in: right of the rank labels, below the section names and above the ruler.
        private static Rect PlotOf(Rect bounds) => new(GutterWidth, SectionHeight, bounds.Width - GutterWidth, bounds.Height - SectionHeight - RulerHeight);

        // The score at the top of the plot: the soft cap, or the most shown if more, with a little room above.
        private double TopOf(DifficultyGraph graph)
        {
            double most = graph.RankThresholds.Count > 0 ? graph.RankThresholds[^1] : 100;
            foreach (var point in graph.Points)
            {
                most = Math.Max(most, point.Overall);
                if (VisibleLines is { } lines)
                {
                    foreach (var attribute in lines)
                        most = Math.Max(most, point.Attributes[attribute]);
                }
            }

            return most * 1.05;
        }

        private void FillSpan(DrawingContext context, IBrush brush, double from, double to, Rect plot)
        {
            double left = Math.Max(plot.Left, XOf(from, plot)), right = Math.Min(plot.Right, XOf(to, plot));
            if (right > left)
                context.FillRectangle(brush, new Rect(left, plot.Top, Math.Max(1, right - left), plot.Height));
        }

        // A dashed line where each rank starts, in its tier's colour, numbered in the gutter.
        private void DrawRankLines(DrawingContext context, DifficultyGraph graph, Rect plot, double top)
        {
            for (int i = 0; i < graph.RankThresholds.Count; i++)
            {
                double score = graph.RankThresholds[i];
                if (score > top)
                    break;
                int rank = i + 1;
                var brush = TierBrush(rank);
                double y = Math.Round(YOf(score, plot, top)) + 0.5;
                var pen = new ImmutablePen(brush.ToImmutable(), 1, new ImmutableDashStyle([3, 3], 0));
                context.DrawLine(pen, new Point(plot.Left, y), new Point(plot.Right, y));
                var label = new FormattedText(rank.ToString(CultureInfo.CurrentCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 10, brush);
                context.DrawText(label, new Point(GutterWidth - label.Width - 4, y - (label.Height / 2)));
            }
        }

        private IBrush TierBrush(int rank) =>
            this.TryFindResource($"DifficultyTier{Math.Min(rank, 6)}Brush", ActualThemeVariant, out var found) && found is IBrush brush ? brush : TickBrush ?? Brushes.Gray;

        private void DrawSections(DrawingContext context, DifficultyGraph graph, Rect plot)
        {
            if (TickBrush is not { } brush)
                return;
            var pen = new ImmutablePen(brush.ToImmutable(), 1, new ImmutableDashStyle([1, 3], 0));
            double lastLabelEnd = double.NegativeInfinity;
            foreach (var section in graph.Sections)
            {
                if (section.Seconds < graph.FromSeconds || section.Seconds > graph.ToSeconds)
                    continue;
                double x = Math.Round(XOf(section.Seconds, plot)) + 0.5;
                context.DrawLine(pen, new Point(x, plot.Top), new Point(x, plot.Bottom));
                if (x < lastLabelEnd)
                    continue;
                var label = new FormattedText(section.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 10, brush)
                {
                    MaxTextWidth = Math.Max(1, plot.Right - x - 2),
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis,
                };
                context.DrawText(label, new Point(x + 2, 0));
                lastLabelEnd = x + label.Width + 6;
            }
        }

        // One line through the points, the highest of those in each pixel column, so no peak is lost to the width.
        private static void DrawLine(DrawingContext context, DifficultyGraph graph, Rect plot, double top, IPen pen, Func<DifficultyMoment, double> value)
        {
            var points = graph.Points;
            int columns = Math.Max(2, (int)Math.Ceiling(plot.Width));
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                int index = 0;
                for (int column = 0; column < columns; column++)
                {
                    int end = (int)Math.Min(points.Count, Math.Ceiling((double)(column + 1) * points.Count / columns));
                    double most = 0;
                    bool any = false;
                    for (; index < end; index++)
                    {
                        most = Math.Max(most, value(points[index]));
                        any = true;
                    }

                    if (!any)
                        continue;
                    var point = new Point(plot.Left + (column * plot.Width / (columns - 1)), YOf(most, plot, top));
                    if (column == 0)
                        g.BeginFigure(point, false);
                    else
                        g.LineTo(point);
                }

                g.EndFigure(false);
            }

            context.DrawGeometry(null, pen, geometry);
        }

        private void DrawRuler(DrawingContext context, DifficultyGraph graph, Rect bounds, Rect plot)
        {
            if (TickBrush is not { } brush)
                return;

            var pen = new ImmutablePen(brush.ToImmutable(), 1);
            double y = plot.Bottom + 0.5;
            context.DrawLine(pen, new Point(plot.Left, y), new Point(plot.Right, y));

            // A labelled mark at a round step that leaves room for its label.
            double pixelsPerSecond = plot.Width / (graph.ToSeconds - graph.FromSeconds);
            double step = 3600;
            foreach (double candidate in (ReadOnlySpan<double>)[1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600])
            {
                if (candidate * pixelsPerSecond >= MinLabelSpacing)
                {
                    step = candidate;
                    break;
                }
            }

            for (double t = Math.Ceiling(Math.Max(0, graph.FromSeconds) / step) * step; t <= graph.ToSeconds; t += step)
            {
                double x = Math.Round(XOf(t, plot)) + 0.5;
                context.DrawLine(pen, new Point(x, y), new Point(x, y + 4));
                var label = new FormattedText(SongTime.Format(TimeSpan.FromSeconds(t)), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 10, brush);
                if (x + 3 + label.Width <= bounds.Width)
                    context.DrawText(label, new Point(x + 3, y + 1));
            }
        }

        /// <summary>Has the playhead drawn again every frame while playing.</summary>
        private void RequestFrame()
        {
            if (_frameRequested || !IsPlaying || TopLevel.GetTopLevel(this) is not { } top)
                return;

            _frameRequested = true;
            top.RequestAnimationFrame(_ =>
            {
                _frameRequested = false;
                _playhead.InvalidateVisual();
                RequestFrame();
            });
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Graph is null)
                return;
            e.Pointer.Capture(this);
            Scrub(e);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (ReferenceEquals(e.Pointer.Captured, this))
                Scrub(e);
            Hover(e.GetPosition(this).X);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (ReferenceEquals(e.Pointer.Captured, this))
                e.Pointer.Capture(null);
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            _hovered = double.NaN;
            ToolTip.SetTip(this, null);
        }

        private void Scrub(PointerEventArgs e)
        {
            if (Graph is { } graph)
                Position = Math.Max(0, Math.Clamp(TimeOf(e.GetPosition(this).X, PlotOf(new Rect(Bounds.Size))), graph.FromSeconds, graph.ToSeconds));
        }

        // The tooltip says what's at the pointer, worked out again only when it moves to another pixel column.
        private void Hover(double x)
        {
            if (Graph is not { } graph || Describe is not { } describe)
                return;
            var plot = PlotOf(new Rect(Bounds.Size));
            double column = Math.Round(Math.Clamp(x, plot.Left, plot.Right));
            if (column == _hovered)
                return;
            _hovered = column;
            double seconds = Math.Clamp(TimeOf(column, plot), graph.FromSeconds, graph.ToSeconds);
            ToolTip.SetTip(this, describe(seconds));
        }

        /// <summary>Where the playhead is drawn: at the clock while playing, otherwise at <see cref="Position"/>.</summary>
        private double PlayheadSeconds => IsPlaying && Clock is { } clock ? clock().TotalSeconds : Position;

        private double XOf(double seconds, Rect plot) => Graph is { } graph
            ? plot.Left + ((seconds - graph.FromSeconds) / (graph.ToSeconds - graph.FromSeconds) * plot.Width)
            : plot.Left;

        private double TimeOf(double x, Rect plot) => Graph is { } graph
            ? graph.FromSeconds + ((x - plot.Left) / Math.Max(1e-6, plot.Width) * (graph.ToSeconds - graph.FromSeconds))
            : 0;

        private static double YOf(double score, Rect plot, double top) => plot.Bottom - (Math.Clamp(score / top, 0, 1) * plot.Height);

        /// <summary>The playhead's line, over the rest.</summary>
        private sealed class PlayheadLayer(DifficultyGraphView owner) : Control
        {
            private IPen? _pen;
            private IBrush? _penBrush;

            public override void Render(DrawingContext context)
            {
                if (owner.PlayheadBrush is not { } brush || owner.Graph is not { } graph || graph.ToSeconds <= graph.FromSeconds)
                    return;
                if (!ReferenceEquals(_penBrush, brush))
                {
                    _pen = new ImmutablePen(brush.ToImmutable(), 1.5);
                    _penBrush = brush;
                }

                double seconds = owner.PlayheadSeconds;
                if (seconds < graph.FromSeconds || seconds > graph.ToSeconds)
                    return;
                var plot = PlotOf(new Rect(Bounds.Size));
                double x = Math.Round(owner.XOf(seconds, plot)) + 0.5;
                context.DrawLine(_pen!, new Point(x, plot.Top), new Point(x, plot.Bottom));
            }
        }
    }
}
