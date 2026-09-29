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
    /// <para>
    /// Each column shows how loud that stretch is (its root mean square) in full, over a faint bar for how far
    /// its peaks reach, so a stem's dynamics show even where its peaks are always near full scale.
    /// </para>
    /// <para>
    /// While the pointer is down <see cref="PositionSeconds"/> follows it, between <see cref="ScrubStarted"/>
    /// and <see cref="ScrubEnded"/>; the owner seeks when it ends, as it does for a slider's thumb. The time
    /// shown is <see cref="LengthSeconds"/>, or else what the waveform covers, so it can be used before the
    /// song is opened. For a waveform that only shows, set <c>IsHitTestVisible</c> to false.
    /// </para>
    /// <para>
    /// A waveform still being read grows in from the left, following <see cref="Waveform.ReadSeconds"/>
    /// smoothly rather than jumping with each part read; one that's already read sweeps in quickly.
    /// </para>
    /// </remarks>
    /// <example><c>&lt;ctl:WaveformView Waveform="{Binding Player.Waveform}" PositionSeconds="{Binding Player.PositionSeconds}" /&gt;</c></example>
    public sealed class WaveformView : Control
    {
        private const double BarWidth = 2;
        private const double BarGap = 1;
        private const double MinBarHeight = 1.5;

        // How loud a column must be to fill the height: a full-scale sine, at -3 dBFS.
        private const double RmsGain = 1.41;

        // The peaks behind the loudness are drawn this faintly.
        private const double PeakOpacity = 0.35;

        // How far back from where the waveform is shown to the columns grow into place, as a part of its length.
        private const double EdgeFraction = 0.03;

        // The time taken to catch up with what has been read. A little over how often a stem being read reports
        // its progress, so the edge keeps moving between reports rather than stopping and starting.
        private static readonly TimeSpan RevealTime = TimeSpan.FromMilliseconds(300);

        // A frame late by more than this (the window was hidden, say) moves the edge on by this much only.
        private static readonly TimeSpan MaxStep = TimeSpan.FromMilliseconds(100);

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
        private Column[] _columns = [];
        private Waveform? _columnsWaveform;
        private double _columnsDuration;

        // How far in, in seconds, the waveform is drawn, moving towards TargetSeconds at _revealSpeed seconds a second.
        private double _shownSeconds;
        private double _revealSpeed;
        private bool _isFrameRequested;
        private TimeSpan? _lastFrame;

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

        private double EdgeSeconds => DurationSeconds * EdgeFraction;

        /// <summary>
        /// Where the edge is heading: as far as has been read, or once it's all read, far enough past the end
        /// that the last columns have grown in too.
        /// </summary>
        private double TargetSeconds => Waveform switch
        {
            null or { Count: 0 } => 0,
            { } waveform when waveform.ReadCount >= waveform.Count => Math.Max(DurationSeconds, waveform.Seconds) + EdgeSeconds,
            { } waveform => waveform.ReadSeconds,
        };

        protected override Size MeasureOverride(Size availableSize) =>
            new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, Math.Min(availableSize.Height, 44));

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == WaveformProperty)
                OnWaveformChanged(change.GetOldValue<Waveform?>(), change.GetNewValue<Waveform?>());
            else if (change.Property == LengthSecondsProperty)
                Retarget();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            RequestFrame();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);

            // A frame asked for now may never come; one is asked for again when it's shown.
            _isFrameRequested = false;
            _lastFrame = null;
        }

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
            var levels = Columns(Waveform, columns, duration);
            double edge = EdgeSeconds;

            // The peaks first, faintly, all in one layer; then the loudness over them.
            using (context.PushOpacity(PeakOpacity))
            {
                for (int column = 0; column < columns; column++)
                {
                    double grown = Grown(column, columns, duration, edge);
                    var (low, high, _) = levels[column];
                    DrawBar(context, column, columns, played, middle, -low * grown, high * grown);
                }
            }

            for (int column = 0; column < columns; column++)
            {
                double grown = Grown(column, columns, duration, edge);
                var (low, high, rms) = levels[column];

                // Never past the peaks: a square wave is as loud as its peaks, and no louder.
                double loud = Math.Min(rms * RmsGain, Math.Max(-low, high)) * grown;
                DrawBar(context, column, columns, played, middle, loud, loud);
            }
        }

        /// <summary>Draws a column reaching <paramref name="below"/> and <paramref name="above"/> of the way from the middle, each from 0 to 1.</summary>
        private void DrawBar(DrawingContext context, int column, int columns, double played, double middle, double below, double above)
        {
            double x = column * (BarWidth + BarGap);
            double top = middle - (Math.Clamp(above, 0, 1) * middle);
            double bottom = middle + (Math.Clamp(below, 0, 1) * middle);
            if (bottom - top < MinBarHeight)
            {
                top = middle - (MinBarHeight / 2);
                bottom = middle + (MinBarHeight / 2);
            }

            // A column is played once the playhead has passed its middle.
            double centre = (column + 0.5) / columns;
            var brush = centre <= played ? PlayedBrush : UnplayedBrush;
            context.DrawRectangle(brush, null, new RoundedRect(new Rect(x, top, BarWidth, bottom - top), 1));
        }

        /// <summary>
        /// How far <paramref name="column"/> has grown in, from 0 to 1: flat past where the waveform is shown, and
        /// growing over the <paramref name="edge"/> seconds before it.
        /// </summary>
        private double Grown(int column, int columns, double duration, double edge)
        {
            double start = (double)column / columns * duration;
            if (edge <= 0)
                return _shownSeconds > start ? 1 : 0;

            double grown = Math.Clamp((_shownSeconds - start) / edge, 0, 1);

            // Eased, so a column slows as it reaches its full height.
            return grown * (2 - grown);
        }

        /// <summary>A new waveform: from nothing, if it's another one, or the edge heads on to what's been read of it.</summary>
        private void OnWaveformChanged(Waveform? old, Waveform? waveform)
        {
            if (waveform is null || waveform.Count == 0 || waveform.Count != old?.Count)
                _shownSeconds = 0;

            Retarget();
        }

        /// <summary>Heads the edge for <see cref="TargetSeconds"/>, to get there in <see cref="RevealTime"/>.</summary>
        private void Retarget()
        {
            double target = TargetSeconds;

            // Less is known than is shown (a stem still being read was unmuted, say): shown only as far as is known.
            if (target < _shownSeconds)
                _shownSeconds = target;

            _revealSpeed = (target - _shownSeconds) / RevealTime.TotalSeconds;
            RequestFrame();
        }

        /// <summary>Asks to be called back when the screen is next drawn, while the edge has further to go.</summary>
        private void RequestFrame()
        {
            if (_isFrameRequested || _shownSeconds >= TargetSeconds || TopLevel.GetTopLevel(this) is not { } topLevel)
                return;

            _isFrameRequested = true;
            topLevel.RequestAnimationFrame(OnFrame);
        }

        private void OnFrame(TimeSpan now)
        {
            if (!_isFrameRequested)
                return;

            _isFrameRequested = false;
            var elapsed = _lastFrame is { } last ? now - last : TimeSpan.Zero;
            _lastFrame = now;
            if (elapsed > MaxStep)
                elapsed = MaxStep;

            double target = TargetSeconds;
            _shownSeconds = Math.Min(target, _shownSeconds + (_revealSpeed * elapsed.TotalSeconds));
            InvalidateVisual();

            if (_shownSeconds >= target)
            {
                _lastFrame = null;
                return;
            }

            RequestFrame();
        }

        /// <summary>The levels of each of <paramref name="columns"/> columns across <paramref name="duration"/>.</summary>
        private Column[] Columns(Waveform? waveform, int columns, double duration)
        {
            if (_columns.Length == columns && ReferenceEquals(waveform, _columnsWaveform) && duration == _columnsDuration)
                return _columns;

            var levels = new Column[columns];
            if (waveform is not null && duration > 0)
            {
                for (int column = 0; column < columns; column++)
                    levels[column] = Levels(waveform, (double)column / columns * duration, (double)(column + 1) / columns * duration);
            }

            _columns = levels;
            _columnsWaveform = waveform;
            _columnsDuration = duration;
            return levels;
        }

        /// <summary>The lowest and highest level between two times, and how loud it is across them.</summary>
        private static Column Levels(Waveform waveform, double fromSeconds, double toSeconds)
        {
            int first = Math.Max(0, (int)Math.Floor(fromSeconds * WaveformPeaks.BucketsPerSecond));
            int last = Math.Min(waveform.Count, (int)Math.Ceiling(toSeconds * WaveformPeaks.BucketsPerSecond));

            double low = 0, high = 0, squares = 0;
            for (int i = first; i < last; i++)
            {
                low = Math.Min(low, waveform.Min[i]);
                high = Math.Max(high, waveform.Max[i]);
                squares += (double)waveform.Rms[i] * waveform.Rms[i];
            }

            // The loudness of the buckets together, rather than of the loudest, so a column isn't only as quiet as its loudest moment.
            double rms = last > first ? Math.Sqrt(squares / (last - first)) : 0;
            return new Column(low, high, rms);
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

        /// <summary>A column's lowest and highest level, and its root mean square.</summary>
        private readonly record struct Column(double Low, double High, double Rms);
    }
}
