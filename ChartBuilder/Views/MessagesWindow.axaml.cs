using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using ChartBuilder.ViewModels;
using ChartShared.UI.Views;

namespace ChartBuilder.Views
{
    /// <summary>What the Editor has said this session, opened from its message bar.</summary>
    public partial class MessagesWindow : Window
    {
        public MessagesWindow()
        {
            InitializeComponent();
            WindowChrome.ShowIcon(this, WindowTitleBar);
            WindowMemory.Remember(this);
            Opened += (_, _) => WindowChrome.UseDarkTitleBar(this);
        }

        // The clipboard is the window's, so copying is done here rather than in the view model.
        private async void OnCopy(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MessagesDialogViewModel { Selected: { } message } && Clipboard is { } clipboard)
                await clipboard.SetTextAsync(message.ToString());
        }

        private void OnClose(object? sender, RoutedEventArgs e) => Close();
    }
}
