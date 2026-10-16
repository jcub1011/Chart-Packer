using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ChartShared.UI.Views
{
    /// <summary>A chart's findings, searched, filtered and in sections by how serious they are, in a window of their own.</summary>
    public partial class ChartIssuesWindow : Window
    {
        public ChartIssuesWindow()
        {
            InitializeComponent();
            WindowChrome.ShowIcon(this, WindowTitleBar);
            Opened += (_, _) => WindowChrome.UseDarkTitleBar(this);
        }

        private void OnClose(object? sender, RoutedEventArgs e) => Close();
    }
}
