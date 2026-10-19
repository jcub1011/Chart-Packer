using System;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls.Shapes;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;

namespace ChartShared.UI.Controls
{
    /// <summary>
    /// The spinner, a rotating arc styled as <c>Arc.spinner</c> (AppTheme.axaml), turned by the compositor on the render
    /// thread rather than by a style animation on the UI thread. It goes on turning while the UI thread is busy, as when a
    /// song's Editor is being made. It turns while <see cref="IsActive"/>.
    /// </summary>
    public sealed class Spinner : Arc
    {
        // How long a turn takes, as Arc.spinner.active's.
        private static readonly TimeSpan TurnDuration = TimeSpan.FromSeconds(0.9);

        public static readonly StyledProperty<bool> IsActiveProperty =
            AvaloniaProperty.Register<Spinner, bool>(nameof(IsActive));

        // The visual turning, while it is.
        private CompositionVisual? _turning;

        // Styled as an Arc, so Arc.spinner applies.
        protected override Type StyleKeyOverride => typeof(Arc);

        /// <summary>Whether it turns.</summary>
        public bool IsActive
        {
            get => GetValue(IsActiveProperty);
            set => SetValue(IsActiveProperty, value);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Refresh();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            Stop();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsActiveProperty || change.Property == BoundsProperty)
                Refresh();
        }

        /// <summary>Turns it about its centre while it's active and laid out, or stops it.</summary>
        private void Refresh()
        {
            var visual = IsActive && Bounds.Width > 0 ? ElementComposition.GetElementVisual(this) : null;
            if (visual is null)
            {
                Stop();
                return;
            }

            visual.CenterPoint = new Vector3D(Bounds.Width / 2, Bounds.Height / 2, 0);
            if (ReferenceEquals(visual, _turning))
                return;

            Stop();
            var turn = visual.Compositor.CreateScalarKeyFrameAnimation();
            turn.InsertKeyFrame(0f, 0f);
            turn.InsertKeyFrame(1f, MathF.Tau, new LinearEasing());
            turn.Duration = TurnDuration;
            turn.IterationBehavior = AnimationIterationBehavior.Forever;
            visual.StartAnimation(nameof(CompositionVisual.RotationAngle), turn);
            _turning = visual;
        }

        private void Stop()
        {
            if (_turning is not { } visual)
                return;
            visual.StopAnimation(nameof(CompositionVisual.RotationAngle));
            visual.RotationAngle = 0;
            _turning = null;
        }
    }
}
