using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives.PopupPositioning;

namespace ChartShared.UI.Controls
{
    /// <summary>Where a control's tooltip goes, for a control that runs along the bottom of a window.</summary>
    public static class TipPlacement
    {
        // Between the control's top edge and the bottom of a tip.
        private const double Gap = 4;

        /// <summary>
        /// Puts a tip above the control, starting at the pointer's x. Fluent's tip, placed below the pointer, flips up over a
        /// control at the bottom of the window: under the pointer, it covers what the control shows and takes the hover and
        /// the clicks. Above the control it never covers it, and it only slides sideways to stay on screen. Without a
        /// pointer over the control, the tip starts at the control's left edge.
        /// </summary>
        /// <param name="placement">The placement Avalonia is asking for, set here.</param>
        /// <param name="pointerSpace">The visual <paramref name="pointer"/> is in the coordinates of.</param>
        /// <param name="pointer">Where the pointer is over the control, or null while it's elsewhere.</param>
        public static void PlaceAbove(CustomPopupPlacement placement, Visual pointerSpace, Point? pointer)
        {
            // The anchor comes as the control's bounds in the window's coordinates, not the control's own.
            Rect bounds = placement.AnchorRectangle;
            double x = pointer is { } at ? pointerSpace.TranslatePoint(at, placement.Target)?.X ?? 0 : 0;
            x = Math.Clamp(x, 0, bounds.Width);
            placement.AnchorRectangle = new Rect(bounds.X + x, bounds.Y, 1, 1);
            placement.Anchor = PopupAnchor.TopLeft;
            placement.Gravity = PopupGravity.TopRight;
            placement.Offset = new Point(0, -Gap);
            placement.ConstraintAdjustment = PopupPositionerConstraintAdjustment.SlideX;
        }
    }
}
