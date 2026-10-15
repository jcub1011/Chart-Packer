using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

namespace ChartShared.UI.Controls
{
    /// <summary>
    /// Lays its children out in a row, each as wide as it wants while they fit. When they don't, the children marked
    /// <see cref="CanShrinkProperty"/> give up the width, the widest first, so a short one stays whole while a long one
    /// trims (a <c>TextBlock</c> with <c>TextTrimming</c>); the rest keep their width.
    /// </summary>
    /// <remarks>
    /// A Grid's star columns would split the width evenly instead, trimming a long path while a short one beside it
    /// leaves space unused; a StackPanel would give its children all the width they ask for, so none trims.
    /// When even the parts that can't trim don't fit, the panel clips its children at its edge rather than draw past it
    /// (in a title bar, under the caption buttons).
    /// </remarks>
    /// <example>
    /// <code>
    /// &lt;ctl:ShrinkPanel Spacing="4"&gt;
    ///     &lt;Button ctl:ShrinkPanel.CanShrink="True"&gt;…&lt;/Button&gt;
    ///     &lt;ctl:LineIcon Data="{StaticResource ArrowRightStroke}" /&gt;
    ///     &lt;Button ctl:ShrinkPanel.CanShrink="True"&gt;…&lt;/Button&gt;
    /// &lt;/ctl:ShrinkPanel&gt;
    /// </code>
    /// </example>
    public sealed class ShrinkPanel : Panel
    {
        public static readonly StyledProperty<double> SpacingProperty =
            AvaloniaProperty.Register<ShrinkPanel, double>(nameof(Spacing));

        public static readonly AttachedProperty<bool> CanShrinkProperty =
            AvaloniaProperty.RegisterAttached<ShrinkPanel, Control, bool>("CanShrink");

        // The shrinking children, gathered each measure; kept so a resize doesn't allocate a list every pass.
        private readonly List<Control> _shrinking = [];

        static ShrinkPanel()
        {
            AffectsMeasure<ShrinkPanel>(SpacingProperty);
            AffectsParentMeasure<ShrinkPanel>(CanShrinkProperty);
            ClipToBoundsProperty.OverrideDefaultValue<ShrinkPanel>(true);
        }

        /// <summary>The space between neighbouring children.</summary>
        public double Spacing
        {
            get => GetValue(SpacingProperty);
            set => SetValue(SpacingProperty, value);
        }

        public static bool GetCanShrink(Control control) => control.GetValue(CanShrinkProperty);

        public static void SetCanShrink(Control control, bool value) => control.SetValue(CanShrinkProperty, value);

        protected override Size MeasureOverride(Size availableSize)
        {
            var unbounded = new Size(double.PositiveInfinity, availableSize.Height);

            var count = 0;
            double width = 0, shrinkingWidth = 0;
            foreach (var child in Children)
            {
                if (!child.IsVisible)
                    continue;

                child.Measure(unbounded);
                width += child.DesiredSize.Width;
                count++;
                if (GetCanShrink(child))
                {
                    _shrinking.Add(child);
                    shrinkingWidth += child.DesiredSize.Width;
                }
            }

            width += Math.Max(0, count - 1) * Spacing;

            if (width > availableSize.Width && _shrinking.Count > 0)
            {
                // Share what the fixed children leave among the shrinking ones, narrowest first: each takes what it
                // wants up to an even share of what's left, so what the narrow ones don't use goes to the wide ones.
                // What a child actually takes is taken off, not its share, as a trimmed TextBlock ends at a whole
                // glyph or segment and comes in under it.
                if (_shrinking.Count > 1)
                    _shrinking.Sort(static (a, b) => a.DesiredSize.Width.CompareTo(b.DesiredSize.Width));

                width -= shrinkingWidth;
                var remaining = Math.Max(0, availableSize.Width - width);
                for (var i = 0; i < _shrinking.Count; i++)
                {
                    var child = _shrinking[i];
                    var share = Math.Min(child.DesiredSize.Width, remaining / (_shrinking.Count - i));
                    child.Measure(new Size(share, availableSize.Height));
                    width += child.DesiredSize.Width;
                    remaining = Math.Max(0, remaining - child.DesiredSize.Width);
                }
            }

            _shrinking.Clear();

            double height = 0;
            foreach (var child in Children)
            {
                if (child.IsVisible)
                    height = Math.Max(height, child.DesiredSize.Height);
            }

            // A shrinking child can still want more than its share (its padding and icon don't trim), and the fixed
            // ones never shrink, so the row can still be too wide: ask for no more than there is, and clip the rest.
            return new Size(Math.Min(width, availableSize.Width), height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double x = 0;
            foreach (var child in Children)
            {
                if (!child.IsVisible)
                    continue;

                child.Arrange(new Rect(x, 0, child.DesiredSize.Width, finalSize.Height));
                x += child.DesiredSize.Width + Spacing;
            }

            return finalSize;
        }
    }
}
