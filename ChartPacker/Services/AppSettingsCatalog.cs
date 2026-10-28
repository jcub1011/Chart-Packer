using System;
using System.Globalization;
using System.Linq;
using ChartPacker.Resources;
using ChartPacker.ViewModels;
using ChartShared.Audio.Waveforms;
using ChartShared.Core.Charting;
using ChartShared.Core.Dedup;
using ChartShared.Core.Jobs;
using ChartShared.Core.Settings;
using ChartShared.UI;
using ChartShared.UI.Resources;

namespace ChartPacker.Services
{
    /// <summary>
    /// Every setting the Settings window shows. To add a setting, add its definition (and its strings) here;
    /// to remove one, delete it.
    /// </summary>
    /// <remarks>
    /// Settings are read and written through the view models that own the live values rather than through
    /// <see cref="AppSettings"/> directly. The view models already persist a change and refresh what depends
    /// on it, so the menus and player stay in step with the window. Window layout (pane sizes) isn't listed.
    /// </remarks>
    public static class AppSettingsCatalog
    {
        /// <param name="calibrate">Runs Chart Builder's calibration tool, which measures the audio latency (charter-spec C11).</param>
        public static SettingsCatalog Create(
            LibraryViewModel library,
            PlayerViewModel player,
            HighwayViewModel highway,
            DownloadService downloads,
            SettingsService settings,
            Action calibrate)
        {
            var libraryCategory = new SettingCategory("library", Strings.SettingsCategoryLibrary);
            var scanning = new SettingCategory("library.scanning", Strings.SettingsCategoryScanning, libraryCategory);
            var duplicates = new SettingCategory("library.dedup", Strings.SettingsCategoryDuplicates, libraryCategory);
            var output = new SettingCategory("output", Strings.SettingsCategoryOutput);
            var packing = new SettingCategory("output.packing", Strings.SettingsCategoryPacking, output);
            var downloadsCategory = new SettingCategory("output.downloads", Strings.SettingsCategoryDownloads, output);
            var playback = new SettingCategory("playback", Strings.SettingsCategoryPlayback);
            var audio = new SettingCategory("playback.audio", Strings.SettingsCategoryAudio, playback);
            var visualizers = new SettingCategory("playback.visualizers", Strings.SettingsCategoryVisualizers, playback);
            var scrubbing = new SettingCategory("playback.scrubbing", Strings.SettingsCategoryScrubbing, playback);
            var general = new SettingCategory("general", SharedStrings.SettingsCategoryGeneral);
            var appearance = new SettingCategory("general.appearance", Strings.SettingsCategoryAppearance, general);
            var logging = new SettingCategory("general.logging", SharedStrings.SettingsCategoryLogging, general);

            return new SettingsCatalog(
                [libraryCategory, scanning, duplicates, output, packing, downloadsCategory, playback, audio, visualizers, scrubbing, general, appearance, logging],
                [
                    new ToggleSetting
                    {
                        Id = "library.scanOnLaunch",
                        Category = scanning,
                        Label = Strings.ScanOnLaunchLabel,
                        Description = Strings.ScanOnLaunchDescription,
                        Keywords = SettingKeywords.Split(Strings.ScanOnLaunchKeywords),
                        DefaultValue = AppSettings.DefaultScanOnLaunch,
                        Get = () => library.ScanOnLaunch,
                        Set = scan => library.ScanOnLaunch = scan,
                    },
                    new IntegerSetting
                    {
                        Id = "library.scanDepth",
                        Category = scanning,
                        Label = Strings.ScanDepthLabel,
                        Description = Strings.ScanDepthDescription,
                        Keywords = SettingKeywords.Split(Strings.ScanDepthKeywords),
                        DefaultValue = AppSettings.DefaultScanMaxDepth,
                        Minimum = 0,
                        Maximum = AppSettings.MaxScanMaxDepth,
                        Get = () => library.ScanDepth is { } depth ? (int)depth : AppSettings.DefaultScanMaxDepth,
                        Set = depth => library.ScanDepth = depth,
                    },
                    new IntegerSetting
                    {
                        Id = "library.scanParallelism",
                        Category = scanning,
                        Label = Strings.ScanParallelismLabel,
                        Description = Strings.ScanParallelismDescription,
                        Keywords = SettingKeywords.Split(Strings.ScanParallelismKeywords),
                        DefaultValue = AppSettings.DefaultScanParallelism,
                        Minimum = AppSettings.MinScanParallelism,
                        Maximum = int.MaxValue,
                        Get = () => library.ScanParallelism,
                        Set = parallelism => library.ScanParallelism = parallelism,
                    },
                    new ChoiceSetting<DedupKindPreference>
                    {
                        Id = "library.dedupKeep",
                        Category = duplicates,
                        Label = Strings.DedupKeepLabel,
                        Description = Strings.DedupKeepDescription,
                        Keywords = SettingKeywords.Split(Strings.DedupKeepKeywords),
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
                        Keywords = SettingKeywords.Split(Strings.OutputFolderKeywords),
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
                        Keywords = SettingKeywords.Split(Strings.OverwriteKeywords),
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
                        Keywords = SettingKeywords.Split(Strings.FlatPackKeywords),
                        DefaultValue = false,
                        Get = () => library.FlatPackOutput,
                        Set = flat => library.FlatPackOutput = flat,
                    },
                    new FolderSetting
                    {
                        Id = "downloads.folder",
                        Category = downloadsCategory,
                        Label = Strings.DownloadFolderLabel,
                        Description = Strings.DownloadFolderDescription,
                        Keywords = SettingKeywords.Split(Strings.DownloadFolderKeywords),
                        DefaultValue = null,
                        EmptyLabel = Strings.DownloadFolderEmpty,
                        PickerTitle = Strings.BrowseDownloadDialogTitle,
                        Get = () => downloads.Folder,
                        Set = folder => downloads.Folder = folder,
                    },
                    new ToggleSetting
                    {
                        Id = "downloads.askEveryTime",
                        Category = downloadsCategory,
                        Label = Strings.AskDownloadLocationLabel,
                        Description = Strings.AskDownloadLocationDescription,
                        Keywords = SettingKeywords.Split(Strings.AskDownloadLocationKeywords),
                        DefaultValue = false,
                        Get = () => downloads.AskEveryTime,
                        Set = ask => downloads.AskEveryTime = ask,
                    },
                    new RangeSetting
                    {
                        Id = "playback.masterVolume",
                        Category = audio,
                        Label = Strings.VolumeName,
                        Description = Strings.VolumeDescription,
                        Keywords = SettingKeywords.Split(Strings.VolumeKeywords),
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
                        Id = "playback.pitchCorrection",
                        Category = audio,
                        Label = Strings.PitchCorrectionLabel,
                        Description = Strings.PitchCorrectionDescription,
                        Keywords = SettingKeywords.Split(Strings.PitchCorrectionKeywords),
                        DefaultValue = AppSettings.DefaultPitchCorrection,
                        Get = () => player.PitchCorrection,
                        Set = correct => player.PitchCorrection = correct,
                    },
                    // Measured, per device, by the calibration tool both apps share (charter-spec C11).
                    new ActionSetting
                    {
                        Id = "playback.latency",
                        Category = audio,
                        Label = Strings.AudioLatencyLabel,
                        Description = Strings.AudioLatencyDescription,
                        Keywords = SettingKeywords.Split(Strings.AudioLatencyKeywords),
                        Actions = [new SettingAction(Strings.CalibrateButton, calibrate)],
                    },
                    // Kept in the shared latency file, not Chart Packer's settings, so Chart Builder agrees (issue #140).
                    new ToggleSetting
                    {
                        Id = "playback.detectLatency",
                        Category = audio,
                        Label = Strings.DetectLatencyLabel,
                        Description = Strings.DetectLatencyDescription,
                        Keywords = SettingKeywords.Split(Strings.DetectLatencyKeywords),
                        DefaultValue = true,
                        Get = () => player.DetectLatency,
                        Set = detect => player.DetectLatency = detect,
                    },
                    new ToggleSetting
                    {
                        Id = "playback.showWaveforms",
                        Category = visualizers,
                        Label = Strings.ShowWaveformsLabel,
                        Description = Strings.ShowWaveformsDescription,
                        Keywords = SettingKeywords.Split(Strings.ShowWaveformsKeywords),
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
                        Keywords = SettingKeywords.Split(Strings.ShowLaneWaveformsKeywords),
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
                        Keywords = SettingKeywords.Split(Strings.WaveformFidelityKeywords),
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
                        Keywords = SettingKeywords.Split(Strings.ShowSpectrumKeywords),
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
                        Keywords = SettingKeywords.Split(Strings.ShowHighwayKeywords),
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
                        Keywords = SettingKeywords.Split(Strings.HighwayPlacementKeywords),
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
                    new LookAheadSetting
                    {
                        Id = "playback.highwayLookAhead",
                        Category = visualizers,
                        Label = Strings.HighwayLookAheadLabel,
                        Description = Strings.HighwayLookAheadDescription,
                        Keywords = SettingKeywords.Split(Strings.HighwayLookAheadKeywords),
                        DefaultValue = AppSettings.DefaultHighwayLookAhead,
                        Get = () => highway.LookAhead,
                        Set = seconds => highway.LookAhead = seconds,
                    },
                    new ChoiceSetting<ScrubStep>
                    {
                        Id = "playback.scrubStep",
                        Category = scrubbing,
                        Label = Strings.ScrubStepLabel,
                        Description = Strings.ScrubStepDescription,
                        Keywords = SettingKeywords.Split(Strings.ScrubStepKeywords),
                        DefaultValue = AppSettings.DefaultScrubStep,
                        Choices = [.. Enum.GetValues<ScrubStep>().Select(step => new SettingChoice<ScrubStep>(step, ScrubSteps.NameOf(step)))],
                        Get = () => player.ScrubStep,
                        Set = step => player.ScrubStep = step,
                    },
                    new RangeSetting
                    {
                        Id = "playback.fineScrubSpeed",
                        Category = scrubbing,
                        Label = Strings.FineScrubSpeedLabel,
                        Description = Strings.FineScrubSpeedDescription,
                        Keywords = SettingKeywords.Split(Strings.FineScrubSpeedKeywords),
                        DefaultValue = AppSettings.DefaultFineScrubMilliseconds,
                        Minimum = AppSettings.MinFineScrubMilliseconds,
                        Maximum = AppSettings.MaxFineScrubMilliseconds,
                        SmallChange = 0.5,
                        Format = ms => string.Format(CultureInfo.CurrentCulture, Strings.FineScrubSpeedFormat, ms),
                        Get = () => player.FineScrubMilliseconds,
                        Set = ms => player.FineScrubMilliseconds = ms,
                    },
                    new RangeSetting
                    {
                        Id = "playback.laneFineScrubFactor",
                        Category = scrubbing,
                        Label = Strings.LaneFineScrubLabel,
                        Description = Strings.LaneFineScrubDescription,
                        Keywords = SettingKeywords.Split(Strings.LaneFineScrubKeywords),
                        DefaultValue = AppSettings.DefaultLaneFineScrubFactor,
                        Minimum = AppSettings.MinLaneFineScrubFactor,
                        Maximum = AppSettings.MaxLaneFineScrubFactor,
                        SmallChange = 1,
                        Format = factor => string.Format(CultureInfo.CurrentCulture, Strings.LaneFineScrubFormat, factor),
                        Get = () => player.LaneFineScrubFactor,
                        Set = factor => player.LaneFineScrubFactor = factor,
                    },
                    new ToggleSetting
                    {
                        Id = "general.albumArtTheme",
                        Category = appearance,
                        Label = Strings.AlbumArtThemeLabel,
                        Description = Strings.AlbumArtThemeDescription,
                        Keywords = SettingKeywords.Split(Strings.AlbumArtThemeKeywords),
                        DefaultValue = true,
                        Get = () => player.AlbumArtTheming,
                        Set = theme => player.AlbumArtTheming = theme,
                    },
                    AppLog.CreateSetting(logging, () => settings.Settings.LoggingEnabled, enabled => settings.Settings.LoggingEnabled = enabled),
                    .. UpdateCheck.CreateSettings(general),
                ]);
        }
    }
}
