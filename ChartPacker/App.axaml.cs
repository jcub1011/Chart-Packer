using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ChartPacker.Audio.Playback;
using ChartPacker.Core;
using ChartPacker.Core.Scanning;
using ChartPacker.Ffmpeg;
using ChartPacker.Services;
using ChartPacker.ViewModels;
using ChartPacker.ViewModels.Settings;
using ChartPacker.Views;

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
                var player = new PlayerViewModel(settings, audio.Open);
                var highway = new HighwayViewModel(settings, player);

                // FFmpeg is looked for now, off the UI thread, so the first video shown doesn't wait for it to load.
                _ = Task.Run(() => FfmpegLibrary.IsAvailable);

                var videoProbe = new FfmpegVideoDurationProbe();
                // A Merge window belongs to whichever window opens it: the main window, or the Deduplicate window.
                Func<Func<Window?>, IMergeWindowService> mergeWindowFor = owner =>
                    new AvaloniaMergeWindowService(owner, drafts, settings, audio.Open, player, highway, imagePicker, filePicker, videoProbe);
                var dedupWindow = new AvaloniaDedupWindowService(
                    () => window,
                    drafts,
                    settings,
                    player,
                    mergeWindowFor,
                    Path.Combine(UserConfigPaths.ConfigFolder, "dedup-log.jsonl"));
                var detail = new SongDetailViewModel(drafts, job, imagePicker, filePicker, player, highway, videoProbe, downloads, reveal, songFiles, confirmation);
                var validatorWindow = new AvaloniaValidatorWindowService(
                    () => window, drafts, job, settings, audio.Open, detail, videoProbe, notice, songFiles);
                var library = new LibraryViewModel(
                    folderPicker, settings, drafts, job, player, mergeWindowFor(() => window), dedupWindow, validatorWindow, reveal, downloads, new ChartBuilderLauncher(), notice);
                var settingsWindow = new AvaloniaSettingsWindowService(
                    () => window,
                    settingsPicker => new SettingsWindowViewModel(AppSettingsCatalog.Create(library, player, highway, downloads), settingsPicker, settings.Save));

                window.DataContext = new MainWindowViewModel(
                    library,
                    detail,
                    settings,
                    drafts,
                    confirmation,
                    settingsWindow,
                    notice);

                // Subscribed after MainWindow's own Closing handler, which captures the splitter positions first.
                // It also runs when that handler cancels the close to ask about unsaved changes, which is harmless.
                window.Closing += (_, _) => settings.Save();

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
