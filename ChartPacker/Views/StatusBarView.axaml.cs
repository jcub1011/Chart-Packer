using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using ChartShared.UI.Controls;

namespace ChartPacker.Views
{
    public partial class StatusBarView : UserControl
    {
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

        // The bar runs along the bottom of the window, where Fluent's tip would flip up over it.
        private void PlaceTipAbove(CustomPopupPlacement placement) => TipPlacement.PlaceAbove(placement, this, _pointer);
    }
}
