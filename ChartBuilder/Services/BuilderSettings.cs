using ChartPacker.Core.Charting;

namespace ChartBuilder.Services
{
    /// <summary>Chart Builder's settings, persisted to <c>builder-settings.json</c> (charter-spec §10).</summary>
    /// <remarks>Chart Packer's are in its own file, so neither app overwrites the other's.</remarks>
    public sealed class BuilderSettings
    {
        /// <summary>The name the new-chart wizard fills <c>charter</c> in with. Empty for none.</summary>
        public string DefaultCharterName { get; set; } = "";

        /// <summary>The resolution, in ticks per beat, the new-chart wizard starts on: one of <see cref="NewChartSettings.Resolutions"/>.</summary>
        public int DefaultChartResolution { get; set; } = NewChartSettings.DefaultResolution;

        /// <summary>The folder the last new chart was made in, which the wizard starts on. Null for the folder it's given.</summary>
        public string? LastNewChartFolder { get; set; }
    }
}
