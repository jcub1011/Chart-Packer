using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

namespace ChartBuilder.Controls
{
    /// <summary>
    /// Lays its children out in rows, wrapping as they don't fit, and centres each row in the width it's given; the
    /// toolbar's groups, whose rows stay centred as the window narrows.
    /// </summary>
    /// <remarks>
    /// A <see cref="WrapPanel"/> starts every row at its left edge, so a toolbar that wraps is only centred when it fits
    /// on one row. This keeps each row's children together and places the row as a whole, so the last, shorter row sits
    /// under the middle of the ones above it.
    /// </remarks>
    /// <example>
    /// <code>
    /// &lt;bctl:CenteredWrapPanel ItemSpacing="8" LineSpacing="6"&gt;
    ///     &lt;Border Classes="toolbar-group"&gt;…&lt;/Border&gt;
    /// &lt;/bctl:CenteredWrapPanel&gt;
    /// </code>
    /// </example>
    public sealed class CenteredWrapPanel : Panel
    {
        /// <summary>The space between neighbouring children in a row.</summary>
        public static readonly StyledProperty<double> ItemSpacingProperty =
            AvaloniaProperty.Register<CenteredWrapPanel, double>(nameof(ItemSpacing));

        /// <summary>The space between rows.</summary>
        public static readonly StyledProperty<double> LineSpacingProperty =
            AvaloniaProperty.Register<CenteredWrapPanel, double>(nameof(LineSpacing));

        // The children in each row, gathered each measure; arranging them needs the rows the measure broke them into.
        private readonly List<List<Control>> _rows = [];

        static CenteredWrapPanel()
        {
            AffectsMeasure<CenteredWrapPanel>(ItemSpacingProperty, LineSpacingProperty);
        }

        /// <summary>The space between neighbouring children in a row.</summary>
        public double ItemSpacing
        {
            get => GetValue(ItemSpacingProperty);
            set => SetValue(ItemSpacingProperty, value);
        }

        /// <summary>The space between rows.</summary>
        public double LineSpacing
        {
            get => GetValue(LineSpacingProperty);
            set => SetValue(LineSpacingProperty, value);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            _rows.Clear();

            // Children are measured unbounded so a row breaks on what they want, not on what's left of the width.
            var unbounded = new Size(double.PositiveInfinity, availableSize.Height);
            List<Control>? row = null;
            double rowWidth = 0, rowHeight = 0, width = 0, height = 0;

            foreach (var child in Children)
            {
                if (!child.IsVisible)
                    continue;

                child.Measure(unbounded);
                var childSize = child.DesiredSize;

                // The first child starts a row; so does one that doesn't fit after those already on this one.
                if (row is null || (row.Count > 0 && rowWidth + ItemSpacing + childSize.Width > availableSize.Width))
                {
                    if (row is not null)
                    {
                        width = Math.Max(width, rowWidth);
                        height += rowHeight + (_rows.Count > 0 ? LineSpacing : 0);
                    }

                    row = [];
                    _rows.Add(row);
                    rowWidth = 0;
                    rowHeight = 0;
                }

                row.Add(child);
                rowWidth += (row.Count > 1 ? ItemSpacing : 0) + childSize.Width;
                rowHeight = Math.Max(rowHeight, childSize.Height);
            }

            if (row is not null)
            {
                width = Math.Max(width, rowWidth);
                height += rowHeight + (_rows.Count > 1 ? LineSpacing : 0);
            }

            // No more than there is to give: a child too wide for its row hangs off the edge rather than widening the
            // panel, as the rows are centred on the width it's arranged at.
            return new Size(
                double.IsInfinity(availableSize.Width) ? width : Math.Min(width, availableSize.Width),
                height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var y = 0.0;
            foreach (var row in _rows)
            {
                double rowWidth = 0, rowHeight = 0;
                foreach (var child in row)
                {
                    rowWidth += child.DesiredSize.Width + (rowWidth > 0 ? ItemSpacing : 0);
                    rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
                }

                // Centre the row on the width there is, and lay its children across it.
                var x = Math.Max(0, (finalSize.Width - rowWidth) / 2);
                foreach (var child in row)
                {
                    child.Arrange(new Rect(x, y, child.DesiredSize.Width, child.DesiredSize.Height));
                    x += child.DesiredSize.Width + ItemSpacing;
                }

                y += rowHeight + LineSpacing;
            }

            return finalSize;
        }
    }
}
