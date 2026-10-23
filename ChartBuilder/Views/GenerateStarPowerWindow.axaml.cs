using Avalonia.Controls;
using Avalonia.Interactivity;
using ChartShared.UI.Views;

namespace ChartBuilder.Views
{
    /// <summary>Generate star power's options (charter-spec C8 item 1). Closes with whether Generate was pressed.</summary>
    public partial class GenerateStarPowerWindow : Window
    {
        public GenerateStarPowerWindow()
        {
            InitializeComponent();
            Opened += (_, _) => WindowChrome.UseDarkTitleBar(this);
        }

        private void OnGenerate(object? sender, RoutedEventArgs e) => Close(true);

        private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
    }
}
