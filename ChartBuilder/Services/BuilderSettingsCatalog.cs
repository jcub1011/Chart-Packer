using System;
using System.Globalization;
using System.Linq;
using ChartBuilder.Resources;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Editing;
using ChartPacker.Core.Settings;
using ChartPacker.Resources;
using ChartPacker.UI;

namespace ChartBuilder.Services
{
    /// <summary>
    /// Every setting Chart Builder's Settings window shows. To add a setting, add its definition (and its
    /// strings) here; to remove one, delete it.
    /// </summary>
    public static class BuilderSettingsCatalog
    {
        /// <param name="calibrate">Opens the calibration tool, which edits the audio latency (charter-spec C11).</param>
        public static SettingsCatalog Create(BuilderSettingsService settings, Action calibrate)
        {
            var charter = new SettingCategory("charter", Strings.SettingsCategoryCharter);
            var newCharts = new SettingCategory("charter.newCharts", Strings.SettingsCategoryNewCharts, charter);
            var editing = new SettingCategory("charter.editing", Strings.SettingsCategoryEditing, charter);
            var audio = new SettingCategory("audio", Strings.SettingsCategoryAudio);
            var general = new SettingCategory("general", SharedStrings.SettingsCategoryGeneral);
            var logging = new SettingCategory("general.logging", SharedStrings.SettingsCategoryLogging, general);

            return new SettingsCatalog(
                [charter, newCharts, editing, audio, general, logging],
                [
                    // The latency is per device, so it's edited in the calibration tool rather than here (charter-spec §10).
                    new ActionSetting
                    {
                        Id = "audio.latency",
                        Category = audio,
                        Label = Strings.AudioLatencyLabel,
                        Description = Strings.AudioLatencyDescription,
                        Keywords = SettingKeywords.Split(Strings.AudioLatencyKeywords),
                        Actions = [new SettingAction(Strings.CalibrateButton, calibrate)],
                    },
                    new ToggleSetting
                    {
                        Id = "audio.promptForCalibration",
                        Category = audio,
                        Label = Strings.PromptForCalibrationLabel,
                        Description = Strings.PromptForCalibrationDescription,
                        Keywords = SettingKeywords.Split(Strings.PromptForCalibrationKeywords),
                        DefaultValue = true,
                        Get = () => settings.Settings.PromptForCalibration,
                        Set = prompt => settings.Settings.PromptForCalibration = prompt,
                    },
                    new TextSetting
                    {
                        Id = "charter.defaultCharterName",
                        Category = newCharts,
                        Label = Strings.DefaultCharterNameLabel,
                        Description = Strings.DefaultCharterNameDescription,
                        Keywords = SettingKeywords.Split(Strings.DefaultCharterNameKeywords),
                        DefaultValue = "",
                        Placeholder = Strings.DefaultCharterNamePlaceholder,
                        ForbiddenCharacters = ";",
                        Get = () => settings.Settings.DefaultCharterName,
                        Set = name => settings.Settings.DefaultCharterName = name.Trim(),
                    },
                    new ChoiceSetting<int>
                    {
                        Id = "charter.defaultResolution",
                        Category = newCharts,
                        Label = Strings.DefaultChartResolutionLabel,
                        Description = Strings.DefaultChartResolutionDescription,
                        Keywords = SettingKeywords.Split(Strings.DefaultChartResolutionKeywords),
                        DefaultValue = NewChartSettings.DefaultResolution,
                        Choices = [.. NewChartSettings.Resolutions.Select(r => new SettingChoice<int>(r, ResolutionLabel(r)))],
                        Get = () => settings.Settings.DefaultChartResolution,
                        Set = resolution => settings.Settings.DefaultChartResolution = resolution,
                    },
                    new ChoiceSetting<TempoEditMode>
                    {
                        Id = "charter.tempoEdits",
                        Category = editing,
                        Label = Strings.TempoEditsLabel,
                        Description = Strings.TempoEditsDescription,
                        Keywords = SettingKeywords.Split(Strings.TempoEditsKeywords),
                        DefaultValue = TempoEditMode.KeepTicks,
                        Choices =
                        [
                            new SettingChoice<TempoEditMode>(TempoEditMode.KeepTicks, Strings.TempoEditsKeepTicks),
                            new SettingChoice<TempoEditMode>(TempoEditMode.KeepTimes, Strings.TempoEditsKeepTimes),
                        ],
                        Get = () => settings.Settings.TempoEdits,
                        Set = mode => settings.Settings.TempoEdits = mode,
                    },
                    new ChoiceSetting<int>
                    {
                        Id = "charter.defaultSnap",
                        Category = editing,
                        Label = Strings.DefaultSnapLabel,
                        Description = Strings.DefaultSnapDescription,
                        Keywords = SettingKeywords.Split(Strings.DefaultSnapKeywords),
                        DefaultValue = SnapGrid.Default.Division,
                        Choices = [.. SnapGrid.CommonDivisions.Select(d => new SettingChoice<int>(d, SnapLabel(d)))],
                        Get = () => settings.Settings.DefaultSnapDivision,
                        Set = division => settings.Settings.DefaultSnapDivision = division,
                    },
                    new ChoiceSetting<LaneView>
                    {
                        Id = "charter.defaultView",
                        Category = editing,
                        Label = Strings.DefaultEditorViewLabel,
                        Description = Strings.DefaultEditorViewDescription,
                        Keywords = SettingKeywords.Split(Strings.DefaultEditorViewKeywords),
                        DefaultValue = LaneView.Highway,
                        Choices =
                        [
                            new SettingChoice<LaneView>(LaneView.Highway, Strings.EditorViewHighway),
                            new SettingChoice<LaneView>(LaneView.Timeline, Strings.EditorViewTimeline),
                        ],
                        Get = () => settings.Settings.DefaultEditorView,
                        Set = view => settings.Settings.DefaultEditorView = view,
                    },
                    new IntegerSetting
                    {
                        Id = "charter.autosaveMinutes",
                        Category = editing,
                        Label = Strings.AutosaveMinutesLabel,
                        Description = Strings.AutosaveMinutesDescription,
                        Keywords = SettingKeywords.Split(Strings.AutosaveMinutesKeywords),
                        DefaultValue = 2,
                        Minimum = BuilderSettings.MinAutosaveMinutes,
                        Maximum = BuilderSettings.MaxAutosaveMinutes,
                        Get = () => settings.Settings.AutosaveMinutes,
                        Set = minutes => settings.Settings.AutosaveMinutes = minutes,
                    },
                    new RangeSetting
                    {
                        Id = "charter.highwaySpeed",
                        Category = editing,
                        Label = Strings.EditorHighwaySpeedLabel,
                        Description = Strings.EditorHighwaySpeedDescription,
                        Keywords = SettingKeywords.Split(Strings.EditorHighwaySpeedKeywords),
                        DefaultValue = 1,
                        Minimum = BuilderSettings.MinEditorHighwaySpeed,
                        Maximum = BuilderSettings.MaxEditorHighwaySpeed,
                        SmallChange = 0.1,
                        Format = speed => speed.ToString("0.0x", CultureInfo.CurrentCulture),
                        Get = () => settings.Settings.EditorHighwaySpeed,
                        Set = speed => settings.Settings.EditorHighwaySpeed = speed,
                    },
                    Volume("audio.metronomeVolume", audio, Strings.MetronomeVolumeLabel, Strings.MetronomeVolumeDescription, Strings.MetronomeVolumeKeywords,
                        () => settings.Settings.MetronomeVolume, volume => settings.Settings.MetronomeVolume = volume),
                    Volume("audio.hitSoundVolume", audio, Strings.HitSoundVolumeLabel, Strings.HitSoundVolumeDescription, Strings.HitSoundVolumeKeywords,
                        () => settings.Settings.HitSoundVolume, volume => settings.Settings.HitSoundVolume = volume),
                    AppLog.CreateSetting(logging, () => settings.Settings.LoggingEnabled, enabled => settings.Settings.LoggingEnabled = enabled),
                ]);
        }

        /// <summary>A snap as it's shown, e.g. "1/16".</summary>
        public static string SnapLabel(int division) =>
            string.Format(CultureInfo.CurrentCulture, Strings.SnapFormat, division);

        /// <summary>A volume from 0 to 100%, edited with a slider.</summary>
        private static RangeSetting Volume(string id, SettingCategory category, string label, string description, string keywords, Func<int> get, Action<int> set) => new()
        {
            Id = id,
            Category = category,
            Label = label,
            Description = description,
            Keywords = SettingKeywords.Split(keywords),
            DefaultValue = 50,
            Minimum = 0,
            Maximum = 100,
            SmallChange = 5,
            Format = volume => string.Format(CultureInfo.CurrentCulture, Strings.SpeedPercentFormat, (int)Math.Round(volume)),
            Get = () => get(),
            Set = volume => set((int)Math.Round(volume)),
        };

        /// <summary>A resolution as Settings shows it, e.g. "480 ticks per beat".</summary>
        public static string ResolutionLabel(int resolution) =>
            string.Format(CultureInfo.CurrentCulture, Strings.ChartResolutionFormat, resolution);
    }
}
