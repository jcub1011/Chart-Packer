using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace ChartBuilder.Controls
{
    /// <summary>
    /// Stands in for the editor's timeline: second marks, a made-up waveform where the full mix will be drawn, and a
    /// playhead that clicking or dragging moves (scrubbing). The notes come later.
    /// </summary>
    public sealed class TimelinePlaceholder : Control
    {
        private const double RulerHeight = 18;
        private const double BarWidth = 3;

        public static readonly StyledProperty<double> PositionProperty =
            AvaloniaProperty.Register<TimelinePlaceholder, double>(nameof(Position), defaultBindingMode: BindingMode.TwoWay);

        public static readonly StyledProperty<double> LengthProperty =
            AvaloniaProperty.Register<TimelinePlaceholder, double>(nameof(Length), 1);

        public static readonly StyledProperty<IBrush?> WaveformBrushProperty =
            AvaloniaProperty.Register<TimelinePlaceholder, IBrush?>(nameof(WaveformBrush));

        public static readonly StyledProperty<IBrush?> TickBrushProperty =
            AvaloniaProperty.Register<TimelinePlaceholder, IBrush?>(nameof(TickBrush));

        public static readonly StyledProperty<IBrush?> PlayheadBrushProperty =
            AvaloniaProperty.Register<TimelinePlaceholder, IBrush?>(nameof(PlayheadBrush));

        static TimelinePlaceholder()
        {
            AffectsRender<TimelinePlaceholder>(PositionProperty, LengthProperty, WaveformBrushProperty, TickBrushProperty, PlayheadBrushProperty);
            ClipToBoundsProperty.OverrideDefaultValue<TimelinePlaceholder>(true);
        }

        /// <summary>The playhead, in seconds.</summary>
        public double Position
        {
            get => GetValue(PositionProperty);
            set => SetValue(PositionProperty, value);
        }

        /// <summary>How many seconds the timeline spans.</summary>
        public double Length
        {
            get => GetValue(LengthProperty);
            set => SetValue(LengthProperty, value);
        }

        public IBrush? WaveformBrush
        {
            get => GetValue(WaveformBrushProperty);
            set => SetValue(WaveformBrushProperty, value);
        }

        /// <summary>The second marks and their labels.</summary>
        public IBrush? TickBrush
        {
            get => GetValue(TickBrushProperty);
            set => SetValue(TickBrushProperty, value);
        }

        public IBrush? PlayheadBrush
        {
            get => GetValue(PlayheadBrushProperty);
            set => SetValue(PlayheadBrushProperty, value);
        }

        public override void Render(DrawingContext context)
        {
            var bounds = new Rect(Bounds.Size);
            context.FillRectangle(Brushes.Transparent, bounds); // Hit-testable everywhere, for scrubbing.
            if (Length <= 0 || bounds.Width <= 0)
                return;

            DrawWaveform(context, bounds);
            DrawRuler(context, bounds);

            if (PlayheadBrush is { } playhead)
            {
                double x = Math.Round(XOf(Position, bounds.Width)) + 0.5;
                context.DrawLine(new Pen(playhead, 1.5), new Point(x, 0), new Point(x, bounds.Height));
            }
        }

        private void DrawWaveform(DrawingContext context, Rect bounds)
        {
            if (WaveformBrush is not { } brush)
                return;

            double top = RulerHeight;
            double height = bounds.Height - top;
            if (height <= 4)
                return;

            // A few sines at unrelated rates, fixed to the song's time so it moves with the ruler rather than the width.
            double middle = top + height / 2;
            for (double x = 0; x < bounds.Width; x += BarWidth + 1)
            {
                double t = TimeOf(x, bounds.Width);
                double level = 0.35 + 0.25 * Math.Sin(t * 0.21) + 0.2 * Math.Sin(t * 1.7) * Math.Sin(t * 0.05) + 0.1 * Math.Sin(t * 7.3);
                double half = Math.Clamp(level, 0.05, 1) * (height / 2 - 2);
                context.FillRectangle(brush, new Rect(x, middle - half, BarWidth, half * 2));
            }
        }

        private void DrawRuler(DrawingContext context, Rect bounds)
        {
            if (TickBrush is not { } brush)
                return;

            var pen = new Pen(brush, 1);
            context.DrawLine(pen, new Point(0, RulerHeight - 0.5), new Point(bounds.Width, RulerHeight - 0.5));

            // A labelled mark at a round step that leaves room for its label, and a short mark between each.
            double step = LabelStep(bounds.Width);
            for (double t = 0; t <= Length; t += step)
            {
                double x = Math.Round(XOf(t, bounds.Width)) + 0.5;
                context.DrawLine(pen, new Point(x, RulerHeight - 8), new Point(x, RulerHeight));

                double between = Math.Round(XOf(t + step / 2, bounds.Width)) + 0.5;
                context.DrawLine(pen, new Point(between, RulerHeight - 4), new Point(between, RulerHeight));

                var label = new FormattedText(
                    TimeSpan.FromSeconds(t).ToString(@"m\:ss", CultureInfo.CurrentCulture),
                    CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 10, brush);
                context.DrawText(label, new Point(x + 3, 1));
            }
        }

        /// <summary>The smallest of 1, 2, 5, 10, 15, 30 or 60 seconds that keeps labels at least 56px apart.</summary>
        private double LabelStep(double width)
        {
            double pixelsPerSecond = width / Length;
            foreach (double step in (ReadOnlySpan<double>)[1, 2, 5, 10, 15, 30])
            {
                if (step * pixelsPerSecond >= 56)
                    return step;
            }

            return 60;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
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
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (ReferenceEquals(e.Pointer.Captured, this))
                e.Pointer.Capture(null);
        }

        private void Scrub(PointerEventArgs e)
        {
            if (Bounds.Width > 0)
                Position = Math.Clamp(TimeOf(e.GetPosition(this).X, Bounds.Width), 0, Length);
        }

        private double XOf(double seconds, double width) => seconds / Length * width;

        private double TimeOf(double x, double width) => x / width * Length;
    }
}
