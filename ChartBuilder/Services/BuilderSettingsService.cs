using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ChartShared.Core;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charting.Playtest;
using ChartShared.Core.Launching;
using ChartShared.Core.Settings;
using ChartShared.UI.Services;
using ChartShared.UI.ViewModels;
using ChartShared.UI.Views;

namespace ChartBuilder.Services
{
    /// <summary>Loads and saves <see cref="BuilderSettings"/> as JSON, through <see cref="JsonSettingsFile{T}"/>.</summary>
    /// <remarks>
    /// The file path is always passed in, never defaulted, so nothing but the composition root
    /// (which passes <see cref="ChartShared.Core.UserConfigPaths.BuilderSettingsFile"/>) can touch the real
    /// settings. Tests must pass a temp file. Several Chart Builders can run at once, and the file keeps each
    /// one's changes.
    /// </remarks>
    public sealed class BuilderSettingsService
    {
        private readonly JsonSettingsFile<BuilderSettings> _file;

        private BuilderSettingsService(JsonSettingsFile<BuilderSettings> file, BuilderSettings settings, ReductionRulesFile reductionRules)
        {
            _file = file;
            Settings = settings;
            ReductionRules = reductionRules;
        }

        public BuilderSettings Settings { get; }

        /// <summary>
        /// The default rules Reduce starts from (charter-spec C9 item 2, §10): <c>reduction-rules.json</c> beside the settings
        /// file, which Chart Packer reads and writes too.
        /// </summary>
        public ReductionRulesFile ReductionRules { get; }

        /// <summary>Loads settings from <paramref name="filePath"/>, falling back to defaults when it's missing or unreadable.</summary>
        public static BuilderSettingsService Load(string filePath)
        {
            var file = new JsonSettingsFile<BuilderSettings>(filePath, BuilderSettingsJsonContext.Default.BuilderSettings);
            var settings = file.Load();
            Sanitize(settings);
            string rules = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? "", Path.GetFileName(UserConfigPaths.ReductionRulesFile));
            return new BuilderSettingsService(file, settings, new ReductionRulesFile(rules, filePath));
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
            if (!SnapGrid.CommonDivisions.Contains(settings.QuantizeDivision))
                settings.QuantizeDivision = BuilderSettings.DefaultQuantizeDivision;
            if (!Enum.IsDefined(settings.DefaultEditorView))
                settings.DefaultEditorView = LaneView.Highway;
            if (!Enum.IsDefined(settings.WheelScrollDirection))
                settings.WheelScrollDirection = WheelScrollDirection.UpIsLater;
            settings.EditorHighwaySpeed = double.IsFinite(settings.EditorHighwaySpeed)
                ? Math.Clamp(settings.EditorHighwaySpeed, BuilderSettings.MinEditorHighwaySpeed, BuilderSettings.MaxEditorHighwaySpeed)
                : 1;
            settings.AutosaveMinutes = Math.Clamp(settings.AutosaveMinutes, BuilderSettings.MinAutosaveMinutes, BuilderSettings.MaxAutosaveMinutes);
            settings.LastTranscodeBitrateKbps = Math.Clamp(settings.LastTranscodeBitrateKbps, 32, 512);
            settings.MasterVolume = double.IsFinite(settings.MasterVolume)
                ? Math.Clamp(settings.MasterVolume, 0, 1)
                : BuilderSettings.DefaultMasterVolume;
            settings.MetronomeVolume = Math.Clamp(settings.MetronomeVolume, 0, 100);
            settings.HitSoundVolume = Math.Clamp(settings.HitSoundVolume, 0, 100);
            settings.PreviewLookAhead = double.IsFinite(settings.PreviewLookAhead) ? Math.Clamp(settings.PreviewLookAhead, 0.5, 4) : 1.4;
            settings.ReducePreviewCategories &= ReductionChangeCategories.All;
            settings.ReducePreviewLookAhead = BuilderSettings.SnapReducePreviewLookAhead(settings.ReducePreviewLookAhead);
            settings.PlaytestEarlyMs = Math.Clamp(settings.PlaytestEarlyMs, (int)HitWindow.MinMs, (int)HitWindow.MaxMs);
            settings.PlaytestLateMs = Math.Clamp(settings.PlaytestLateMs, (int)HitWindow.MinMs, (int)HitWindow.MaxMs);
            if (!Enum.IsDefined(settings.GuitarRecordStyle))
                settings.GuitarRecordStyle = GuitarRecordStyle.Strum;

            settings.EditorLeftPanelWidth = Size(settings.EditorLeftPanelWidth, BuilderSettings.MinEditorPanelWidth, BuilderSettings.DefaultEditorLeftPanelWidth);
            settings.EditorRightPanelWidth = Size(settings.EditorRightPanelWidth, BuilderSettings.MinEditorPanelWidth, BuilderSettings.DefaultEditorRightPanelWidth);
            settings.EditorTimelineHeight = Size(settings.EditorTimelineHeight, BuilderSettings.MinEditorTimelineHeight, BuilderSettings.DefaultEditorTimelineHeight);
            SanitizePanelLayout(settings);

            if (settings.EditorFullscreen)
                settings.EditorMaximized = true;
            SanitizeEditorBounds(settings);
            SettingsWindowSize.Sanitize(settings);
        }

        /// <summary>
        /// A saved panel size, from <paramref name="min"/> to <see cref="BuilderSettings.MaxEditorPanelSize"/>;
        /// <paramref name="fallback"/> when it isn't a number.
        /// </summary>
        private static double Size(double value, double min, double fallback) =>
            double.IsFinite(value) ? Math.Clamp(value, min, BuilderSettings.MaxEditorPanelSize) : fallback;

        /// <summary>
        /// The side panels' auto-hide, from the collapsed flags of files saved before it, and their tabs' and sections'
        /// layout, with blank and repeated keys dropped.
        /// </summary>
        private static void SanitizePanelLayout(BuilderSettings settings)
        {
            settings.EditorLeftPanelAutoHide ??= settings.EditorLeftPanelCollapsed;
            settings.EditorRightPanelAutoHide ??= settings.EditorRightPanelCollapsed;
            settings.EditorLeftTabs = Keys(settings.EditorLeftTabs);
            settings.EditorRightTabs = Keys(settings.EditorRightTabs);
            settings.EditorCollapsedSections = Keys(settings.EditorCollapsedSections);
            if (settings.EditorSectionOrder is { } orders)
            {
                var kept = new Dictionary<string, List<string>>();
                foreach (var (tab, order) in orders.Take(PanelLayoutStore.MaxKeys))
                {
                    if (!string.IsNullOrWhiteSpace(tab) && Keys(order) is { } keys)
                        kept[tab] = keys;
                }
                settings.EditorSectionOrder = kept.Count > 0 ? kept : null;
            }
        }

        // A saved list of layout keys, without blanks or repeats; null when nothing's left.
        private static List<string>? Keys(List<string>? keys)
        {
            if (keys is null)
                return null;
            var kept = keys.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct().Take(PanelLayoutStore.MaxKeys).ToList();
            return kept.Count > 0 ? kept : null;
        }

        /// <summary>The editor's saved normal bounds, as <see cref="WindowBounds.FromSaved"/> reads them: dropped whole when half-saved.</summary>
        private static void SanitizeEditorBounds(BuilderSettings settings)
        {
            var bounds = WindowBounds.FromSaved(settings.EditorX, settings.EditorY, settings.EditorWidth, settings.EditorHeight);
            settings.EditorX = bounds?.Position.X;
            settings.EditorY = bounds?.Position.Y;
            settings.EditorWidth = bounds?.Size.Width;
            settings.EditorHeight = bounds?.Size.Height;
        }
    }
}
