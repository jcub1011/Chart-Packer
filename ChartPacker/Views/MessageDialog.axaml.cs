using Avalonia.Controls;
using Avalonia.Interactivity;
using ChartShared.UI.Views;

namespace ChartPacker.Views
{
    /// <summary>A modal notice with only an OK button.</summary>
    public partial class MessageDialog : Window
    {
        public MessageDialog()
        {
            InitializeComponent();
            WindowChrome.ShowIcon(this, WindowTitleBar);
            Opened += (_, _) => WindowChrome.UseDarkTitleBar(this);
        }

        public MessageDialog(string title, string message)
            : this()
        {
            Title = title;
            MessageText.Text = message;
        }

        private void OnOk(object? sender, RoutedEventArgs e) => Close();
    }
}
