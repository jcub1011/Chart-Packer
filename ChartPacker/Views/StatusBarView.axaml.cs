using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;

namespace ChartPacker.Views
{
    public partial class StatusBarView : UserControl
    {
        // Between the bar's top edge and the bottom of a tip.
        private const double TipGap = 4;

        // Where the pointer is over the bar, in this view's coordinates; null while it's elsewhere.
        private Point? _pointer;

        public StatusBarView()
        {
            InitializeComponent();
            ToolTip.SetCustomPopupPlacementCallback(BarButton, PlaceTipAbove);
            ToolTip.SetCustomPopupPlacementCallback(CancelButton, PlaceTipAbove);
        }

        protected override void OnPointerEntered(PointerEventArgs e)
        {
            base.OnPointerEntered(e);
            _pointer = e.GetPosition(this);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            _pointer = e.GetPosition(this);
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            _pointer = null;
        }

        /// <summary>
        /// Puts a tip above the bar, starting at the pointer's x. The bar runs along the bottom of the window, where
        /// Fluent's tip, placed below the pointer, would flip up over it: under the pointer, it took the hover and the
        /// clicks. Above the bar it never covers it, and it only slides sideways to stay on screen. Without a pointer
        /// over the bar, the tip starts at the button's left edge.
        /// </summary>
        private void PlaceTipAbove(CustomPopupPlacement placement)
        {
            // The anchor comes as the button's bounds in the window's coordinates, not the button's own.
            Rect button = placement.AnchorRectangle;
            double x = _pointer is { } pointer ? this.TranslatePoint(pointer, placement.Target)?.X ?? 0 : 0;
            x = Math.Clamp(x, 0, button.Width);
            placement.AnchorRectangle = new Rect(button.X + x, button.Y, 1, 1);
            placement.Anchor = PopupAnchor.TopLeft;
            placement.Gravity = PopupGravity.TopRight;
            placement.Offset = new Point(0, -TipGap);
            placement.ConstraintAdjustment = PopupPositionerConstraintAdjustment.SlideX;
        }
    }
}
