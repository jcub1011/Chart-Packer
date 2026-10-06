using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ChartBuilder.Services;
using ChartPacker.Audio.Playback;
using ChartPacker.Core;
using ChartPacker.Core.Launching;

namespace ChartBuilder
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
                // Composition root: services are wired by hand, as in Chart Packer. BuilderWindows wires each window
                // as it opens: the launcher first, then the Editor in its place.
                var settings = BuilderSettingsService.Load(UserConfigPaths.BuilderSettingsFile);

                // SDL's audio starts on the main thread. It never throws: a device that can't start says so when a
                // song is played.
                var audio = new SdlAudio();
                var windows = new BuilderWindows(desktop, settings, audio.Open, UserConfigPaths.LockFolder);
                windows.StartWith(BuilderArgs.Parse(desktop.Args ?? []));

                // Settings are saved as they change (the Settings window saves them, and the Editor saves its layout
                // as it closes), so there's nothing left to save on exit. The Editor's player has closed with its
                // window by then.
                desktop.Exit += (_, _) => audio.Dispose();
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
