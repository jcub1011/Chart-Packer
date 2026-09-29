using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using ChartPacker.Audio.Waveforms;

namespace ChartPacker.Controls
{
    /// <summary>
    /// A song's waveform with a playhead, which can be clicked or dragged to seek. The part already
    /// played is drawn in <see cref="PlayedBrush"/>.
    /// </summary>
    /// <remarks>
    /// While the pointer is down <see cref="PositionSeconds"/> follows it, between <see cref="ScrubStarted"/>
    /// and <see cref="ScrubEnded"/>; the owner seeks when it ends, as it does for a slider's thumb. The time
    /// shown is <see cref="LengthSeconds"/>, or else what the waveform covers, so it can be used before the
    /// song is opened.
    /// </remarks>
    /// <example><c>&lt;ctl:WaveformView Waveform="{Binding Player.Waveform}" PositionSeconds="{Binding Player.PositionSeconds}" /&gt;</c></example>
    public sealed class WaveformView : Control
    {
        private const double BarWidth = 2;
        private const double BarGap = 1;
        private const double MinBarHeight = 1.5;

        public static readonly StyledProperty<Waveform?> WaveformProperty =
            AvaloniaProperty.Register<WaveformView, Waveform?>(nameof(Waveform));

        public static readonly StyledProperty<double> PositionSecondsProperty =
            AvaloniaProperty.Register<WaveformView, double>(nameof(PositionSeconds), defaultBindingMode: BindingMode.TwoWay);

        public static readonly StyledProperty<double> LengthSecondsProperty =
            AvaloniaProperty.Register<WaveformView, double>(nameof(LengthSeconds));

        public static readonly StyledProperty<IBrush?> PlayedBrushProperty =
            AvaloniaProperty.Register<WaveformView, IBrush?>(nameof(PlayedBrush), Brushes.White);

        public static readonly StyledProperty<IBrush?> UnplayedBrushProperty =
            AvaloniaProperty.Register<WaveformView, IBrush?>(nameof(UnplayedBrush), Brushes.Gray);

        private bool _isScrubbing;

        // Each column's levels, worked out again only when the waveform, the width or the duration
        // changes, and not as the playhead moves.
        private (double Low, double High)[] _columnPeaks = [];
        private Waveform? _columnPeaksWaveform;
        private double _columnPeaksDuration;

        static WaveformView()
        {
            AffectsRender<WaveformView>(WaveformProperty, PositionSecondsProperty, LengthSecondsProperty, PlayedBrushProperty, UnplayedBrushProperty);
            FocusableProperty.OverrideDefaultValue<WaveformView>(false);
            CursorProperty.OverrideDefaultValue<WaveformView>(new Cursor(StandardCursorType.Hand));
        }

        /// <summary>Raised when the pointer goes down on the waveform, before <see cref="PositionSeconds"/> moves.</summary>
        public event EventHandler? ScrubStarted;

        /// <summary>Raised when the pointer is let go, or capture is lost.</summary>
        public event EventHandler? ScrubEnded;

        /// <summary>What is heard, or null for a flat line.</summary>
        public Waveform? Waveform
        {
            get => GetValue(WaveformProperty);
            set => SetValue(WaveformProperty, value);
        }

        public double PositionSeconds
        {
            get => GetValue(PositionSecondsProperty);
            set => SetValue(PositionSecondsProperty, value);
        }

        /// <summary>The song's length; zero until it is known, when the waveform's own length is used.</summary>
        public double LengthSeconds
        {
            get => GetValue(LengthSecondsProperty);
            set => SetValue(LengthSecondsProperty, value);
        }

        public IBrush? PlayedBrush
        {
            get => GetValue(PlayedBrushProperty);
            set => SetValue(PlayedBrushProperty, value);
        }

        public IBrush? UnplayedBrush
        {
            get => GetValue(UnplayedBrushProperty);
            set => SetValue(UnplayedBrushProperty, value);
        }

        private double DurationSeconds => LengthSeconds > 0 ? LengthSeconds : Waveform?.Seconds ?? 0;

        protected override Size MeasureOverride(Size availableSize) =>
            new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, Math.Min(availableSize.Height, 44));

        public override void Render(DrawingContext context)
        {
            double width = Bounds.Width;
            double height = Bounds.Height;
            if (width <= 0 || height <= 0)
                return;

            // Transparent, so the whole bar takes the pointer and not only the drawn bars.
            context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

            double duration = DurationSeconds;
            double played = duration > 0 ? Math.Clamp(PositionSeconds / duration, 0, 1) : 0;
            double middle = height / 2;
            int columns = Math.Max(1, (int)((width + BarGap) / (BarWidth + BarGap)));
            var peaks = ColumnPeaks(Waveform, columns, duration);

            for (int column = 0; column < columns; column++)
            {
                double x = column * (BarWidth + BarGap);
                double from = (double)column / columns;
                double to = (double)(column + 1) / columns;
                var (low, high) = peaks[column];

                double top = middle - (high * middle);
                double bottom = middle - (low * middle);
                if (bottom - top < MinBarHeight)
                {
                    top = middle - (MinBarHeight / 2);
                    bottom = middle + (MinBarHeight / 2);
                }

                // A column is played once the playhead has passed its middle.
                var brush = (from + to) / 2 <= played ? PlayedBrush : UnplayedBrush;
                context.DrawRectangle(brush, null, new RoundedRect(new Rect(x, top, BarWidth, bottom - top), 1));
            }
        }

        /// <summary>The lowest and highest level of each of <paramref name="columns"/> columns across <paramref name="duration"/>.</summary>
        private (double Low, double High)[] ColumnPeaks(Waveform? waveform, int columns, double duration)
        {
            if (_columnPeaks.Length == columns && ReferenceEquals(waveform, _columnPeaksWaveform) && duration == _columnPeaksDuration)
                return _columnPeaks;

            var peaks = new (double Low, double High)[columns];
            if (waveform is not null && duration > 0)
            {
                for (int column = 0; column < columns; column++)
                    peaks[column] = Peak(waveform, (double)column / columns * duration, (double)(column + 1) / columns * duration);
            }

            _columnPeaks = peaks;
            _columnPeaksWaveform = waveform;
            _columnPeaksDuration = duration;
            return peaks;
        }

        /// <summary>The lowest and highest level between two times.</summary>
        private static (double Low, double High) Peak(Waveform waveform, double fromSeconds, double toSeconds)
        {
            int first = Math.Max(0, (int)Math.Floor(fromSeconds * WaveformPeaks.BucketsPerSecond));
            int last = Math.Min(waveform.Count, (int)Math.Ceiling(toSeconds * WaveformPeaks.BucketsPerSecond));

            double low = 0, high = 0;
            for (int i = first; i < last; i++)
            {
                low = Math.Min(low, waveform.Min[i]);
                high = Math.Max(high, waveform.Max[i]);
            }

            return (low, high);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || DurationSeconds <= 0)
                return;

            _isScrubbing = true;
            e.Pointer.Capture(this);
            ScrubStarted?.Invoke(this, EventArgs.Empty);
            Scrub(e);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (_isScrubbing)
                Scrub(e);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (!_isScrubbing)
                return;

            Scrub(e);
            e.Pointer.Capture(null);
            EndScrub();
            e.Handled = true;
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            EndScrub();
        }

        private void Scrub(PointerEventArgs e)
        {
            double x = e.GetPosition(this).X;
            PositionSeconds = Math.Clamp(x / Math.Max(1, Bounds.Width), 0, 1) * DurationSeconds;
        }

        private void EndScrub()
        {
            if (!_isScrubbing)
                return;

            _isScrubbing = false;
            ScrubEnded?.Invoke(this, EventArgs.Empty);
        }
    }
}
