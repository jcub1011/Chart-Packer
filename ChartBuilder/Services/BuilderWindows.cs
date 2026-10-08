using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using ChartBuilder.Resources;
using ChartBuilder.ViewModels;
using ChartBuilder.Views;
using ChartPacker.Audio.Playback;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Recovery;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Launching;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Settings;
using ChartPacker.UI.Services;
using ChartPacker.UI.Views;
using ChartPacker.ViewModels.Settings;

namespace ChartBuilder.Services
{
    /// <summary>
    /// Chart Builder's top-level windows: the launcher (home screen) and the Editor. Only one is open at a time,
    /// and closing the Editor brings the launcher back.
    /// </summary>
    /// <param name="openOutput">Opens the audio output at a rate, for the Editor's playback and the calibration tool.</param>
    /// <param name="latency">The audio/visual latency, which the calibration tool measures.</param>
    /// <param name="lockFolder">Where the songs open are locked (<see cref="SongLock"/>).</param>
    /// <param name="chartPackerSettingsFile">Chart Packer's settings, which a save reads for the library and packing, never writes.</param>
    /// <param name="recovery">Where the Editor autosaves the chart open, and the launcher finds what's left to recover.</param>
    public sealed class BuilderWindows(
        IClassicDesktopStyleApplicationLifetime desktop,
        BuilderSettingsService settings,
        Func<int, IAudioOutput> openOutput,
        AudioLatencyService latency,
        string lockFolder,
        string chartPackerSettingsFile,
        RecoveryStore recovery) : IEditorWindowService
    {
        // The calibration tool, while it's open.
        private CalibrationWindow? _calibration;

        /// <summary>Makes the launcher the app's main window, opening <paramref name="args"/> over it once it's showing.</summary>
        /// <remarks>Called before the app has started, so the lifetime shows it.</remarks>
        public void StartWith(BuilderArgs args)
        {
            var (launcher, viewModel) = CreateLauncher();

            // What the command line asks for (charter-spec §4.8) opens over the window once it's showing.
            launcher.Opened += OnOpened;
            desktop.MainWindow = launcher;

            void OnOpened(object? sender, EventArgs e)
            {
                launcher.Opened -= OnOpened;
                // Posted, since it can close the launcher (New chart opens the editor) and Opened is still running.
                Dispatcher.UIThread.Post(() => viewModel.Start(args));
            }
        }

        public void OpenNewChart(IReadOnlyList<string> audioFiles)
        {
            // The chart starts on the default resolution, 120 BPM and 4/4 with a Lead part, all of which the Chart
            // settings tab changes (charter-spec C1).
            var chart = EditableChart.CreateNew(new NewChartSettings(
                "", settings.Settings.DefaultChartResolution, NewChartSettings.DefaultBpm, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.Guitar }, DrumsVariant.Pro));
            var audio = NewChartStems.TracksOf(audioFiles);
            var editor = new EditorWindow();
            OpenEditor(editor, EditorWindowViewModel.ForNewChart(ServicesFor(editor), chart, audio));
        }

        public void OpenSong(OpenedSong song, SongLock songLock)
        {
            var editor = new EditorWindow();
            OpenEditor(editor, EditorWindowViewModel.ForSong(ServicesFor(editor), song, songLock));
        }

        public void OpenRecovered(EditorSong song, SongLock? songLock, string recoveryKey)
        {
            var editor = new EditorWindow();
            OpenEditor(editor, EditorWindowViewModel.ForRecovered(ServicesFor(editor), song, songLock, recoveryKey));
        }

        /// <summary>What the Editor in <paramref name="editor"/> works with, its prompts and pickers shown over it.</summary>
        private EditorServices ServicesFor(EditorWindow editor) => new(
            settings,
            SettingsWindowFor(editor),
            new AvaloniaConfirmationService(() => editor),
            new AvaloniaFolderPickerService(() => editor),
            new AvaloniaImagePickerService(() => editor),
            new AvaloniaFirstSaveDialogService(() => editor),
            new AvaloniaMessagesDialogService(() => editor),
            openOutput,
            latency,
            () => _ = ShowCalibrationAsync(),
            lockFolder,
            chartPackerSettingsFile,
            recovery,
            new AvaloniaAuthoringDialogService(() => editor));

        /// <summary>Shows the Editor in place of the launcher, and starts its audio.</summary>
        private void OpenEditor(EditorWindow editor, EditorWindowViewModel viewModel)
        {
            Open(editor, viewModel);

            // The song's output says which device it's on, which may never have been calibrated.
            void OnLatencyChanged(object? sender, EventArgs e) => OfferCalibration(editor);
            latency.Changed += OnLatencyChanged;
            editor.Closed += (_, _) => latency.Changed -= OnLatencyChanged;
            _ = viewModel.Playback.OpenAsync();
        }

        /// <summary>
        /// Opens the calibration tool (charter-spec C11) over the window in front, pausing the Editor's playback. Only
        /// one is open at a time; opening it again brings that one to the front.
        /// </summary>
        public async Task ShowCalibrationAsync()
        {
            if (_calibration is { } open)
            {
                open.Activate();
                return;
            }

            if (desktop.MainWindow is EditorWindow { DataContext: EditorWindowViewModel editor })
                editor.Playback.Pause();

            var window = new CalibrationWindow { DataContext = new CalibrationViewModel(openOutput, latency) };
            var owner = desktop.Windows.LastOrDefault(w => w.IsActive) ?? desktop.MainWindow;
            _calibration = window;
            try
            {
                if (owner is null)
                {
                    var closed = new TaskCompletionSource();
                    window.Closed += (_, _) => closed.TrySetResult();
                    window.Show();
                    await closed.Task;
                }
                else
                {
                    WindowChrome.UseOwnerIcon(window, owner);
                    await window.ShowDialog(owner);
                }
            }
            finally
            {
                _calibration = null;
            }
        }

        /// <summary>
        /// Offers the calibration tool the first time a song plays on a device that has no calibrated latency, once for
        /// each device, unless the user turned that off.
        /// </summary>
        private void OfferCalibration(EditorWindow editor)
        {
            if (!settings.Settings.PromptForCalibration
                || _calibration is not null
                || latency.Source != LatencySource.Fallback
                || latency.Device is not { } device)
                return;

            string key = string.IsNullOrEmpty(device.Id) ? device.Name : device.Id;
            if (string.IsNullOrEmpty(key) || settings.Settings.CalibrationPrompted.Contains(key))
                return;

            // Remembered before asking, so the answer, or closing the prompt, skips it from now on.
            settings.Settings.CalibrationPrompted.Add(key);
            settings.Save();
            _ = OfferCalibrationAsync(editor, device);
        }

        private async Task OfferCalibrationAsync(EditorWindow editor, AudioDevice device)
        {
            var confirmations = new AvaloniaConfirmationService(() => editor);
            string name = string.IsNullOrEmpty(device.Name) ? Strings.CalibrationUnknownDevice : device.Name;
            if (await confirmations.ConfirmAsync(
                    Strings.CalibrationPromptTitle,
                    string.Format(CultureInfo.CurrentCulture, Strings.CalibrationPromptFormat, name),
                    Strings.CalibrationPromptConfirm))
                await ShowCalibrationAsync();
        }

        private void Open(EditorWindow editor, EditorWindowViewModel viewModel)
        {
            editor.DataContext = viewModel;

            // The song's audio and lock go with the window.
            editor.Closed += (_, _) => viewModel.Dispose();

            // Unsaved edits are asked about first: Save, Discard or Cancel (charter-spec C15 item 7). The close is held
            // while the user answers, and made again once they've chosen to go ahead. Registered before the handler
            // that brings the launcher back, so that one sees the close cancelled.
            bool closeAllowed = false;
            editor.Closing += (_, e) =>
            {
                if (closeAllowed || e.Cancel || !viewModel.HasUnsavedChanges && !viewModel.IsSaving)
                    return;

                // The OS won't wait for an answer, so the edits are autosaved, to be offered when the song is next opened.
                if (e.CloseReason == WindowCloseReason.OSShutdown)
                {
                    viewModel.AutosaveNow();
                    return;
                }

                e.Cancel = true;
                Dispatcher.UIThread.Post(async () =>
                {
                    if (await viewModel.ConfirmCloseAsync())
                    {
                        closeAllowed = true;
                        editor.Close();
                    }
                });
            };

            // Back to the launcher when the user closes the editor, but not when the app or OS is shutting down. It
            // opens while the editor is still closing, so the app, which exits when its last window closes, stays up.
            editor.Closing += (_, e) =>
            {
                if (!e.Cancel && e.CloseReason is not (WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown))
                    Show(CreateLauncher().Window);
            };

            // The launcher closes once the editor is showing, for the same reason.
            var launcher = desktop.MainWindow;
            Show(editor);
            launcher?.Close();
        }

        private (MainWindow Window, MainWindowViewModel ViewModel) CreateLauncher()
        {
            var launcher = new MainWindow();
            var viewModel = new MainWindowViewModel(
                this,
                    SettingsWindowFor(launcher),
                new AvaloniaFilePickerService(() => launcher),
                new AvaloniaConfirmationService(() => launcher),
                settings,
                lockFolder,
                recovery);
            launcher.DataContext = viewModel;
            return (launcher, viewModel);
        }

        private void Show(Window window)
        {
            window.Show();
            desktop.MainWindow = window;
        }

        private AvaloniaSettingsWindowService SettingsWindowFor(Window owner) => new(
            () => owner,
            settingsPicker => new SettingsWindowViewModel(
                BuilderSettingsCatalog.Create(settings, () => _ = ShowCalibrationAsync(), () => _ = ShowReductionDefaultsAsync()), settingsPicker, settings.Save));

        /// <summary>Opens the Reduce dialog's Settings mode over the window in front, which edits the default rules (charter-spec §10).</summary>
        private async Task ShowReductionDefaultsAsync()
        {
            var owner = desktop.Windows.LastOrDefault(w => w.IsActive) ?? desktop.MainWindow;
            await new AvaloniaAuthoringDialogService(() => owner).ShowReduceAsync(ReduceViewModel.ForDefaults(settings));
        }
    }
}
