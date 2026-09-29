using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ChartPacker.Audio.Spectrum;

namespace ChartPacker.Controls
{
    /// <summary>
    /// A spectrum as bars, the way an old-style analyser draws one: they leap up with the music and fall back
    /// slowly, each with a cap marking its recent peak. While <see cref="IsActive"/>, it follows
    /// <see cref="Source"/> each time the screen is drawn; otherwise the bars fall away and it stops. Becoming
    /// active, or showing another item, starts it again from nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each bar is one shade, stepped from <see cref="LowColor"/> at the left through <see cref="MiddleColor"/> to
    /// <see cref="HighColor"/> at the right, at <see cref="BarOpacity"/> at its top and fading towards its foot.
    /// Its cap is the same shade, solid at <see cref="PeakOpacity"/>: not lightened, since anything near white
    /// competes with the text drawn over it.
    /// </para>
    /// <para>It's decoration: it takes no pointer input and isn't in the automation tree.</para>
    /// </remarks>
    /// <example><c>&lt;ctl:SpectrumView Source="{Binding Player}" IsActive="{Binding Player.IsPlaying}" /&gt;</c></example>
    public sealed class SpectrumView : Control
    {
        private const double BarGap = 2;
        private const double PeakHeight = 2;

        // A bar's foot, as a part of the opacity at its top.
        private const double FootFade = 0.1;

        // A frame late by more than this (the window was hidden, say) moves the bars on by this much only.
        private static readonly TimeSpan MaxStep = TimeSpan.FromMilliseconds(100);

        public static readonly StyledProperty<ISpectrumSource?> SourceProperty =
            AvaloniaProperty.Register<SpectrumView, ISpectrumSource?>(nameof(Source));

        public static readonly StyledProperty<bool> IsActiveProperty =
            AvaloniaProperty.Register<SpectrumView, bool>(nameof(IsActive));

        public static readonly StyledProperty<Color> LowColorProperty =
            AvaloniaProperty.Register<SpectrumView, Color>(nameof(LowColor), Colors.Gray);

        public static readonly StyledProperty<Color> MiddleColorProperty =
            AvaloniaProperty.Register<SpectrumView, Color>(nameof(MiddleColor), Colors.Gray);

        public static readonly StyledProperty<Color> HighColorProperty =
            AvaloniaProperty.Register<SpectrumView, Color>(nameof(HighColor), Colors.Gray);

        public static readonly StyledProperty<double> BarOpacityProperty =
            AvaloniaProperty.Register<SpectrumView, double>(nameof(BarOpacity), 0.14);

        public static readonly StyledProperty<double> PeakOpacityProperty =
            AvaloniaProperty.Register<SpectrumView, double>(nameof(PeakOpacity), 0.2);

        private readonly SpectrumBars _bars = new(SpectrumAnalyzer.BandCount);
        private readonly float[] _levels = new float[SpectrumAnalyzer.BandCount];
        private bool _isFrameRequested;
        private TimeSpan? _lastFrame;

        // Each bar's brushes, made again when a colour or opacity changes.
        private IImmutableBrush[]? _barBrushes;
        private IImmutableBrush[]? _peakBrushes;

        static SpectrumView()
        {
            AffectsRender<SpectrumView>(LowColorProperty, MiddleColorProperty, HighColorProperty, BarOpacityProperty, PeakOpacityProperty);
            IsHitTestVisibleProperty.OverrideDefaultValue<SpectrumView>(false);
            FocusableProperty.OverrideDefaultValue<SpectrumView>(false);
            AutomationProperties.AccessibilityViewProperty.OverrideDefaultValue<SpectrumView>(AccessibilityView.Raw);
        }

        public ISpectrumSource? Source
        {
            get => GetValue(SourceProperty);
            set => SetValue(SourceProperty, value);
        }

        /// <summary>Whether to follow <see cref="Source"/>; when not, the bars fall away.</summary>
        public bool IsActive
        {
            get => GetValue(IsActiveProperty);
            set => SetValue(IsActiveProperty, value);
        }

        /// <summary>The shade of the leftmost bar, the lowest notes.</summary>
        public Color LowColor
        {
            get => GetValue(LowColorProperty);
            set => SetValue(LowColorProperty, value);
        }

        /// <summary>The shade of the bars in the middle.</summary>
        public Color MiddleColor
        {
            get => GetValue(MiddleColorProperty);
            set => SetValue(MiddleColorProperty, value);
        }

        /// <summary>The shade of the rightmost bar, the highest notes.</summary>
        public Color HighColor
        {
            get => GetValue(HighColorProperty);
            set => SetValue(HighColorProperty, value);
        }

        /// <summary>How opaque a bar is at its top, from 0 to 1; it fades towards its foot.</summary>
        public double BarOpacity
        {
            get => GetValue(BarOpacityProperty);
            set => SetValue(BarOpacityProperty, value);
        }

        /// <summary>How opaque the caps are, from 0 to 1.</summary>
        public double PeakOpacity
        {
            get => GetValue(PeakOpacityProperty);
            set => SetValue(PeakOpacityProperty, value);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == LowColorProperty || change.Property == MiddleColorProperty || change.Property == HighColorProperty
                || change.Property == BarOpacityProperty || change.Property == PeakOpacityProperty)
            {
                _barBrushes = null;
                _peakBrushes = null;
            }

            // Starting again starts from nothing, not from what was falling away from last time.
            if (change.Property == IsActiveProperty && change.GetNewValue<bool>())
                ResetBars();

            if (change.Property == IsActiveProperty || change.Property == SourceProperty || change.Property == IsVisibleProperty)
                RequestFrame();
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);

            // A recycled container is showing something else now: what it drew before isn't this one's.
            ResetBars();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            RequestFrame();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _lastFrame = null;
        }

        // As large as it's given, so it fills a panel up to its MaxHeight even when aligned to one edge.
        protected override Size MeasureOverride(Size availableSize) =>
            new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);

        public override void Render(DrawingContext context)
        {
            double width = Bounds.Width;
            double height = Bounds.Height;
            int count = _bars.Count;
            if (width <= 0 || height <= 0)
                return;

            if (_barBrushes is null || _peakBrushes is null || _barBrushes.Length != count)
                MakeBrushes(count);

            double barWidth = Math.Max(1, (width - (BarGap * (count - 1))) / count);
            for (int i = 0; i < count; i++)
            {
                double x = i * (barWidth + BarGap);
                double bar = _bars.Levels[i] * height;
                if (bar >= 0.5)
                    context.DrawRectangle(_barBrushes![i], null, new Rect(x, height - bar, barWidth, bar));

                double peak = _bars.Peaks[i] * height;
                if (peak >= 0.5)
                    context.DrawRectangle(_peakBrushes![i], null, new Rect(x, Math.Max(0, height - peak - PeakHeight), barWidth, PeakHeight));
            }
        }

        /// <summary>
        /// Each of <paramref name="count"/> bars' brushes: its shade, a step along low, middle and high, fading down
        /// the bar from its top to its foot; and the same shade, solid, for its cap.
        /// </summary>
        private void MakeBrushes(int count)
        {
            _barBrushes = new IImmutableBrush[count];
            _peakBrushes = new IImmutableBrush[count];
            double barOpacity = Math.Clamp(BarOpacity, 0, 1);
            double peakOpacity = Math.Clamp(PeakOpacity, 0, 1);
            for (int i = 0; i < count; i++)
            {
                double along = count > 1 ? (double)i / (count - 1) : 0.5;
                var shade = along < 0.5 ? Blend(LowColor, MiddleColor, along * 2) : Blend(MiddleColor, HighColor, (along - 0.5) * 2);

                // Relative to the rectangle each bar is drawn in, so every bar fades over its own height.
                _barBrushes[i] = new ImmutableLinearGradientBrush(
                    [
                        new ImmutableGradientStop(0, WithOpacity(shade, barOpacity)),
                        new ImmutableGradientStop(1, WithOpacity(shade, barOpacity * FootFade)),
                    ],
                    startPoint: new RelativePoint(0, 0, RelativeUnit.Relative),
                    endPoint: new RelativePoint(0, 1, RelativeUnit.Relative));
                _peakBrushes[i] = new ImmutableSolidColorBrush(WithOpacity(shade, peakOpacity));
            }
        }

        private static Color Blend(Color from, Color to, double amount) => Color.FromArgb(
            255,
            (byte)Math.Round(from.R + ((to.R - from.R) * amount)),
            (byte)Math.Round(from.G + ((to.G - from.G) * amount)),
            (byte)Math.Round(from.B + ((to.B - from.B) * amount)));

        private static Color WithOpacity(Color color, double opacity) => Color.FromArgb((byte)Math.Round(255 * opacity), color.R, color.G, color.B);

        private void ResetBars()
        {
            _bars.Reset();
            _lastFrame = null;
            InvalidateVisual();
        }

        /// <summary>Asks to be called back when the screen is next drawn, while there's something to show.</summary>
        private void RequestFrame()
        {
            if (_isFrameRequested || !IsVisible || (!IsActive && _bars.IsSettled) || TopLevel.GetTopLevel(this) is not { } topLevel)
                return;

            _isFrameRequested = true;
            topLevel.RequestAnimationFrame(OnFrame);
        }

        private void OnFrame(TimeSpan now)
        {
            _isFrameRequested = false;
            if (TopLevel.GetTopLevel(this) is null)
                return;

            var elapsed = _lastFrame is { } last ? now - last : TimeSpan.Zero;
            _lastFrame = now;
            if (elapsed > MaxStep)
                elapsed = MaxStep;

            bool isHeard = IsActive && Source is { } source && source.TryReadSpectrum(_levels);
            _bars.Update(isHeard ? _levels : ReadOnlySpan<float>.Empty, elapsed);
            InvalidateVisual();

            if (!IsActive && _bars.IsSettled)
            {
                _lastFrame = null;
                return;
            }

            RequestFrame();
        }
    }
}
