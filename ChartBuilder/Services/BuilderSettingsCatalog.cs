using System;
using System.Globalization;
using System.Linq;
using ChartBuilder.Resources;
using ChartPacker;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Settings;
using ChartPacker.Resources;

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
            var audio = new SettingCategory("audio", Strings.SettingsCategoryAudio);
            var general = new SettingCategory("general", SharedStrings.SettingsCategoryGeneral);
            var logging = new SettingCategory("general.logging", SharedStrings.SettingsCategoryLogging, general);

            return new SettingsCatalog(
                [charter, newCharts, audio, general, logging],
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
                    AppLog.CreateSetting(logging, () => settings.Settings.LoggingEnabled, enabled => settings.Settings.LoggingEnabled = enabled),
                ]);
        }

        /// <summary>A resolution as Settings shows it, e.g. "480 ticks per beat".</summary>
        public static string ResolutionLabel(int resolution) =>
            string.Format(CultureInfo.CurrentCulture, Strings.ChartResolutionFormat, resolution);
    }
}
