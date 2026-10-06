using System;
using System.Linq;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Settings;

namespace ChartBuilder.Services
{
    /// <summary>Loads and saves <see cref="BuilderSettings"/> as JSON, through <see cref="JsonSettingsFile{T}"/>.</summary>
    /// <remarks>
    /// The file path is always passed in, never defaulted, so nothing but the composition root
    /// (which passes <see cref="ChartPacker.Core.UserConfigPaths.BuilderSettingsFile"/>) can touch the real
    /// settings. Tests must pass a temp file. Several Chart Builders can run at once, and the file keeps each
    /// one's changes.
    /// </remarks>
    public sealed class BuilderSettingsService
    {
        private readonly JsonSettingsFile<BuilderSettings> _file;

        private BuilderSettingsService(JsonSettingsFile<BuilderSettings> file, BuilderSettings settings)
        {
            _file = file;
            Settings = settings;
        }

        public BuilderSettings Settings { get; }

        /// <summary>Loads settings from <paramref name="filePath"/>, falling back to defaults when it's missing or unreadable.</summary>
        public static BuilderSettingsService Load(string filePath)
        {
            var file = new JsonSettingsFile<BuilderSettings>(filePath, BuilderSettingsJsonContext.Default.BuilderSettings);
            var settings = file.Load();
            Sanitize(settings);
            return new BuilderSettingsService(file, settings);
        }

        /// <summary>Writes the settings changed in this run to the file, keeping other processes' changes to the rest.</summary>
        public void Save() => _file.Save(Settings);

        private static void Sanitize(BuilderSettings settings)
        {
            settings.DefaultCharterName ??= "";

            if (!NewChartSettings.Resolutions.Contains(settings.DefaultChartResolution))
                settings.DefaultChartResolution = NewChartSettings.DefaultResolution;

            settings.EditorLeftPanelWidth = Size(settings.EditorLeftPanelWidth, BuilderSettings.MinEditorPanelWidth, BuilderSettings.DefaultEditorLeftPanelWidth);
            settings.EditorRightPanelWidth = Size(settings.EditorRightPanelWidth, BuilderSettings.MinEditorPanelWidth, BuilderSettings.DefaultEditorRightPanelWidth);
            settings.EditorTimelineHeight = Size(settings.EditorTimelineHeight, BuilderSettings.MinEditorTimelineHeight, BuilderSettings.DefaultEditorTimelineHeight);
        }

        /// <summary>A saved panel size, at least <paramref name="min"/>; <paramref name="fallback"/> when it isn't a number.</summary>
        private static double Size(double value, double min, double fallback) =>
            double.IsFinite(value) ? Math.Max(value, min) : fallback;
    }
}
