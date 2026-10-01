using System;
using System.Globalization;
using System.Linq;
using ChartPacker.Audio.Waveforms;
using ChartPacker.Core.Dedup;
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
        public static SettingsCatalog Create(LibraryViewModel library, PlayerViewModel player, HighwayViewModel highway)
        {
            var libraryCategory = new SettingCategory("library", Strings.SettingsCategoryLibrary);
            var scanning = new SettingCategory("library.scanning", Strings.SettingsCategoryScanning, libraryCategory);
            var duplicates = new SettingCategory("library.dedup", Strings.SettingsCategoryDuplicates, libraryCategory);
            var output = new SettingCategory("output", Strings.SettingsCategoryOutput);
            var packing = new SettingCategory("output.packing", Strings.SettingsCategoryPacking, output);
            var playback = new SettingCategory("playback", Strings.SettingsCategoryPlayback);
            var audio = new SettingCategory("playback.audio", Strings.SettingsCategoryAudio, playback);
            var visualizers = new SettingCategory("playback.visualizers", Strings.SettingsCategoryVisualizers, playback);

            return new SettingsCatalog(
                [libraryCategory, scanning, duplicates, output, packing, playback, audio, visualizers],
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
                    new ChoiceSetting<DedupKindPreference>
                    {
                        Id = "library.dedupKeep",
                        Category = duplicates,
                        Label = Strings.DedupKeepLabel,
                        Description = Strings.DedupKeepDescription,
                        Keywords = Words(Strings.DedupKeepKeywords),
                        DefaultValue = AppSettings.DefaultDedupKeepPreference,
                        Choices =
                        [
                            new SettingChoice<DedupKindPreference>(DedupKindPreference.PreferSng, Strings.DedupKeepSng),
                            new SettingChoice<DedupKindPreference>(DedupKindPreference.PreferFolder, Strings.DedupKeepFolder),
                        ],
                        Get = () => library.DedupKeepPreference,
                        Set = preference => library.DedupKeepPreference = preference,
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
                        Id = "playback.showLaneWaveforms",
                        Category = visualizers,
                        Label = Strings.ShowLaneWaveformsLabel,
                        Description = Strings.ShowLaneWaveformsDescription,
                        Keywords = Words(Strings.ShowLaneWaveformsKeywords),
                        RequiresId = "playback.showWaveforms",
                        DefaultValue = true,
                        Get = () => player.ShowLaneWaveforms,
                        Set = show => player.ShowLaneWaveforms = show,
                    },
                    new ChoiceSetting<WaveformFidelity>
                    {
                        Id = "playback.waveformFidelity",
                        Category = visualizers,
                        Label = Strings.WaveformFidelityLabel,
                        Description = Strings.WaveformFidelityDescription,
                        Keywords = Words(Strings.WaveformFidelityKeywords),
                        RequiresId = "playback.showWaveforms",
                        DefaultValue = AppSettings.DefaultWaveformFidelity,
                        Choices =
                        [
                            new SettingChoice<WaveformFidelity>(WaveformFidelity.Low, Strings.WaveformFidelityLow),
                            new SettingChoice<WaveformFidelity>(WaveformFidelity.Medium, Strings.WaveformFidelityMedium),
                            new SettingChoice<WaveformFidelity>(WaveformFidelity.High, Strings.WaveformFidelityHigh),
                            new SettingChoice<WaveformFidelity>(WaveformFidelity.Ultra, Strings.WaveformFidelityUltra),
                        ],
                        Get = () => player.WaveformFidelity,
                        Set = fidelity => player.WaveformFidelity = fidelity,
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
                    new ToggleSetting
                    {
                        Id = "playback.showNoteHighway",
                        Category = visualizers,
                        Label = Strings.ShowHighwayLabel,
                        Description = Strings.ShowHighwayDescription,
                        Keywords = Words(Strings.ShowHighwayKeywords),
                        DefaultValue = true,
                        Get = () => highway.IsEnabled,
                        Set = show => highway.IsEnabled = show,
                    },
                    new ChoiceSetting<HighwayPlacement>
                    {
                        Id = "playback.highwayPlacement",
                        Category = visualizers,
                        Label = Strings.HighwayPlacementLabel,
                        Description = Strings.HighwayPlacementDescription,
                        Keywords = Words(Strings.HighwayPlacementKeywords),
                        RequiresId = "playback.showNoteHighway",
                        DefaultValue = AppSettings.DefaultHighwayPlacement,
                        Choices =
                        [
                            new SettingChoice<HighwayPlacement>(HighwayPlacement.Background, Strings.HighwayPlacementBackground),
                            new SettingChoice<HighwayPlacement>(HighwayPlacement.Row, Strings.HighwayPlacementRow),
                        ],
                        Get = () => highway.Placement,
                        Set = placement => highway.Placement = placement,
                    },
                    new RangeSetting
                    {
                        Id = "playback.highwayNoteSpeed",
                        Category = visualizers,
                        Label = Strings.HighwayNoteSpeedLabel,
                        Description = Strings.HighwayNoteSpeedDescription,
                        Keywords = Words(Strings.HighwayNoteSpeedKeywords),
                        DefaultValue = AppSettings.DefaultHighwayNoteSpeed,
                        Minimum = AppSettings.MinHighwayNoteSpeed,
                        Maximum = AppSettings.MaxHighwayNoteSpeed,
                        SmallChange = 0.1,
                        Format = speed => speed.ToString("0.0", CultureInfo.CurrentCulture) + "×",
                        Get = () => highway.NoteSpeed,
                        Set = speed => highway.NoteSpeed = speed,
                    },
                ]);
        }

        private static string[] Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    }
}
