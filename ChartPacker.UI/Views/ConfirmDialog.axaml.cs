using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ChartPacker.Services;

namespace ChartPacker.Views
{
    /// <summary>
    /// A modal prompt with one or two actions and Cancel. Closes with <see cref="ConfirmChoice.First"/> or
    /// <see cref="ConfirmChoice.Second"/> for the action chosen, <see cref="ConfirmChoice.Cancel"/> otherwise.
    /// </summary>
    public partial class ConfirmDialog : Window
    {
        public ConfirmDialog()
        {
            InitializeComponent();
        }

        public ConfirmDialog(string title, string message, string confirmLabel, string? secondLabel = null)
            : this()
        {
            Title = title;
            MessageText.Text = message;
            ConfirmButton.Content = confirmLabel;
            AutomationProperties.SetName(ConfirmButton, confirmLabel);
            if (secondLabel is not null)
            {
                SecondButton.Content = secondLabel;
                SecondButton.IsVisible = true;
                AutomationProperties.SetName(SecondButton, secondLabel);
            }
        }

        private void OnConfirm(object? sender, RoutedEventArgs e) => Close(ConfirmChoice.First);

        private void OnSecond(object? sender, RoutedEventArgs e) => Close(ConfirmChoice.Second);

        private void OnCancel(object? sender, RoutedEventArgs e) => Close(ConfirmChoice.Cancel);
    }
}
