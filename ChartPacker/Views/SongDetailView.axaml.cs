using Avalonia.Controls;
using Avalonia.Interactivity;
using ChartPacker.ViewModels;

namespace ChartPacker.Views
{
    public partial class SongDetailView : UserControl
    {
        public SongDetailView()
        {
            InitializeComponent();
        }

        /// <summary>Clicking away from a value's edit box keeps what was typed, like pressing Enter.</summary>
        private void OnEditLostFocus(object? sender, RoutedEventArgs e)
        {
            if (sender is Control { DataContext: MetadataRowViewModel row })
                row.CommitCommand.Execute(null);
        }
    }
}
