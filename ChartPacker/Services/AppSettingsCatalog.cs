using System;
using System.Globalization;
using System.Linq;
using ChartPacker.Core.Jobs;
using ChartPacker.Core.Settings;
using ChartPacker.Resources;
using ChartPacker.ViewModels;

namespace ChartPacker.Services
{
    /// <summary>
    /// Every setting the Settings window shows. To add a setting, add its definition (and its strings) here;
    /// to remove one, delete it.
    /// </summary>
    /// <remarks>
    /// Settings are read and written through the view models that own the live values rather than through
    /// <see cref="AppSettings"/> directly. The view models already persist a change and refresh what depends
    /// on it, so the toolbar and player stay in step with the window. Window layout (pane sizes) isn't listed.
    /// </remarks>
    public static class AppSettingsCatalog
    {
        public static SettingsCatalog Create(LibraryViewModel library, PlayerViewModel player)
        {
            var libraryCategory = new SettingCategory("library", Strings.SettingsCategoryLibrary);
            var scanning = new SettingCategory("library.scanning", Strings.SettingsCategoryScanning, libraryCategory);
            var output = new SettingCategory("output", Strings.SettingsCategoryOutput);
            var packing = new SettingCategory("output.packing", Strings.SettingsCategoryPacking, output);
            var playback = new SettingCategory("playback", Strings.SettingsCategoryPlayback);
            var audio = new SettingCategory("playback.audio", Strings.SettingsCategoryAudio, playback);
            var visualizers = new SettingCategory("playback.visualizers", Strings.SettingsCategoryVisualizers, playback);

            return new SettingsCatalog(
                [libraryCategory, scanning, output, packing, playback, audio, visualizers],
                [
                    new IntegerSetting
                    {
                        Id = "library.scanDepth",
                        Category = scanning,
                        Label = Strings.ScanDepthLabel,
                        Description = Strings.ScanDepthDescription,
                        Keywords = Words(Strings.ScanDepthKeywords),
                        DefaultValue = AppSettings.DefaultScanMaxDepth,
                        Minimum = 0,
                        Maximum = AppSettings.MaxScanMaxDepth,
                        Get = () => library.ScanDepth is { } depth ? (int)depth : AppSettings.DefaultScanMaxDepth,
                        Set = depth => library.ScanDepth = depth,
                    },
                    new FolderSetting
                    {
                        Id = "output.folder",
                        Category = packing,
                        Label = Strings.OutputFolderLabel,
                        Description = Strings.OutputFolderDescription,
                        Keywords = Words(Strings.OutputFolderKeywords),
                        DefaultValue = null,
                        EmptyLabel = Strings.OutputFolderEmpty,
                        PickerTitle = Strings.BrowseOutputDialogTitle,
                        Get = () => library.OutputFolder,
                        Set = folder => library.OutputFolder = folder,
                    },
                    new ChoiceSetting<OverwritePolicy>
                    {
                        Id = "output.overwritePolicy",
                        Category = packing,
                        Label = Strings.OverwriteLabel,
                        Description = Strings.OverwriteToolTip,
                        Keywords = Words(Strings.OverwriteKeywords),
                        DefaultValue = AppSettings.DefaultOverwritePolicy,
                        Choices = [.. library.OverwriteOptions.Select(o => new SettingChoice<OverwritePolicy>(o.Value, o.Label))],
                        Get = () => library.SelectedOverwriteOption.Value,
                        Set = policy => library.SelectedOverwriteOption = library.OverwriteOptions.First(o => o.Value == policy),
                    },
                    new ToggleSetting
                    {
                        Id = "output.flatPack",
                        Category = packing,
                        Label = Strings.FlatPackLabel,
                        Description = Strings.FlatPackToolTip,
                        Keywords = Words(Strings.FlatPackKeywords),
                        DefaultValue = false,
                        Get = () => library.FlatPackOutput,
                        Set = flat => library.FlatPackOutput = flat,
                    },
                    new RangeSetting
                    {
                        Id = "playback.masterVolume",
                        Category = audio,
                        Label = Strings.VolumeName,
                        Description = Strings.VolumeDescription,
                        Keywords = Words(Strings.VolumeKeywords),
                        DefaultValue = AppSettings.DefaultMasterVolume,
                        Minimum = 0,
                        Maximum = 1,
                        SmallChange = 0.05,
                        Format = volume => volume.ToString("P0", CultureInfo.CurrentCulture),
                        Get = () => player.Volume,
                        Set = volume => player.Volume = volume,
                    },
                    new ToggleSetting
                    {
                        Id = "playback.showWaveforms",
                        Category = visualizers,
                        Label = Strings.ShowWaveformsLabel,
                        Description = Strings.ShowWaveformsDescription,
                        Keywords = Words(Strings.ShowWaveformsKeywords),
                        DefaultValue = true,
                        Get = () => player.ShowWaveforms,
                        Set = show => player.ShowWaveforms = show,
                    },
                    new ToggleSetting
                    {
                        Id = "playback.showSpectrum",
                        Category = visualizers,
                        Label = Strings.ShowSpectrumLabel,
                        Description = Strings.ShowSpectrumDescription,
                        Keywords = Words(Strings.ShowSpectrumKeywords),
                        DefaultValue = true,
                        Get = () => player.ShowSpectrum,
                        Set = show => player.ShowSpectrum = show,
                    },
                ]);
        }

        private static string[] Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    }
}
