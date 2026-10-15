using Avalonia.Controls;
using Avalonia.Interactivity;
using ChartBuilder.ViewModels;
using ChartPacker.UI.Views;

namespace ChartBuilder.Views
{
    /// <summary>The lane mapping a paste between 5-fret and GHL asks for (charter-spec C15 item 3). Closes with the mapping, or null.</summary>
    public partial class LaneMappingWindow : Window
    {
        public LaneMappingWindow()
        {
            InitializeComponent();
            Opened += (_, _) => WindowChrome.UseDarkTitleBar(this);
        }

        private void OnPaste(object? sender, RoutedEventArgs e) => Close((DataContext as LaneMappingViewModel)?.Mapping);

        private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
    }
}
