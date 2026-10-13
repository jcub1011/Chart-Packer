using System;
using System.Linq;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Authoring;
using ChartPacker.Core.Charting.Editing;
using ChartPacker.Core.Charting.Playtest;
using ChartPacker.Core.Launching;
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
            if (string.IsNullOrWhiteSpace(settings.LastNewChartFolder))
                settings.LastNewChartFolder = null;
            if (string.IsNullOrWhiteSpace(settings.DefaultPackFolder))
                settings.DefaultPackFolder = null;
            if (string.IsNullOrWhiteSpace(settings.LastExportFolder))
                settings.LastExportFolder = null;
            if (settings.LastExportPolicy is not null && !Enum.IsDefined(settings.LastExportPolicy.Value))
                settings.LastExportPolicy = null;
            settings.CalibrationPrompted = [.. (settings.CalibrationPrompted ?? []).Where(d => !string.IsNullOrEmpty(d)).Distinct()];
            if (!Enum.IsDefined(settings.StartupAction))
                settings.StartupAction = StartupAction.BlankChart;
            settings.RecentSongs = [.. (settings.RecentSongs ?? []).Where(s => s is not null && !string.IsNullOrWhiteSpace(s.Path)).Take(RecentSongs.Max)];
            foreach (var song in settings.RecentSongs)
                song.Title ??= "";

            if (!Enum.IsDefined(settings.TempoEdits))
                settings.TempoEdits = TempoEditMode.KeepTicks;

            if (!NewChartSettings.Resolutions.Contains(settings.DefaultChartResolution))
                settings.DefaultChartResolution = NewChartSettings.DefaultResolution;

            if (!SnapGrid.CommonDivisions.Contains(settings.DefaultSnapDivision))
                settings.DefaultSnapDivision = SnapGrid.Default.Division;
            if (!Enum.IsDefined(settings.DefaultEditorView))
                settings.DefaultEditorView = LaneView.Highway;
            settings.EditorHighwaySpeed = double.IsFinite(settings.EditorHighwaySpeed)
                ? Math.Clamp(settings.EditorHighwaySpeed, BuilderSettings.MinEditorHighwaySpeed, BuilderSettings.MaxEditorHighwaySpeed)
                : 1;
            settings.AutosaveMinutes = Math.Clamp(settings.AutosaveMinutes, BuilderSettings.MinAutosaveMinutes, BuilderSettings.MaxAutosaveMinutes);
            settings.LastTranscodeBitrateKbps = Math.Clamp(settings.LastTranscodeBitrateKbps, 32, 512);            settings.MetronomeVolume = Math.Clamp(settings.MetronomeVolume, 0, 100);
            settings.HitSoundVolume = Math.Clamp(settings.HitSoundVolume, 0, 100);
            settings.ReductionRules = (settings.ReductionRules ?? ReductionDefaults.Shipped).Normalized();
            settings.PreviewLookAhead = double.IsFinite(settings.PreviewLookAhead) ? Math.Clamp(settings.PreviewLookAhead, 0.5, 4) : 1.4;
            settings.PlaytestEarlyMs = Math.Clamp(settings.PlaytestEarlyMs, (int)HitWindow.MinMs, (int)HitWindow.MaxMs);
            settings.PlaytestLateMs = Math.Clamp(settings.PlaytestLateMs, (int)HitWindow.MinMs, (int)HitWindow.MaxMs);
            if (!Enum.IsDefined(settings.GuitarRecordStyle))
                settings.GuitarRecordStyle = GuitarRecordStyle.Strum;

            settings.EditorLeftPanelWidth = Size(settings.EditorLeftPanelWidth, BuilderSettings.MinEditorPanelWidth, BuilderSettings.DefaultEditorLeftPanelWidth);
            settings.EditorRightPanelWidth = Size(settings.EditorRightPanelWidth, BuilderSettings.MinEditorPanelWidth, BuilderSettings.DefaultEditorRightPanelWidth);
            settings.EditorTimelineHeight = Size(settings.EditorTimelineHeight, BuilderSettings.MinEditorTimelineHeight, BuilderSettings.DefaultEditorTimelineHeight);

            if (settings.EditorFullscreen)
                settings.EditorMaximized = true;
            SanitizeEditorBounds(settings);
        }

        /// <summary>
        /// A saved panel size, from <paramref name="min"/> to <see cref="BuilderSettings.MaxEditorPanelSize"/>;
        /// <paramref name="fallback"/> when it isn't a number.
        /// </summary>
        private static double Size(double value, double min, double fallback) =>
            double.IsFinite(value) ? Math.Clamp(value, min, BuilderSettings.MaxEditorPanelSize) : fallback;

        /// <summary>
        /// A saved editor normal size, clamped to the window's minimum and maximum; null when it isn't a
        /// number. A bound missing any part restores none of it, so a half-saved bound is dropped whole.
        /// </summary>
        private static void SanitizeEditorBounds(BuilderSettings settings)
        {
            double? width = settings.EditorWidth is { } w && double.IsFinite(w)
                ? Math.Clamp(w, BuilderSettings.MinEditorWidth, BuilderSettings.MaxEditorWidth)
                : null;
            double? height = settings.EditorHeight is { } h && double.IsFinite(h)
                ? Math.Clamp(h, BuilderSettings.MinEditorHeight, BuilderSettings.MaxEditorHeight)
                : null;
            if (width is null || height is null || settings.EditorX is null || settings.EditorY is null)
            {
                settings.EditorX = null;
                settings.EditorY = null;
                settings.EditorWidth = null;
                settings.EditorHeight = null;
                return;
            }

            settings.EditorWidth = width;
            settings.EditorHeight = height;
        }
    }
}
