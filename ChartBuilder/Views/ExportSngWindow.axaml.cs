using Avalonia.Controls;
using Avalonia.Interactivity;
using ChartBuilder.ViewModels;
using ChartPacker.UI.Views;

namespace ChartBuilder.Views
{
    /// <summary>Export to <c>.sng</c>: preview, location and if-exists choice. Closes with true once Export is chosen, false otherwise.</summary>
    public partial class ExportSngWindow : Window
    {
        public ExportSngWindow()
        {
            InitializeComponent();
            Opened += (_, _) => WindowChrome.UseDarkTitleBar(this);
        }

        protected override void OnDataContextChanged(System.EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (DataContext is ExportSngViewModel vm)
                vm.Exported += (_, _) => Close(true);
        }

        private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
    }
}
