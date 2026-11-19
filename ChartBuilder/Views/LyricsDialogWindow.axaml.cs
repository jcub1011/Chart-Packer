using Avalonia.Controls;
using Avalonia.Interactivity;
using ChartShared.UI.Views;

namespace ChartBuilder.Views
{
    /// <summary>The options of Import lyrics and Export lyrics (issue 270). Closes with whether it was confirmed.</summary>
    public partial class LyricsDialogWindow : Window
    {
        public LyricsDialogWindow()
        {
            InitializeComponent();
            WindowChrome.ShowIcon(this, WindowTitleBar);
            Opened += (_, _) => WindowChrome.UseDarkTitleBar(this);
        }

        private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);

        private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
    }
}
