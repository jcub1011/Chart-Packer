using Avalonia.Controls;

namespace ChartBuilder.Views
{
    /// <summary>
    /// Chart settings (charter-spec C1), a tab in the Editor's left panel: the timing, the parts and their ratings, the
    /// metadata and the tracks. Each change applies as it's made, as an undo step.
    /// </summary>
    public partial class ChartSettingsView : UserControl
    {
        public ChartSettingsView()
        {
            InitializeComponent();
        }
    }
}
