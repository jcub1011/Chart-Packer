using Avalonia.Controls;
using Avalonia.Interactivity;
using ChartBuilder.ViewModels;
using ChartShared.UI.Views;

namespace ChartBuilder.Views
{
    /// <summary>A new chart's first save (charter-spec C1). Closes with true once Save is chosen, false otherwise.</summary>
    public partial class FirstSaveWindow : Window
    {
        public FirstSaveWindow()
        {
            InitializeComponent();
            Opened += (_, _) => WindowChrome.UseDarkTitleBar(this);
        }

        protected override void OnDataContextChanged(System.EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (DataContext is FirstSaveViewModel vm)
                vm.Saved += (_, _) => Close(true);
        }

        private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
    }
}
