using Avalonia.Controls;
using Avalonia.Interactivity;
using ChartPacker.UI.Views;

namespace ChartPacker.Views
{
    /// <summary>
    /// Shows how far the Validator's check has got while no song has failed yet. The Validator window takes over once
    /// one does; Cancel, Escape or closing it stops the check.
    /// </summary>
    public partial class ValidatorProgressDialog : Window
    {
        public ValidatorProgressDialog()
        {
            InitializeComponent();
            Opened += (_, _) => WindowChrome.UseDarkTitleBar(this);
        }

        private void OnCancel(object? sender, RoutedEventArgs e) => Close();
    }
}
