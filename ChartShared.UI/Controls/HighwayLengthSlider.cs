using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using ChartShared.Core.Charting;

namespace ChartShared.UI.Controls
{
    /// <summary>
    /// The slider a highway's length is set with, whichever way it's set (<see cref="Mode"/>): in
    /// <see cref="HighwayLengthMode.LookAhead"/> it sets <see cref="LookAhead"/>, in seconds, and in
    /// <see cref="HighwayLengthMode.PixelsPerSecond"/> it sets <see cref="PixelsPerSecond"/>. Each keeps its own value, so
    /// switching the mode and back loses neither. The slider moves on the base-2 logarithmic scale of
    /// <see cref="HighwayLookAhead"/> or <see cref="HighwayPixelsPerSecond"/>, and the value is snapped as it's set.
    /// </summary>
    public sealed class HighwayLengthSlider : Slider
    {
        public static readonly StyledProperty<HighwayLengthMode> ModeProperty =
            AvaloniaProperty.Register<HighwayLengthSlider, HighwayLengthMode>(nameof(Mode), HighwayLengthMode.LookAhead);

        public static readonly StyledProperty<double> LookAheadProperty =
            AvaloniaProperty.Register<HighwayLengthSlider, double>(nameof(LookAhead), HighwayLookAhead.Min, defaultBindingMode: BindingMode.TwoWay, coerce: (_, seconds) => HighwayLookAhead.Clamp(seconds));

        public static readonly StyledProperty<double> PixelsPerSecondProperty =
            AvaloniaProperty.Register<HighwayLengthSlider, double>(nameof(PixelsPerSecond), HighwayPixelsPerSecond.Default, defaultBindingMode: BindingMode.TwoWay, coerce: (_, pixels) => HighwayPixelsPerSecond.Clamp(pixels));

        // Set while the slider and the value follow each other, so they don't set each other round again.
        private bool _following;

        public HighwayLengthSlider()
        {
            // In steps of the scale: a key moves it a twelfth of an octave, a page a half of one.
            SmallChange = 1.0 / 12;
            LargeChange = 0.5;
            Follow(ShowValue);
        }

        // Styled as a Slider, so it looks like one.
        protected override Type StyleKeyOverride => typeof(Slider);

        /// <summary>Which of <see cref="LookAhead"/> and <see cref="PixelsPerSecond"/> the slider sets.</summary>
        public HighwayLengthMode Mode
        {
            get => GetValue(ModeProperty);
            set => SetValue(ModeProperty, value);
        }

        /// <summary>The look-ahead, in seconds, in <see cref="HighwayLengthMode.LookAhead"/>.</summary>
        public double LookAhead
        {
            get => GetValue(LookAheadProperty);
            set => SetValue(LookAheadProperty, value);
        }

        /// <summary>The pixels a second of the song takes up, in <see cref="HighwayLengthMode.PixelsPerSecond"/>.</summary>
        public double PixelsPerSecond
        {
            get => GetValue(PixelsPerSecondProperty);
            set => SetValue(PixelsPerSecondProperty, value);
        }

        private bool IsPixels => Mode == HighwayLengthMode.PixelsPerSecond;

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (_following)
                return;

            if (change.Property == ModeProperty
                || (change.Property == LookAheadProperty && !IsPixels)
                || (change.Property == PixelsPerSecondProperty && IsPixels))
            {
                Follow(ShowValue);
            }
            else if (change.Property == ValueProperty)
            {
                // The thumb comes to rest on the snapped value, so it matches the readout.
                Follow(() =>
                {
                    if (IsPixels)
                        SetCurrentValue(PixelsPerSecondProperty, HighwayPixelsPerSecond.FromScale(Value));
                    else
                        SetCurrentValue(LookAheadProperty, HighwayLookAhead.FromScale(Value));
                    ShowValue();
                });
            }
        }

        // The track's range and thumb for the value in use.
        private void ShowValue()
        {
            if (IsPixels)
                ShowRange(HighwayPixelsPerSecond.MinScale, HighwayPixelsPerSecond.MaxScale, HighwayPixelsPerSecond.ToScale(PixelsPerSecond));
            else
                ShowRange(HighwayLookAhead.MinScale, HighwayLookAhead.MaxScale, HighwayLookAhead.ToScale(LookAhead));
        }

        // The range is widened to take both the old and the new before the value is set, and narrowed after, so the value in
        // the old range isn't coerced to something else on the way and then set again: it only changes once, to the new one.
        private void ShowRange(double minimum, double maximum, double value)
        {
            Minimum = Math.Min(Minimum, minimum);
            Maximum = Math.Max(Maximum, maximum);
            // Not a local value, which would win over a style or a binding that sets Value.
            SetCurrentValue(ValueProperty, value);
            Minimum = minimum;
            Maximum = maximum;
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
    }
}
