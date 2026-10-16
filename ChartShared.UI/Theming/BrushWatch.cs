using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Utilities;

namespace ChartShared.UI.Theming
{
    /// <summary>
    /// Says when a brush is recoloured in place, as <see cref="DynamicTheme"/>'s are while they fade. A control that
    /// draws straight with a brush needs none: the compositor follows the brush. One that copies its colour, into a
    /// cached picture or brushes of its own, watches it with this and redraws.
    /// </summary>
    /// <remarks>The brush holds this weakly, so a long-lived brush doesn't keep the control alive.</remarks>
    public sealed class BrushWatch
    {
        private readonly WeakEventSubscriber<AvaloniaPropertyChangedEventArgs> _subscriber = new();
        private AvaloniaObject? _watched;

        /// <param name="changed">Called after the watched brush's colour or opacity changes.</param>
        public BrushWatch(Action changed)
        {
            ArgumentNullException.ThrowIfNull(changed);
            _subscriber.Event += (_, _, e) =>
            {
                if (e.Property == SolidColorBrush.ColorProperty || e.Property == Brush.OpacityProperty)
                    changed();
            };
        }

        /// <summary>Watches <paramref name="brush"/> instead of the brush watched before; <see langword="null"/> or an immutable brush, nothing.</summary>
        public void Watch(IBrush? brush)
        {
            var target = brush as AvaloniaObject;
            if (ReferenceEquals(target, _watched))
                return;

            if (_watched is not null)
                WeakEvents.AvaloniaPropertyChanged.Unsubscribe(_watched, _subscriber);
            _watched = target;
            if (target is not null)
                WeakEvents.AvaloniaPropertyChanged.Subscribe(target, _subscriber);
        }
    }
}
