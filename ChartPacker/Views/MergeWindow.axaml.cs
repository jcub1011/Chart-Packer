using System;
using Avalonia.Controls;
using ChartPacker.ViewModels;

namespace ChartPacker.Views
{
    /// <summary>
    /// The Merge window. Cancel, Escape or closing the window stops a merge that's running; once a
    /// song is merged the button closes it.
    /// </summary>
    public partial class MergeWindow : Window
    {
        public MergeWindow()
        {
            InitializeComponent();
            Opened += (_, _) => WindowChrome.UseDarkTitleBar(this);
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (DataContext is MergeWindowViewModel viewModel)
                viewModel.CloseRequested += _ => Close();
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);
            (DataContext as MergeWindowViewModel)?.OnClosed();
        }
    }
}
