using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using ChartShared.UI.Controls;
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
            WindowChrome.ShowIcon(this, WindowTitleBar);
            Opened += (_, _) => WindowChrome.UseDarkTitleBar(this);
            SetAction(CancelButton, SharedStrings.CancelButton, "ClearStroke");
        }

        /// <param name="confirmIcon">The line icon beside the first action, by its key; null for none.</param>
        /// <param name="secondIcon">The line icon beside the second action, by its key; null for none.</param>
        public ConfirmDialog(string title, string message, string confirmLabel, string? secondLabel = null, string? confirmIcon = null, string? secondIcon = null)
            : this()
        {
            Title = title;
            TitleText.Text = title;
            MessageText.Text = message;
            SetAction(ConfirmButton, confirmLabel, confirmIcon);
            if (secondLabel is not null)
            {
                SetAction(SecondButton, secondLabel, secondIcon);
                SecondButton.IsVisible = true;
            }
        }

        /// <summary>A message with only OK, which Enter and Escape choose too.</summary>
        public static ConfirmDialog Alert(string title, string message)
        {
            var dialog = new ConfirmDialog(title, message, SharedStrings.OkButton, confirmIcon: "CheckStroke");
            dialog.CancelButton.IsVisible = false;
            dialog.ConfirmButton.Classes.Add("accent");
            dialog.ConfirmButton.IsDefault = true;
            dialog.ConfirmButton.IsCancel = true;
            return dialog;
        }

        // An action's label, with its icon before it when it has one.
        private static void SetAction(Button button, string label, string? icon)
        {
            AutomationProperties.SetName(button, label);
            if (LineIcon.For(icon) is not { } glyph)
            {
                button.Content = label;
                return;
            }

            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(glyph);
            content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            button.Content = content;
        }

        private void OnConfirm(object? sender, RoutedEventArgs e) => Close(ConfirmChoice.First);

        private void OnSecond(object? sender, RoutedEventArgs e) => Close(ConfirmChoice.Second);

        private void OnCancel(object? sender, RoutedEventArgs e) => Close(ConfirmChoice.Cancel);
    }
}
