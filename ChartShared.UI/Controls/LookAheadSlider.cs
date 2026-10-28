using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using ChartShared.Core.Charting;

namespace ChartShared.UI.Controls
{
    /// <summary>
    /// The slider every highway's look-ahead is set with: <see cref="LookAhead"/>, in seconds, from
    /// <see cref="HighwayLookAhead.Min"/> to <see cref="HighwayLookAhead.Max"/>. It moves on <see cref="HighwayLookAhead"/>'s
    /// base-2 logarithmic scale, so the short look-aheads aren't crowded into the start of the track, and the value is
    /// snapped as it's set.
    /// </summary>
    public sealed class LookAheadSlider : Slider
    {
        public static readonly StyledProperty<double> LookAheadProperty =
            AvaloniaProperty.Register<LookAheadSlider, double>(nameof(LookAhead), HighwayLookAhead.Min, defaultBindingMode: BindingMode.TwoWay, coerce: Coerce);

        // Set while one of the two values follows the other, so the pair doesn't set each other round again.
        private bool _following;

        public LookAheadSlider()
        {
            Minimum = HighwayLookAhead.MinScale;
            Maximum = HighwayLookAhead.MaxScale;

            // In steps of the scale: a key moves it a twelfth of an octave, a page a half of one.
            SmallChange = 1.0 / 12;
            LargeChange = 0.5;
            // Not a local value, which would win over a style or a binding that sets Value or LookAhead.
            SetCurrentValue(ValueProperty, HighwayLookAhead.ToScale(LookAhead));
        }

        // Styled as a Slider, so it looks like one.
        protected override Type StyleKeyOverride => typeof(Slider);

        /// <summary>The look-ahead, in seconds.</summary>
        public double LookAhead
        {
            get => GetValue(LookAheadProperty);
            set => SetValue(LookAheadProperty, value);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (_following)
                return;

            if (change.Property == LookAheadProperty)
                Follow(() => SetCurrentValue(ValueProperty, HighwayLookAhead.ToScale(LookAhead)));
            else if (change.Property == ValueProperty)
            {
                // The thumb comes to rest on the snapped look-ahead, so it matches the readout.
                Follow(() =>
                {
                    SetCurrentValue(LookAheadProperty, HighwayLookAhead.FromScale(Value));
                    SetCurrentValue(ValueProperty, HighwayLookAhead.ToScale(LookAhead));
                });
            }
        }

        private void Follow(Action set)
        {
            _following = true;
            try
            {
                set();
            }
            finally
            {
                _following = false;
            }
        }

        private static double Coerce(AvaloniaObject sender, double seconds) => HighwayLookAhead.Clamp(seconds);
    }
}
