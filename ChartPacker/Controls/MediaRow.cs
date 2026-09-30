using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace ChartPacker.Controls
{
    /// <summary>
    /// Lays its children out left to right at one height, each as wide as its
    /// <see cref="AspectRatioProperty"/> makes it: the tallest height at which they all fit the width,
    /// with the row no taller than <see cref="MaxRowHeight"/> or <see cref="MaxViewportFraction"/> of
    /// the window's height, whichever is smaller.
    /// Each child is <see cref="FooterHeight"/> taller than its media, for a caption below it.
    /// </summary>
    /// <example><c>&lt;ctl:MediaRow&gt;&lt;Border ctl:MediaRow.AspectRatio="{Binding ArtAspect}" /&gt;…&lt;/ctl:MediaRow&gt;</c></example>
    public sealed class MediaRow : Panel
    {
        /// <summary>The most the row can be tall, footer included.</summary>
        public static readonly StyledProperty<double> MaxRowHeightProperty =
            AvaloniaProperty.Register<MediaRow, double>(nameof(MaxRowHeight), 512);

        /// <summary>The most the row can be tall, footer included, as a fraction of the window's height; unlimited unless set.</summary>
        public static readonly StyledProperty<double> MaxViewportFractionProperty =
            AvaloniaProperty.Register<MediaRow, double>(nameof(MaxViewportFraction), double.PositiveInfinity);

        public static readonly StyledProperty<double> SpacingProperty =
            AvaloniaProperty.Register<MediaRow, double>(nameof(Spacing), 20);

        /// <summary>The height each child has below its media, e.g. for a caption; its aspect ratio doesn't count it.</summary>
        public static readonly StyledProperty<double> FooterHeightProperty =
            AvaloniaProperty.Register<MediaRow, double>(nameof(FooterHeight), 0);

        /// <summary>A child's media width over its height; 1 unless set.</summary>
        public static readonly AttachedProperty<double> AspectRatioProperty =
            AvaloniaProperty.RegisterAttached<MediaRow, Control, double>("AspectRatio", 1);

        private TopLevel? _topLevel;

        static MediaRow()
        {
            AffectsMeasure<MediaRow>(MaxRowHeightProperty, MaxViewportFractionProperty, SpacingProperty, FooterHeightProperty);
            AffectsParentMeasure<MediaRow>(AspectRatioProperty);
        }

        public double MaxRowHeight
        {
            get => GetValue(MaxRowHeightProperty);
            set => SetValue(MaxRowHeightProperty, value);
        }

        public double MaxViewportFraction
        {
            get => GetValue(MaxViewportFractionProperty);
            set => SetValue(MaxViewportFractionProperty, value);
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

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _topLevel = TopLevel.GetTopLevel(this);
            if (_topLevel is not null)
                _topLevel.PropertyChanged += OnTopLevelPropertyChanged;
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            if (_topLevel is not null)
                _topLevel.PropertyChanged -= OnTopLevelPropertyChanged;
            _topLevel = null;
            base.OnDetachedFromVisualTree(e);
        }

        // The window's height limits the row's.
        private void OnTopLevelPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == TopLevel.ClientSizeProperty)
                InvalidateMeasure();
        }

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

        /// <summary>The media height at which the visible children fill <paramref name="width"/>, kept within the limits.</summary>
        private double RowHeight(double width)
        {
            double max = MaxMediaHeight();
            var visible = Children.Where(c => c.IsVisible).ToList();
            double aspects = visible.Sum(AspectOf);
            if (double.IsInfinity(width) || aspects <= 0)
                return max;

            double fit = (width - Spacing * Math.Max(0, visible.Count - 1)) / aspects;
            return Math.Clamp(fit, 0, max);
        }

        /// <summary>The tallest the media can be: the row's limit, less the footer.</summary>
        private double MaxMediaHeight()
        {
            double max = MaxRowHeight;
            if (_topLevel is { ClientSize.Height: > 0 } topLevel && MaxViewportFraction is var fraction && double.IsFinite(fraction) && fraction >= 0)
                max = Math.Min(max, topLevel.ClientSize.Height * fraction);

            return Math.Max(0, max - Math.Max(0, FooterHeight));
        }

        // A missing or nonsensical ratio (no image yet, a 0x0 frame) is taken as square.
        private static double AspectOf(Control child) =>
            GetAspectRatio(child) is var aspect && double.IsFinite(aspect) && aspect > 0 ? aspect : 1;
    }
}
