using Avalonia.Controls;
using ChartShared.UI.Views;

namespace ChartBuilder.Views
{
    /// <summary>Shows how far Pack has got: transcoding each stem, then packing. Closing it cancels the export.</summary>
    public partial class ExportProgressWindow : Window
    {
        public ExportProgressWindow()
        {
            InitializeComponent();
            WindowChrome.ShowIcon(this, WindowTitleBar);
            Opened += (_, _) => WindowChrome.UseDarkTitleBar(this);
        }
    }
}
