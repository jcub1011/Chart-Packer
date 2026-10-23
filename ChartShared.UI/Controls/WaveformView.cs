using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ChartShared.Audio.Waveforms;
using ChartShared.UI.Theming;

namespace ChartShared.UI.Controls
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
    /// With Ctrl held a drag is fine: the position moves on from where it is by <see cref="FineSecondsPerPixel"/> for each
    /// pixel, rather than following the pointer, which is held still, and a press with Ctrl held leaves it where it is.
    /// Let go of Ctrl while dragging, it follows the pointer again.
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
        private const double BarRadius = 1;

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

        public static readonly StyledProperty<double> FineSecondsPerPixelProperty =
            AvaloniaProperty.Register<WaveformView, double>(nameof(FineSecondsPerPixel), LaneScrub.DefaultFineSecondsPerPixel);

        private bool _isScrubbing;

        // A drag with Ctrl held, which moves the position by how far the pointer goes, holding it still.
        private readonly FineDrag _fine;

        // Each column's levels, worked out again only when the waveform, the width or the duration
        // changes, and not as the playhead moves.
        private Column[] _columns = [];
        private Waveform? _columnsWaveform;
        private double _columnsDuration;

        // The columns drawn into two pictures, played and not, at the screen's pixels, so redrawing the control (as the
        // playhead moves, or as what's behind it moves) draws two pictures rather than every column. Drawn again only
        // when the columns, the size, the brushes or how far they've grown in change. And how many columns were drawn
        // played, so the playhead only redraws as it passes one.
        private RenderTargetBitmap? _playedBars;
        private RenderTargetBitmap? _unplayedBars;
        private (Column[] Columns, Size Size, double Scaling, double Shown, IBrush? Played, IBrush? Unplayed) _barsKey;
        private int _drawnPlayed = -1;

        // A brush recoloured in place, as a themed one fades, leaves the pictures in its old colour; draw them again.
        private readonly BrushWatch _playedWatch;
        private readonly BrushWatch _unplayedWatch;

        // How far in, in seconds, the waveform is drawn, moving towards TargetSeconds at _revealSpeed seconds a second.
        private double _shownSeconds;
        private double _revealSpeed;
        private bool _isFrameRequested;
        private TimeSpan? _lastFrame;

        static WaveformView()
        {
            AffectsRender<WaveformView>(WaveformProperty, LengthSecondsProperty, PlayedBrushProperty, UnplayedBrushProperty);
            FocusableProperty.OverrideDefaultValue<WaveformView>(false);
            CursorProperty.OverrideDefaultValue<WaveformView>(LaneScrub.SidewaysCursor);
        }

        public WaveformView()
        {
            _fine = new FineDrag(this);
            _playedWatch = new BrushWatch(OnBrushRecoloured);
            _unplayedWatch = new BrushWatch(OnBrushRecoloured);
            _playedWatch.Watch(PlayedBrush);
            _unplayedWatch.Watch(UnplayedBrush);
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

        /// <summary>How far a pixel dragged with Ctrl held moves the position, in seconds, whatever the song's length.</summary>
        public double FineSecondsPerPixel
        {
            get => GetValue(FineSecondsPerPixelProperty);
            set => SetValue(FineSecondsPerPixelProperty, value);
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
            else if (change.Property == PlayedBrushProperty)
                _playedWatch.Watch(PlayedBrush);
            else if (change.Property == UnplayedBrushProperty)
                _unplayedWatch.Watch(UnplayedBrush);
            else if (change.Property == PositionSecondsProperty && PlayedColumns(ColumnCount(Bounds.Width)) != _drawnPlayed)
                InvalidateVisual();
        }

        private void OnBrushRecoloured()
        {
            _barsKey = default;
            InvalidateVisual();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            RequestFrame();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);

            // Drawn again when it's shown.
            _playedBars?.Dispose();
            _unplayedBars?.Dispose();
            _playedBars = _unplayedBars = null;
            _barsKey = default;

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
            int columns = ColumnCount(width);
            var levels = Columns(Waveform, columns, duration);
            int played = PlayedColumns(columns);
            _drawnPlayed = played;

            // While the columns grow in they change every frame, so they're drawn as they are rather than into pictures.
            if (_shownSeconds < TargetSeconds)
            {
                DrawGrowing(context, levels, columns, duration, height, played);
                return;
            }

            Bars(levels, columns, duration, new Size(width, height));

            // The columns played, up to the gap after the last of them, in the played brush; the rest in the other.
            double split = Math.Clamp((played * (BarWidth + BarGap)) - (BarGap / 2), 0, width);
            var playedArea = new Rect(0, 0, split, height);
            var unplayedArea = new Rect(split, 0, width - split, height);

            DrawPart(context, _playedBars, playedArea, width, height);
            DrawPart(context, _unplayedBars, unplayedArea, width, height);
        }

        private void DrawGrowing(DrawingContext context, Column[] levels, int columns, double duration, double height, int played)
        {
            double middle = height / 2;
            double edge = EdgeSeconds;
            using (context.PushOpacity(PeakOpacity))
            {
                for (int column = 0; column < columns; column++)
                {
                    if ((column < played ? PlayedBrush : UnplayedBrush) is not { } brush)
                        continue;

                    var (low, high, _) = levels[column];
                    double grown = Grown(column, columns, duration, edge);
                    Bar(context, brush, column, middle, -low * grown, high * grown);
                }
            }

            for (int column = 0; column < columns; column++)
            {
                if ((column < played ? PlayedBrush : UnplayedBrush) is not { } brush)
                    continue;

                var (low, high, rms) = levels[column];
                double loud = WaveformLevels.Loudness(low, high, rms) * Grown(column, columns, duration, edge);
                Bar(context, brush, column, middle, loud, loud);
            }
        }

        private static void DrawPart(DrawingContext context, RenderTargetBitmap? bars, Rect area, double width, double height)
        {
            if (area.Width <= 0 || bars is null)
                return;

            // Pixel for pixel, as it was drawn at the screen's pixels: smoothing would only blur it.
            using (context.PushClip(area))
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
                context.DrawImage(bars, new Rect(0, 0, bars.PixelSize.Width, bars.PixelSize.Height), new Rect(0, 0, width, height));
        }

        private static int ColumnCount(double width) => Math.Max(1, (int)((width + BarGap) / (BarWidth + BarGap)));

        /// <summary>How many of <paramref name="columns"/> columns have been played: each once the playhead has passed its middle.</summary>
        private int PlayedColumns(int columns)
        {
            double duration = DurationSeconds;
            double played = duration > 0 ? Math.Clamp(PositionSeconds / duration, 0, 1) : 0;
            return Math.Clamp((int)Math.Floor((played * columns) - 0.5) + 1, 0, columns);
        }

        /// <summary>Draws the columns into the pictures again, in each brush, if what they show has changed.</summary>
        private void Bars(Column[] levels, int columns, double duration, Size size)
        {
            // Only drawn once every column has grown in, so how far the edge has gone past them doesn't change them.
            double shown = double.PositiveInfinity;
            double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            var key = (levels, size, scaling, shown, PlayedBrush, UnplayedBrush);
            if (_barsKey == key)
                return;

            _barsKey = key;
            _playedBars?.Dispose();
            _unplayedBars?.Dispose();
            _playedBars = Draw(PlayedBrush);
            _unplayedBars = Draw(UnplayedBrush);

            RenderTargetBitmap? Draw(IBrush? brush)
            {
                if (brush is null)
                    return null;

                var pixels = new PixelSize(Math.Max(1, (int)Math.Ceiling(size.Width * scaling)), Math.Max(1, (int)Math.Ceiling(size.Height * scaling)));
                var bitmap = new RenderTargetBitmap(pixels, new Vector(96, 96));
                double middle = size.Height / 2;
                double edge = EdgeSeconds;
                using (var context = bitmap.CreateDrawingContext())
                using (context.PushTransform(Matrix.CreateScale(scaling, scaling)))
                {
                    // The peaks first, faintly; then the loudness over them.
                    using (context.PushOpacity(PeakOpacity))
                    {
                        for (int column = 0; column < columns; column++)
                        {
                            var (low, high, _) = levels[column];
                            double grown = Grown(column, columns, duration, edge);
                            Bar(context, brush, column, middle, -low * grown, high * grown);
                        }
                    }

                    for (int column = 0; column < columns; column++)
                    {
                        var (low, high, rms) = levels[column];
                        double loud = WaveformLevels.Loudness(low, high, rms) * Grown(column, columns, duration, edge);
                        Bar(context, brush, column, middle, loud, loud);
                    }
                }

                return bitmap;
            }
        }

        /// <summary>Draws a column reaching <paramref name="below"/> and <paramref name="above"/> of the way from the middle, each from 0 to 1.</summary>
        private static void Bar(DrawingContext context, IBrush brush, int column, double middle, double below, double above)
        {
            double x = column * (BarWidth + BarGap);
            double top = middle - (Math.Clamp(above, 0, 1) * middle);
            double bottom = middle + (Math.Clamp(below, 0, 1) * middle);
            if (bottom - top < MinBarHeight)
            {
                top = middle - (MinBarHeight / 2);
                bottom = middle + (MinBarHeight / 2);
            }

            context.DrawRectangle(brush, null, new RoundedRect(new Rect(x, top, BarWidth, bottom - top), BarRadius));
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
                {
                    var (low, high, rms) = WaveformLevels.Of(waveform, (double)column / columns * duration, (double)(column + 1) / columns * duration);
                    levels[column] = new Column(low, high, rms);
                }
            }

            _columns = levels;
            _columnsWaveform = waveform;
            _columnsDuration = duration;
            return levels;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || DurationSeconds <= 0)
                return;

            _isScrubbing = true;
            e.Pointer.Capture(this);
            ScrubStarted?.Invoke(this, EventArgs.Empty);

            // A press with Ctrl held starts a fine drag from where the position is.
            if (LaneScrub.IsFine(e.KeyModifiers))
                _fine.Start(e.GetPosition(this));
            else
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

        /// <summary>
        /// Moves the position with the pointer: to where it is across the waveform, or with Ctrl held, on by how far it's
        /// moved, with the pointer held where the fine drag began.
        /// </summary>
        private void Scrub(PointerEventArgs e)
        {
            var at = e.GetPosition(this);
            if (!LaneScrub.IsFine(e.KeyModifiers))
            {
                _fine.Stop();
                PositionSeconds = Math.Clamp(at.X / Math.Max(1, Bounds.Width), 0, 1) * DurationSeconds;
                return;
            }

            _fine.Start(at);
            PositionSeconds = Math.Clamp(PositionSeconds + (_fine.Move(at).X * FineSecondsPerPixel), 0, DurationSeconds);
        }

        private void EndScrub()
        {
            if (!_isScrubbing)
                return;

            _isScrubbing = false;
            _fine.Stop();
            ScrubEnded?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>A column's lowest and highest level, and its root mean square.</summary>
        private readonly record struct Column(double Low, double High, double Rms);
    }
}
