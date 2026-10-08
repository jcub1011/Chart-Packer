using Avalonia.Controls;
using Avalonia.Input;

namespace ChartBuilder.Views
{
    /// <summary>
    /// The Chart Inspector, the Editor's left panel's first tab: the song's metadata, album art, tracks and files.
    /// </summary>
    public partial class ChartInspectorView : UserControl
    {
        public ChartInspectorView()
        {
            InitializeComponent();
            AddHandler(KeyDownEvent, OnFieldKeyDown);
        }

        // After the field has had the key: Space and undo and redo are the field's, so typing a value doesn't play or pause
        // the song (the window's Space) or undo a chart edit (its Ctrl+Z and Ctrl+Y).
        private static void OnFieldKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Source is not TextBox)
                return;
            if (e.Key == Key.Space || (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key is Key.Z or Key.Y))
                e.Handled = true;
        }
    }
}
