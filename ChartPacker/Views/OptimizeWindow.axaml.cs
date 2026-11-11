using System;
using Avalonia.Controls;
using ChartPacker.ViewModels;
using ChartShared.UI.Views;

namespace ChartPacker.Views
{
    /// <summary>
    /// The Optimize window. Closing it while an estimate or a run is going stops that first; the window stays until it has.
    /// </summary>
    public partial class OptimizeWindow : Window
    {
        public OptimizeWindow()
        {
            InitializeComponent();
            WindowChrome.ShowIcon(this, WindowTitleBar);
            WindowMemory.Remember(this);
            Opened += (_, _) => WindowChrome.UseDarkTitleBar(this);
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (DataContext is OptimizeWindowViewModel viewModel)
                viewModel.CloseRequested += Close;
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);
            if (DataContext is OptimizeWindowViewModel viewModel && !viewModel.OnClosing())
                e.Cancel = true;
        }
    }
}
