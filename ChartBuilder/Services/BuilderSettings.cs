using ChartPacker.Core.Charting;

namespace ChartBuilder.Services
{
    /// <summary>Chart Builder's settings, persisted to <c>builder-settings.json</c> (charter-spec §10).</summary>
    /// <remarks>Chart Packer's are in its own file, so neither app overwrites the other's.</remarks>
    public sealed class BuilderSettings
    {
        public const double DefaultEditorLeftPanelWidth = 300;
        public const double DefaultEditorRightPanelWidth = 280;
        public const double DefaultEditorTimelineHeight = 160;

        /// <summary>The narrowest a side panel can be dragged, and the least a saved width is restored as.</summary>
        public const double MinEditorPanelWidth = 180;

        /// <summary>The shortest the timeline can be dragged, and the least a saved height is restored as.</summary>
        public const double MinEditorTimelineHeight = 80;

        /// <summary>The most a saved panel width or timeline height is restored as: wider than an 8K screen.</summary>
        public const double MaxEditorPanelSize = 8192;

        /// <summary>The name a new chart's <c>charter</c> is filled in with. Empty for none.</summary>
        public string DefaultCharterName { get; set; } = "";

        /// <summary>The resolution, in ticks per beat, a new chart starts on: one of <see cref="NewChartSettings.Resolutions"/>.</summary>
        public int DefaultChartResolution { get; set; } = NewChartSettings.DefaultResolution;

        /// <summary>The editor's left panel (Chart Inspector) width, while expanded.</summary>
        public double EditorLeftPanelWidth { get; set; } = DefaultEditorLeftPanelWidth;

        /// <summary>The editor's right panel (Properties) width, while expanded.</summary>
        public double EditorRightPanelWidth { get; set; } = DefaultEditorRightPanelWidth;

        /// <summary>The height of the timeline along the bottom of the editor.</summary>
        public double EditorTimelineHeight { get; set; } = DefaultEditorTimelineHeight;

        /// <summary>Whether the editor's left panel is collapsed to a strip along the window's edge.</summary>
        public bool EditorLeftPanelCollapsed { get; set; }

        /// <summary>Whether the editor's right panel is collapsed to a strip along the window's edge.</summary>
        public bool EditorRightPanelCollapsed { get; set; }
    }
}
