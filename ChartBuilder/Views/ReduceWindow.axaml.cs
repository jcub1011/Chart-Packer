using System;
using Avalonia.Controls;
using ChartBuilder.ViewModels;
using ChartShared.UI.Views;

namespace ChartBuilder.Views
{
    /// <summary>
    /// The Reduce dialog (charter-spec C9 item 2). It closes once its view model is done; closed any other way, a run is
    /// cancelled, adding nothing.
    /// </summary>
    public partial class ReduceWindow : Window
    {
        public ReduceWindow()
        {
            InitializeComponent();
            Opened += (_, _) => WindowChrome.UseDarkTitleBar(this);
            Closed += (_, _) => (DataContext as ReduceViewModel)?.Cancel();
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (DataContext is ReduceViewModel vm)
                vm.Done += (_, _) => Close();
        }
    }
}
