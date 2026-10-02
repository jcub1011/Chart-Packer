using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using ChartPacker.Services;
using ChartPacker.ViewModels;

namespace ChartPacker.Views
{
    /// <summary>
    /// The Merge window. Cancel, Escape or closing the window stops a merge that's running; once a
    /// song is merged the button closes it.
    /// </summary>
    public partial class MergeWindow : Window
    {
        private ColumnDefinition ChoicesColumn => MergeRoot.ColumnDefinitions[0];

        private ColumnDefinition PreviewColumn => MergeRoot.ColumnDefinitions[2];

        public MergeWindow()
        {
            InitializeComponent();
            // The splitter position and maximized state are view state, so they're applied and captured here rather than bound.
            Opened += (_, _) =>
            {
                WindowChrome.UseDarkTitleBar(this);
                RestoreWindowState();
            };
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (DataContext is MergeWindowViewModel viewModel)
            {
                viewModel.CloseRequested += _ => Close();
                viewModel.CompareRequested += compare => _ = ShowCompareAsync(compare);
            }
        }

        private async Task ShowCompareAsync(MergeCompareViewModel compare)
        {
            var dialog = new MergeCompareWindow { DataContext = compare };
            await dialog.ShowDialog(this);
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            if (DataContext is MergeWindowViewModel viewModel)
                CaptureWindowState(viewModel);

            base.OnClosing(e);
        }

        private void CaptureWindowState(MergeWindowViewModel viewModel)
        {
            viewModel.MergeWindowMaximized = WindowState == WindowState.Maximized;

            double left = ChoicesColumn.ActualWidth;
            double right = PreviewColumn.ActualWidth;
            double total = left + right;
            if (double.IsFinite(total) && total > 0)
            {
                double fraction = right / total;
                if (double.IsFinite(fraction))
                    viewModel.MergePreviewFraction = Math.Clamp(fraction, AppSettings.MinMergePreviewFraction, AppSettings.MaxMergePreviewFraction);
            }

            viewModel.OnClosed();
        }

        private void RestoreWindowState()
        {
            if (DataContext is not MergeWindowViewModel viewModel)
                return;

            if (viewModel.MergeWindowMaximized)
                WindowState = WindowState.Maximized;

            double fraction = viewModel.MergePreviewFraction;
            if (!double.IsFinite(fraction))
                return;

            fraction = Math.Clamp(fraction, AppSettings.MinMergePreviewFraction, AppSettings.MaxMergePreviewFraction);
            ChoicesColumn.Width = new GridLength(1 - fraction, GridUnitType.Star);
            PreviewColumn.Width = new GridLength(fraction, GridUnitType.Star);
        }
    }
}
