using Avalonia.Controls;
using Avalonia.Interactivity;
using ChartShared.UI.Views;

namespace ChartBuilder.Views
{
    /// <summary>Generate fills' options (charter-spec C6 item 5). Closes with whether Generate was pressed.</summary>
    public partial class GenerateFillsWindow : Window
    {
        public GenerateFillsWindow()
        {
            InitializeComponent();
            Opened += (_, _) => WindowChrome.UseDarkTitleBar(this);
        }

        private void OnGenerate(object? sender, RoutedEventArgs e) => Close(true);

        private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
    }
}
