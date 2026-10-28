using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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
using ChartShared.Core.Charting.Saving;
using ChartShared.Core.Scanning;
using ChartShared.Core.Settings;
using ChartShared.Ffmpeg;
using ChartShared.UI;
using ChartShared.UI.Services;
using ChartShared.UI.ViewModels.Settings;
using ChartShared.UI.Views;

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
                // An exception nothing else catches on the UI thread is shown, and the app carries on, from here on.
                CrashHandler.Install(desktop, Strings.AppName);

                // Composition root: services are wired by hand (spec §4.2).
                var settings = SettingsService.Load(UserConfigPaths.SettingsFile);
                // Every window opens as it last closed, the main window where it was too, so this comes before any is made.
                WindowMemory.Use(settings.Settings, settings.Save);
                var window = new MainWindow();
                var folderPicker = new AvaloniaFolderPickerService(() => window);
                var imagePicker = new AvaloniaImagePickerService(() => window);
                var filePicker = new AvaloniaFilePickerService(() => window);
                var confirmation = new AvaloniaConfirmationService(() => window);
                var reveal = new FileRevealService(() => window);
                var notice = new NoticeViewModel(reveal);
                var songFiles = new SongFileGate();
                var drafts = new MetadataDraftStore();

                // Holds the songs with unsaved changes and those being written, and watches the songs shown, so that no
                // Chart Builder or other Chart Packer edits a song this one is, nor this one a song they are (charter-spec §4.8).
                var locks = new SongLocks(drafts);
                locks.ChangeRefused += (_, reason) => notice.ShowError(string.Format(CultureInfo.CurrentCulture, Strings.ChangeRefusedFormat, reason));
                var downloads = new DownloadService(settings, () => window, notice, songFiles, locks);
                var job = new JobViewModel(new AvaloniaJobProgressWindowService(() => window), locks.Set);
                // Started here, on the UI thread, as SDL requires; devices are opened on it from any thread.
                var audio = new SdlAudio();
                // Measured by the calibration tool, which both apps share and either may write (charter-spec C11 item 4).
                var latency = new AudioLatencyFile(UserConfigPaths.AudioLatencyFile);
                var player = new PlayerViewModel(settings, audio.Open, latency);
                var highway = new HighwayViewModel(settings, player);

                // FFmpeg is looked for now, off the UI thread, so the first video shown doesn't wait for it to load.
                _ = Task.Run(() => FfmpegLibrary.IsAvailable);

                var videoProbe = new FfmpegVideoDurationProbe();

                // A Merge window belongs to whichever window opens it: the main window, or the Deduplicate window.
                Func<Func<Window?>, IMergeWindowService> mergeWindowFor = owner =>
                    new AvaloniaMergeWindowService(owner, drafts, settings, audio.Open, player, highway, imagePicker, filePicker, videoProbe, latency, locks);
                var dedupWindow = new AvaloniaDedupWindowService(
                    () => window,
                    drafts,
                    settings,
                    player,
                    mergeWindowFor,
                    Path.Combine(UserConfigPaths.ConfigFolder, "dedup-log.jsonl"),
                    locks);
                var detail = new SongDetailViewModel(
                    drafts, job, imagePicker, filePicker, player, highway, videoProbe, downloads, reveal, songFiles, confirmation, locks: locks,
                    chartIssuesWindow: new AvaloniaChartIssuesWindowService(() => window));
                // Generate Lower Difficulties, and the Validator's quick fix for star power, keep the charts they make here
                // until their songs are saved; one an app that didn't close cleanly left is no use a day on. The rules the
                // difficulties are made by are shared with Chart Builder's Reduce (charter-spec C9 item 2).
                GeneratedChartStash.SweepStale(TimeSpan.FromDays(1));
                GeneratedChartStash? generated = null;
                try
                {
                    generated = GeneratedChartStash.Create();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Trace.TraceWarning($"No folder for generated charts, so the library can't generate charts: {ex.Message}");
                }

                var reductionRules = new ReductionRulesFile(UserConfigPaths.ReductionRulesFile, UserConfigPaths.BuilderSettingsFile);
                var validatorWindow = new AvaloniaValidatorWindowService(
                    () => window, drafts, job, settings, audio.Open, detail, videoProbe, notice, songFiles, locks, latency, generated);
                var lowerDifficultiesWindow = new AvaloniaLowerDifficultiesWindowService(
                    () => window, drafts, settings, audio.Open, detail, songFiles, generated, reductionRules, locks, latency);
                var chartBuilder = new ChartBuilderLauncher();
                var library = new LibraryViewModel(
                    folderPicker, settings, drafts, job, player, mergeWindowFor(() => window), dedupWindow, validatorWindow, reveal, downloads, chartBuilder, notice,
                    lowerDifficultiesWindow);

                // Settings' Calibrate… opens the calibration tool (charter-spec C11) over the window in front, pausing the
                // song; the latency it saves is read again once it closes. The tool starts on the device the song plays on,
                // so it names it and shows its latency even when its own output can't be opened.
                var calibrationLatency = new AudioLatencyService(latency);
                var calibration = new CalibrationWindowService(audio.Open, calibrationLatency, () =>
                {
                    player.Pause();
                    if (player.OutputDevice is { } device)
                        calibrationLatency.SetDevice(device);
                });
                async Task CalibrateAsync()
                {
                    try
                    {
                        await calibration.ShowAsync(desktop.Windows.LastOrDefault(w => w.IsActive) ?? window);
                    }
                    catch (Exception ex)
                    {
                        // Nothing awaits this, so anything not caught here would be lost.
                        Trace.TraceError($"The calibration tool failed: {ex}");
                    }
                    finally
                    {
                        player.RefreshLatency();
                    }
                }

                void Calibrate() => _ = CalibrateAsync();

                var settingsWindow = new AvaloniaSettingsWindowService(
                    () => window,
                    settingsPicker => new SettingsWindowViewModel(
                        AppSettingsCatalog.Create(library, player, highway, downloads, settings, Calibrate), settingsPicker, settings.Save),
                    Strings.AppName);

                window.DataContext = new MainWindowViewModel(
                    library,
                    detail,
                    settings,
                    drafts,
                    confirmation,
                    settingsWindow,
                    notice,
                    downloads,
                    folderPicker);

                // Subscribed after MainWindow's own Closing handler, which captures the splitter positions first.
                // It also runs when that handler cancels the close to ask about unsaved changes. They're saved then too,
                // so they aren't lost if the OS shuts down without waiting for the answer; a close the user cancels
                // leaves the window as it was, and the next close saves them again. Where the window is, WindowMemory saves,
                // with the splitters when it has a change of its own to save; nothing's written twice.
                window.Closing += (_, _) => settings.Save();

                // Stops playback and closes the audio device and the song's files.
                desktop.Exit += (_, _) =>
                {
                    player.Dispose();
                    audio.Dispose();
                    generated?.Dispose();
                };

                desktop.MainWindow = window;
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
