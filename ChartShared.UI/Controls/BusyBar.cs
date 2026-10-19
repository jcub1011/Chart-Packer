using System;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;

namespace ChartShared.UI.Controls
{
    /// <summary>
    /// Work under way for as long as it takes: a band in <see cref="Foreground"/> sweeping across it, left to right, while
    /// <see cref="IsActive"/>. The compositor moves the band on the render thread, unlike an indeterminate ProgressBar's
    /// style animation on the UI thread, so it goes on moving while the UI thread is busy, as when a song's Editor is being
    /// made.
    /// </summary>
    public sealed class BusyBar : Control
    {
        // How much of the bar the band covers, and how long it takes to cross.
        private const double BandShare = 0.3;
        private static readonly TimeSpan SweepDuration = TimeSpan.FromSeconds(1.4);

        public static readonly StyledProperty<IBrush?> ForegroundProperty =
            AvaloniaProperty.Register<BusyBar, IBrush?>(nameof(Foreground));

        public static readonly StyledProperty<bool> IsActiveProperty =
            AvaloniaProperty.Register<BusyBar, bool>(nameof(IsActive));

        private readonly Border _band = new();

        // The band's visual sweeping, and across how wide a bar, while it is.
        private CompositionVisual? _sweeping;
        private double _sweptWidth;

        public BusyBar()
        {
            ClipToBounds = true;
            IsHitTestVisible = false;
            VisualChildren.Add(_band);
        }

        /// <summary>The band's brush.</summary>
        public IBrush? Foreground
        {
            get => GetValue(ForegroundProperty);
            set => SetValue(ForegroundProperty, value);
        }

        /// <summary>Whether the band sweeps, and shows.</summary>
        public bool IsActive
        {
            get => GetValue(IsActiveProperty);
            set => SetValue(IsActiveProperty, value);
        }

        protected override Size MeasureOverride(Size availableSize) => default;

        protected override Size ArrangeOverride(Size finalSize)
        {
            _band.Arrange(new Rect(0, 0, finalSize.Width * BandShare, finalSize.Height));
            Refresh(finalSize.Width);
            return finalSize;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Refresh(Bounds.Width);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            Stop();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == ForegroundProperty)
                _band.Background = Foreground;
            else if (change.Property == IsActiveProperty)
                Refresh(Bounds.Width);
        }

        /// <summary>Sweeps the band across <paramref name="width"/> while it's active, or stops it.</summary>
        private void Refresh(double width)
        {
            _band.IsVisible = IsActive;
            var visual = IsActive && width > 0 ? ElementComposition.GetElementVisual(_band) : null;
            if (visual is null)
            {
                Stop();
                return;
            }

            if (ReferenceEquals(visual, _sweeping) && width == _sweptWidth)
                return;

            Stop();
            var sweep = visual.Compositor.CreateVector3DKeyFrameAnimation();
            sweep.InsertKeyFrame(0f, new Vector3D(-width * BandShare, 0, 0));
            sweep.InsertKeyFrame(1f, new Vector3D(width, 0, 0), new SineEaseInOut());
            sweep.Duration = SweepDuration;
            sweep.IterationBehavior = AnimationIterationBehavior.Forever;
            visual.StartAnimation(nameof(CompositionVisual.Translation), sweep);
            _sweeping = visual;
            _sweptWidth = width;
        }

        private void Stop()
        {
            if (_sweeping is not { } visual)
                return;
            visual.StopAnimation(nameof(CompositionVisual.Translation));
            visual.Translation = default;
            _sweeping = null;
        }
    }
}
