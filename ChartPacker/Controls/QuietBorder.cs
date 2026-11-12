using Avalonia.Controls;
using Avalonia.Input;

namespace ChartPacker.Controls
{
    /// <summary>
    /// A border that keeps the clicks made in it to itself. A list row's breakdown sits in one, so that a click on a
    /// picture or beside a button there doesn't reach the row and select or deselect it.
    /// </summary>
    public sealed class QuietBorder : Border
    {
        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            e.Handled = true;
        }
    }
}
