using ChartShared.UI.ViewModels;

namespace ChartPacker.Services
{
    /// <summary>
    /// The Generate lower difficulties window's preview's settings: its look-ahead is kept in <see cref="AppSettings"/>,
    /// as Chart Builder keeps its Reduce preview's, and whether it's shown and its filters are kept for as long as Chart
    /// Packer runs.
    /// </summary>
    public sealed class PackerReductionPreviewSettings(SettingsService settings) : IReductionPreviewSettings
    {
        public bool IsShown { get; set; } = true;

        public double LookAhead
        {
            get => settings.Settings.GenerateDifficultiesLookAhead;
            set => settings.Settings.GenerateDifficultiesLookAhead = value;
        }

        public ReductionChangeCategories Categories { get; set; } = ReductionChangeCategories.Default;
    }
}
