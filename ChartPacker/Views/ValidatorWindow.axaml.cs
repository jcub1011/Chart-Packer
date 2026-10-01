using Avalonia.Controls;

namespace ChartPacker.Views
{
    /// <summary>
    /// The Validator window. Closing it keeps any unsaved edits: they're drafts of the songs, which the main window
    /// shows and saves too.
    /// </summary>
    public partial class ValidatorWindow : Window
    {
        public ValidatorWindow()
        {
            InitializeComponent();
            Opened += (_, _) => WindowChrome.UseDarkTitleBar(this);
        }
    }
}
