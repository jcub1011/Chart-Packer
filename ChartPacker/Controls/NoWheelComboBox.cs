using Avalonia.Controls;
using Avalonia.Input;

namespace ChartPacker.Controls
{
    /// <summary>
    /// A combo box that ignores the mouse wheel while its drop-down is closed, so wheeling over it
    /// scrolls the surrounding view instead of silently changing the selection. With the drop-down
    /// open the wheel still scrolls the list as usual.
    /// </summary>
    public class NoWheelComboBox : ComboBox
    {
        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            if (IsDropDownOpen)
                base.OnPointerWheelChanged(e);
            // Otherwise left unhandled on purpose: the event keeps bubbling to the scroll viewer.
        }
    }
}
