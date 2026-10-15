using Avalonia.Controls;
using ChartShared.UI.Views;

namespace ChartBuilder.Views
{
    /// <summary>Shows how far Save and pack has got: transcoding each stem, then packing. Closing it cancels the export.</summary>
    public partial class ExportProgressWindow : Window
    {
        public ExportProgressWindow()
        {
            InitializeComponent();
            Opened += (_, _) => WindowChrome.UseDarkTitleBar(this);
        }
    }
}
