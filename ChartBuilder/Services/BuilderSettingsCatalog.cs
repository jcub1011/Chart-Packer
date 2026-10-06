using System.Globalization;
using System.Linq;
using ChartBuilder.Resources;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Settings;

namespace ChartBuilder.Services
{
    /// <summary>
    /// Every setting Chart Builder's Settings window shows. To add a setting, add its definition (and its
    /// strings) here; to remove one, delete it.
    /// </summary>
    public static class BuilderSettingsCatalog
    {
        public static SettingsCatalog Create(BuilderSettingsService settings)
        {
            var charter = new SettingCategory("charter", Strings.SettingsCategoryCharter);
            var newCharts = new SettingCategory("charter.newCharts", Strings.SettingsCategoryNewCharts, charter);

            return new SettingsCatalog(
                [charter, newCharts],
                [
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
                ]);
        }

        /// <summary>A resolution as the new-chart wizard and Settings show it, e.g. "480 ticks per beat".</summary>
        public static string ResolutionLabel(int resolution) =>
            string.Format(CultureInfo.CurrentCulture, Strings.ChartResolutionFormat, resolution);
    }
}
