using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using ChartPacker.Audio.Spectrum;

namespace ChartPacker.Controls
{
    /// <summary>
    /// A spectrum as bars, the way an old-style analyser draws one: they leap up with the music and fall back
    /// slowly, each with a cap marking its recent peak. While <see cref="IsActive"/>, it follows
    /// <see cref="Source"/> each time the screen is drawn; otherwise the bars fall away and it stops. Becoming
    /// active, or showing another item, starts it again from nothing.
    /// </summary>
    /// <remarks>It's decoration: it takes no pointer input and isn't in the automation tree.</remarks>
    /// <example><c>&lt;ctl:SpectrumView Source="{Binding Player}" IsActive="{Binding IsPreviewing}" /&gt;</c></example>
    public sealed class SpectrumView : Control
    {
        private const double BarGap = 2;
        private const double PeakHeight = 2;

        // A frame late by more than this (the window was hidden, say) moves the bars on by this much only.
        private static readonly TimeSpan MaxStep = TimeSpan.FromMilliseconds(100);

        public static readonly StyledProperty<ISpectrumSource?> SourceProperty =
            AvaloniaProperty.Register<SpectrumView, ISpectrumSource?>(nameof(Source));

        public static readonly StyledProperty<bool> IsActiveProperty =
            AvaloniaProperty.Register<SpectrumView, bool>(nameof(IsActive));

        public static readonly StyledProperty<IBrush?> BarBrushProperty =
            AvaloniaProperty.Register<SpectrumView, IBrush?>(nameof(BarBrush), Brushes.Gray);

        public static readonly StyledProperty<IBrush?> PeakBrushProperty =
            AvaloniaProperty.Register<SpectrumView, IBrush?>(nameof(PeakBrush), Brushes.White);

        private readonly SpectrumBars _bars = new(SpectrumAnalyzer.BandCount);
        private readonly float[] _levels = new float[SpectrumAnalyzer.BandCount];
        private bool _isFrameRequested;
        private TimeSpan? _lastFrame;

        static SpectrumView()
        {
            AffectsRender<SpectrumView>(BarBrushProperty, PeakBrushProperty);
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

        public IBrush? BarBrush
        {
            get => GetValue(BarBrushProperty);
            set => SetValue(BarBrushProperty, value);
        }

        public IBrush? PeakBrush
        {
            get => GetValue(PeakBrushProperty);
            set => SetValue(PeakBrushProperty, value);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

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

        public override void Render(DrawingContext context)
        {
            double width = Bounds.Width;
            double height = Bounds.Height;
            int count = _bars.Count;
            if (width <= 0 || height <= 0)
                return;

            double barWidth = Math.Max(1, (width - (BarGap * (count - 1))) / count);
            for (int i = 0; i < count; i++)
            {
                double x = i * (barWidth + BarGap);
                double bar = _bars.Levels[i] * height;
                if (bar >= 0.5)
                    context.DrawRectangle(BarBrush, null, new Rect(x, height - bar, barWidth, bar));

                double peak = _bars.Peaks[i] * height;
                if (peak >= 0.5)
                    context.DrawRectangle(PeakBrush, null, new Rect(x, Math.Max(0, height - peak - PeakHeight), barWidth, PeakHeight));
            }
        }

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
