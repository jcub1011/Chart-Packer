using System;
using Avalonia.Controls;
using ChartPacker.ViewModels;
using ChartShared.UI.Views;

namespace ChartPacker.Views
{
    /// <summary>
    /// The Deduplicate window. Closing it stops the comparison; a deletion that's running stops after
    /// the copy it's on.
    /// </summary>
    public partial class DedupWindow : Window
    {
        public DedupWindow()
        {
            InitializeComponent();
            WindowChrome.ShowIcon(this, WindowTitleBar);
            Opened += (_, _) => WindowChrome.UseDarkTitleBar(this);
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (DataContext is DedupWindowViewModel viewModel)
                viewModel.CloseRequested += Close;
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);
            (DataContext as DedupWindowViewModel)?.OnClosed();
        }
    }
}
