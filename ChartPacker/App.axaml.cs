using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ChartPacker.Resources;
using ChartPacker.Services;
using ChartPacker.ViewModels;
using ChartPacker.Views;
using ChartShared.Audio.Playback;
using ChartShared.Core;
using ChartShared.Core.Launching;
using ChartShared.Core.Scanning;
using ChartShared.Core.Settings;
using ChartShared.Ffmpeg;
using ChartShared.UI.Services;
using ChartShared.UI.ViewModels.Settings;

namespace ChartPacker
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Composition root: services are wired by hand (spec §4.2).
                var settings = SettingsService.Load(UserConfigPaths.SettingsFile);
                var window = new MainWindow();
                var folderPicker = new AvaloniaFolderPickerService(() => window);
                var imagePicker = new AvaloniaImagePickerService(() => window);
                var filePicker = new AvaloniaFilePickerService(() => window);
                var confirmation = new AvaloniaConfirmationService(() => window);
                var reveal = new FileRevealService(() => window);
                var notice = new NoticeViewModel(reveal);
                var songFiles = new SongFileGate();
                var downloads = new DownloadService(settings, () => window, notice, songFiles);
                var drafts = new MetadataDraftStore();
                var job = new JobViewModel(new AvaloniaJobProgressWindowService(() => window));
                // Started here, on the UI thread, as SDL requires; devices are opened on it from any thread.
                var audio = new SdlAudio();
                // Measured by Chart Builder's calibration tool, which writes it; read here (charter-spec C11 item 4).
                var latency = new AudioLatencyFile(UserConfigPaths.AudioLatencyFile);
                var player = new PlayerViewModel(settings, audio.Open, latency);
                var highway = new HighwayViewModel(settings, player);

                // FFmpeg is looked for now, off the UI thread, so the first video shown doesn't wait for it to load.
                _ = Task.Run(() => FfmpegLibrary.IsAvailable);

                var videoProbe = new FfmpegVideoDurationProbe();
                // Whether Chart Builder has a song open, so Chart Packer asks before writing to it (charter-spec §4.8).
                Func<string, bool> isOpenInChartBuilder = path => SongLock.IsHeld(path, UserConfigPaths.LockFolder);

                // A Merge window belongs to whichever window opens it: the main window, or the Deduplicate window.
                Func<Func<Window?>, IMergeWindowService> mergeWindowFor = owner =>
                    new AvaloniaMergeWindowService(owner, drafts, settings, audio.Open, player, highway, imagePicker, filePicker, videoProbe, latency, isOpenInChartBuilder);
                var dedupWindow = new AvaloniaDedupWindowService(
                    () => window,
                    drafts,
                    settings,
                    player,
                    mergeWindowFor,
                    Path.Combine(UserConfigPaths.ConfigFolder, "dedup-log.jsonl"),
                    isOpenInChartBuilder);
                var detail = new SongDetailViewModel(
                    drafts, job, imagePicker, filePicker, player, highway, videoProbe, downloads, reveal, songFiles, confirmation, isOpenInChartBuilder: isOpenInChartBuilder,
                    chartIssuesWindow: new AvaloniaChartIssuesWindowService(() => window));
                var validatorWindow = new AvaloniaValidatorWindowService(
                    () => window, drafts, job, settings, audio.Open, detail, videoProbe, notice, songFiles, isOpenInChartBuilder, latency);
                var chartBuilder = new ChartBuilderLauncher();
                var library = new LibraryViewModel(
                    folderPicker, settings, drafts, job, player, mergeWindowFor(() => window), dedupWindow, validatorWindow, reveal, downloads, chartBuilder, notice);

                // Settings' Calibrate… runs Chart Builder's calibration tool (charter-spec §4.8); the latency it saves is
                // read again when this window is next activated.
                void Calibrate()
                {
                    if (chartBuilder.Start(BuilderArgs.ForCalibrate()) is { } problem)
                        notice.ShowError(problem);
                }

                var settingsWindow = new AvaloniaSettingsWindowService(
                    () => window,
                    settingsPicker => new SettingsWindowViewModel(
                        AppSettingsCatalog.Create(library, player, highway, downloads, settings, Calibrate), settingsPicker, settings.Save),
                    settings.Settings,
                    settings.Save,
                    Strings.AppName);

                window.DataContext = new MainWindowViewModel(
                    library,
                    detail,
                    settings,
                    drafts,
                    confirmation,
                    settingsWindow,
                    notice);

                // Opens where it was when it last closed.
                MainWindowPlacement.Restore(window, settings.Settings);

                // Subscribed after MainWindow's own Closing handler, which captures the splitter positions first.
                // It also runs when that handler cancels the close to ask about unsaved changes. The placement is saved
                // then too, so it isn't lost if the OS shuts down without waiting for the answer; a close the user
                // cancels leaves the window as it was, and the next close saves it again.
                window.Closing += (_, _) =>
                {
                    MainWindowPlacement.Capture(window, settings.Settings);
                    settings.Save();
                };

                // Stops playback and closes the audio device and the song's files.
                desktop.Exit += (_, _) =>
                {
                    player.Dispose();
                    audio.Dispose();
                };

                desktop.MainWindow = window;
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
