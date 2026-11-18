using Avalonia.Controls;
using Avalonia.Interactivity;
using ChartShared.UI.Views;

namespace ChartBuilder.Views
{
    /// <summary>Manage Layouts (issue 263): adds, renames, reorders, overwrites and deletes the layout templates.</summary>
    public partial class ManageLayoutsWindow : Window
    {
        public ManageLayoutsWindow()
        {
            InitializeComponent();
            WindowChrome.ShowIcon(this, WindowTitleBar);
            Opened += (_, _) =>
            {
                WindowChrome.UseDarkTitleBar(this);
                LayoutList.Focus();
            };
        }

        private void OnClose(object? sender, RoutedEventArgs e) => Close();
    }
}
