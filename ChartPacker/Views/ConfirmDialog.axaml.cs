using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ChartPacker.Views
{
    /// <summary>A modal yes/no prompt. Closes with true for the confirm button, false otherwise.</summary>
    public partial class ConfirmDialog : Window
    {
        public ConfirmDialog()
        {
            InitializeComponent();
        }

        public ConfirmDialog(string title, string message, string confirmLabel)
            : this()
        {
            Title = title;
            MessageText.Text = message;
            ConfirmButton.Content = confirmLabel;
            AutomationProperties.SetName(ConfirmButton, confirmLabel);
        }

        private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);

        private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
    }
}
