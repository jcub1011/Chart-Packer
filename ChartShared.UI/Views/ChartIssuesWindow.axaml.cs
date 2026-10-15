using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ChartShared.UI.Views
{
    /// <summary>A chart's findings, grouped by where they are, in a window of their own.</summary>
    public partial class ChartIssuesWindow : Window
    {
        public ChartIssuesWindow()
        {
            InitializeComponent();
            Opened += (_, _) => WindowChrome.UseDarkTitleBar(this);
        }

        private void OnClose(object? sender, RoutedEventArgs e) => Close();
    }
}
