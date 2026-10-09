using Avalonia.Controls;
using Avalonia.Input;
using ChartBuilder.ViewModels;

namespace ChartBuilder.Views
{
    /// <summary>
    /// Properties (charter-spec C3 item 4), the top of the Editor's right panel: what's selected. A field applies what's
    /// typed when Enter is pressed or the field is left, and Escape puts it back.
    /// </summary>
    public partial class PropertiesView : UserControl
    {
        public PropertiesView()
        {
            InitializeComponent();
            AddHandler(KeyDownEvent, FieldKeys.OnKeyDown);
        }

        // Type lyrics' line, in its flyout: Enter gives it to the selected notes and closes the flyout. Space is the line's,
        // so typing doesn't play or pause the song.
        private void OnTypedLyricsKeyDown(object? sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    if (DataContext is PropertiesViewModel properties)
                        properties.ApplyTypedLyricsCommand.Execute(null);
                    TypeLyricsButton.Flyout?.Hide();
                    e.Handled = true;
                    break;
                case Key.Space:
                    e.Handled = true;
                    break;
            }
        }
    }
}
