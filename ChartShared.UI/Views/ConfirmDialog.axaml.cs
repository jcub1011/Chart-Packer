using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ChartShared.UI.Resources;
using ChartShared.UI.Services;

namespace ChartShared.UI.Views
{
    /// <summary>
    /// A modal prompt with one or two actions and Cancel. Closes with <see cref="ConfirmChoice.First"/> or
    /// <see cref="ConfirmChoice.Second"/> for the action chosen, <see cref="ConfirmChoice.Cancel"/> otherwise. An
    /// <see cref="Alert"/> has only OK.
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

        /// <summary>A message with only OK, which Enter and Escape choose too.</summary>
        public static ConfirmDialog Alert(string title, string message)
        {
            var dialog = new ConfirmDialog(title, message, SharedStrings.OkButton);
            dialog.CancelButton.IsVisible = false;
            dialog.ConfirmButton.Classes.Add("accent");
            dialog.ConfirmButton.IsDefault = true;
            dialog.ConfirmButton.IsCancel = true;
            return dialog;
        }

        private void OnConfirm(object? sender, RoutedEventArgs e) => Close(ConfirmChoice.First);

        private void OnSecond(object? sender, RoutedEventArgs e) => Close(ConfirmChoice.Second);

        private void OnCancel(object? sender, RoutedEventArgs e) => Close(ConfirmChoice.Cancel);
    }
}
