using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;

namespace ChartBuilder.Views
{
    /// <summary>The keys a panel's text field keeps for itself, rather than the Editor's.</summary>
    internal static class FieldKeys
    {
        /// <summary>
        /// After a field has had the key: Enter applies it, Escape puts it back. Space is the field's, so typing a name or
        /// a lyric doesn't play or pause the song (the window's Space).
        /// </summary>
        public static void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Source is not TextBox box)
                return;
            switch (e.Key)
            {
                case Key.Enter:
                    BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateSource();
                    e.Handled = true;
                    break;
                case Key.Escape:
                    BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateTarget();
                    e.Handled = true;
                    break;
                case Key.Space:
                    e.Handled = true;
                    break;
            }
        }
    }
}
