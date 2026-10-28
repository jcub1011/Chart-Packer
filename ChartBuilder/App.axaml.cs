using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ChartBuilder.Services;
using ChartShared.Audio.Playback;
using ChartShared.Core;
using ChartShared.Core.Charting.Recovery;
using ChartShared.Core.Charting.Saving;
using ChartBuilder.Devices;
using ChartShared.Core.Launching;
using ChartShared.Core.Settings;
using ChartShared.UI.Services;
using ChartShared.UI.Views;

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
                // Composition root: services are wired by hand, as in Chart Packer. BuilderWindows opens the
                // first Editor, then swaps it for a new one for each song or chart opened.
                var settings = BuilderSettingsService.Load(UserConfigPaths.BuilderSettingsFile);
                // Every window opens as it last closed, the Editor where it was too, so this comes before any is made.
                WindowMemory.Use(settings.Settings, settings.Save);

                // An Editor that didn't close cleanly leaves the copies it kept for undo; a day on, no session needs them.
                MediaStash.SweepStale(TimeSpan.FromDays(1));

                // SDL's audio starts on the main thread. It never throws: a device that can't start says so when a
                // song is played.
                var audio = new SdlAudio();
                var latency = new AudioLatencyService(new AudioLatencyFile(UserConfigPaths.AudioLatencyFile));

                // The instrument devices (charter-spec C19). Their backends start only while something plays them, on the
                // main thread, as SDL wants.
                var input = new InputService(InputSettingsService.Load(UserConfigPaths.InputBindingsFile), InputBackends.Create());
                var windows = new BuilderWindows(
                    desktop, settings, audio.Open, latency, UserConfigPaths.SettingsFile, new RecoveryStore(UserConfigPaths.RecoveryFolder), input);
                windows.StartWith(BuilderArgs.Parse(desktop.Args ?? []));

                // Settings are saved as they change (the Settings window saves them, and the Editor saves its layout
                // as it closes), so there's nothing left to save on exit. The Editor's player has closed with its
                // window by then.
                desktop.Exit += (_, _) =>
                {
                    input.Dispose();
                    audio.Dispose();
                };
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
