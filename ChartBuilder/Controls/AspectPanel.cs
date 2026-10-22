using System;
using Avalonia;
using Avalonia.Controls;

namespace ChartBuilder.Controls
{
    /// <summary>
    /// As wide as it's given and as tall as <see cref="AspectRatio"/> makes that width, but no taller than it's given, its
    /// children stacked to fill it: a media tile shaped like the picture it shows.
    /// </summary>
    /// <example><c>&lt;bctl:AspectPanel AspectRatio="{Binding AspectRatio}"&gt;&lt;Image … /&gt;&lt;/bctl:AspectPanel&gt;</c></example>
    public sealed class AspectPanel : Panel
    {
        /// <summary>The ratio taken when none is set, or the one set makes no sense.</summary>
        public const double DefaultAspectRatio = 16.0 / 9.0;

        /// <summary>The panel's width over its height.</summary>
        public static readonly StyledProperty<double> AspectRatioProperty =
            AvaloniaProperty.Register<AspectPanel, double>(nameof(AspectRatio), DefaultAspectRatio);

        static AspectPanel()
        {
            AffectsMeasure<AspectPanel>(AspectRatioProperty);
        }

        public double AspectRatio
        {
            get => GetValue(AspectRatioProperty);
            set => SetValue(AspectRatioProperty, value);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double width = availableSize.Width;
            if (double.IsInfinity(width))
            {
                // Nothing to fill: as wide as the widest child wants.
                width = 0;
                foreach (var child in Children)
                {
                    child.Measure(availableSize);
                    width = Math.Max(width, child.DesiredSize.Width);
                }
            }

            var size = new Size(width, Math.Min(availableSize.Height, width / Ratio()));
            foreach (var child in Children)
                child.Measure(size);
            return size;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var bounds = new Rect(finalSize);
            foreach (var child in Children)
                child.Arrange(bounds);
            return finalSize;
        }

        // A missing or nonsensical ratio (a 0x0 frame) is taken as 16:9.
        private double Ratio() => AspectRatio is var ratio && double.IsFinite(ratio) && ratio > 0 ? ratio : DefaultAspectRatio;
    }
}
