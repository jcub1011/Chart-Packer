using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace ChartPacker.Controls
{
    /// <summary>
    /// Lays its children out left to right at one height, each as wide as its
    /// <see cref="AspectRatioProperty"/> makes it: the tallest height, up to
    /// <see cref="MaxRowHeight"/>, at which they all fit the width.
    /// Each child is <see cref="FooterHeight"/> taller than that, for a caption below its media.
    /// </summary>
    /// <example><c>&lt;ctl:MediaRow&gt;&lt;Border ctl:MediaRow.AspectRatio="{Binding ArtAspect}" /&gt;…&lt;/ctl:MediaRow&gt;</c></example>
    public sealed class MediaRow : Panel
    {
        public static readonly StyledProperty<double> MaxRowHeightProperty =
            AvaloniaProperty.Register<MediaRow, double>(nameof(MaxRowHeight), 512);

        public static readonly StyledProperty<double> SpacingProperty =
            AvaloniaProperty.Register<MediaRow, double>(nameof(Spacing), 20);

        /// <summary>The height each child has below its media, e.g. for a caption; its aspect ratio doesn't count it.</summary>
        public static readonly StyledProperty<double> FooterHeightProperty =
            AvaloniaProperty.Register<MediaRow, double>(nameof(FooterHeight), 0);

        /// <summary>A child's media width over its height; 1 unless set.</summary>
        public static readonly AttachedProperty<double> AspectRatioProperty =
            AvaloniaProperty.RegisterAttached<MediaRow, Control, double>("AspectRatio", 1);

        static MediaRow()
        {
            AffectsMeasure<MediaRow>(MaxRowHeightProperty, SpacingProperty, FooterHeightProperty);
            AffectsParentMeasure<MediaRow>(AspectRatioProperty);
        }

        public double MaxRowHeight
        {
            get => GetValue(MaxRowHeightProperty);
            set => SetValue(MaxRowHeightProperty, value);
        }

        public double Spacing
        {
            get => GetValue(SpacingProperty);
            set => SetValue(SpacingProperty, value);
        }

        public double FooterHeight
        {
            get => GetValue(FooterHeightProperty);
            set => SetValue(FooterHeightProperty, value);
        }

        public static double GetAspectRatio(Control element) => element.GetValue(AspectRatioProperty);

        public static void SetAspectRatio(Control element, double value) => element.SetValue(AspectRatioProperty, value);

        protected override Size MeasureOverride(Size availableSize)
        {
            double height = RowHeight(availableSize.Width);
            double footer = Math.Max(0, FooterHeight);
            double width = 0;
            int count = 0;
            foreach (var child in Children.Where(c => c.IsVisible))
            {
                double childWidth = height * AspectOf(child);
                child.Measure(new Size(childWidth, height + footer));
                width += childWidth;
                count++;
            }

            return new Size(width + Spacing * Math.Max(0, count - 1), count == 0 ? 0 : height + footer);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double height = RowHeight(finalSize.Width);
            double footer = Math.Max(0, FooterHeight);
            double x = 0;
            foreach (var child in Children.Where(c => c.IsVisible))
            {
                double childWidth = height * AspectOf(child);
                child.Arrange(new Rect(x, 0, childWidth, height + footer));
                x += childWidth + Spacing;
            }

            return finalSize;
        }

        /// <summary>The height at which the visible children fill <paramref name="width"/>, kept within the limits.</summary>
        private double RowHeight(double width)
        {
            var visible = Children.Where(c => c.IsVisible).ToList();
            double aspects = visible.Sum(AspectOf);
            if (double.IsInfinity(width) || aspects <= 0)
                return MaxRowHeight;

            double fit = (width - Spacing * Math.Max(0, visible.Count - 1)) / aspects;
            return Math.Clamp(fit, 0, Math.Max(0, MaxRowHeight));
        }

        // A missing or nonsensical ratio (no image yet, a 0x0 frame) is taken as square.
        private static double AspectOf(Control child) =>
            GetAspectRatio(child) is var aspect && double.IsFinite(aspect) && aspect > 0 ? aspect : 1;
    }
}
