using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ChartPacker.Audio.Playback;
using ChartPacker.Core;
using ChartPacker.Services;
using ChartPacker.Video;
using ChartPacker.ViewModels;
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
                var drafts = new MetadataDraftStore();
                var job = new JobViewModel(settings);
                // Started here, on the UI thread, as SDL requires; devices are opened on it from any thread.
                var audio = new SdlAudio();
                var player = new PlayerViewModel(settings, audio.Open);

                // FFmpeg is looked for now, off the UI thread, so the first video shown doesn't wait for it to load.
                _ = Task.Run(() => FfmpegLibrary.IsAvailable);

                window.DataContext = new MainWindowViewModel(
                    new LibraryViewModel(folderPicker, settings, drafts, job, player),
                    new SongDetailViewModel(drafts, job, imagePicker, filePicker, player),
                    settings,
                    drafts,
                    confirmation);

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
