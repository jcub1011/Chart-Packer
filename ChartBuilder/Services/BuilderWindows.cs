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
using ChartShared.Audio.Playback;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Recovery;
using ChartShared.Core.Charts;
using ChartShared.Core.Input;
using ChartShared.Core.Launching;
using ChartShared.Core.Scanning;
using ChartShared.Core.Settings;
using ChartShared.UI.Services;
using ChartShared.UI.ViewModels.Settings;
using ChartShared.UI.Views;

namespace ChartBuilder.Services
{
    /// <summary>
    /// Chart Builder's top-level window: the Editor. Opening a song or a new chart swaps it for a new Editor window,
    /// closing this one; closing the last Editor exits the app.
    /// </summary>
    /// <param name="openOutput">Opens the audio output at a rate, for the Editor's playback and the calibration tool.</param>
    /// <param name="latency">The audio/visual latency, which the calibration tool measures.</param>
    /// <param name="lockFolder">Where the songs open are locked (<see cref="SongLock"/>).</param>
    /// <param name="chartPackerSettingsFile">Chart Packer's settings, which a save reads for the library and packing, never writes.</param>
    /// <param name="recovery">Where the Editor autosaves the chart open, and where what's left to recover is found on startup.</param>
    /// <param name="input">The instrument devices and their bindings (charter-spec C19).</param>
    public sealed class BuilderWindows(
        IClassicDesktopStyleApplicationLifetime desktop,
        BuilderSettingsService settings,
        Func<int, IAudioOutput> openOutput,
        AudioLatencyService latency,
        string lockFolder,
        string chartPackerSettingsFile,
        RecoveryStore recovery,
        InputService input) : IEditorWindowService
    {
        // The calibration tool, while it's open.
        private CalibrationWindow? _calibration;

        // The Input devices window, while it's open.
        private InputDevicesWindow? _inputDevices;

        /// <summary>Makes a new Editor the app's main window, opening <paramref name="args"/> in it once it's showing.</summary>
        /// <remarks>Called before the app has started, so the lifetime shows it.</remarks>
        public void StartWith(BuilderArgs args)
        {
            var (editor, viewModel) = CreateNewChart([]);

            editor.Opened += OnOpened;
            OpenEditor(editor, viewModel);

            void OnOpened(object? sender, EventArgs e)
            {
                editor.Opened -= OnOpened;
                // Posted, since it can swap the window (a song to open replaces the blank chart) and Opened is still running.
                Dispatcher.UIThread.Post(() => _ = StartAsync(viewModel, args));
            }
        }

        /// <summary>
        /// Opens what the command line asks for, or what the startup setting says, in the first Editor window, then
        /// offers the new charts a crash left autosaved but never saved.
        /// </summary>
        private async Task StartAsync(EditorWindowViewModel viewModel, BuilderArgs args)
        {
            switch (args)
            {
                case { Problem: { } problem }:
                    viewModel.Messages.Error(string.Format(CultureInfo.CurrentCulture, EditorWindowViewModel.ProblemFormat(problem.Kind), problem.Argument));
                    break;
                case { Start: BuilderStart.NewChart }:
                    await viewModel.StartNewChartAsync(args.AudioFiles);
                    break;
                case { Start: BuilderStart.OpenSong, SongPath: { } path }:
                    await viewModel.OpenSongAsync(path);
                    break;
                case { Start: BuilderStart.Calibrate }:
                    await ShowCalibrationAsync();
                    break;
                default:
                    if (settings.Settings.StartupAction == StartupAction.ReopenRecent
                        && settings.Settings.RecentSongs.FirstOrDefault() is { Path: string recent }
                        && !string.IsNullOrWhiteSpace(recent))
                        await viewModel.OpenSongAsync(recent);
                    break;
            }

            if (desktop.MainWindow is EditorWindow { DataContext: EditorWindowViewModel current })
                await current.OfferUnsavedChartsAsync();
        }

        public void OpenNewChart(IReadOnlyList<string> audioFiles)
        {
            var (editor, viewModel) = CreateNewChart(audioFiles);
            OpenEditor(editor, viewModel);
        }

        /// <summary>
        /// A new chart (charter-spec C1), with <paramref name="audioFiles"/>' tracks as its stems. It has no folder,
        /// and so no lock, until it's first saved. The chart starts on the default resolution, 120 BPM and 4/4 with a
        /// Lead part, all of which the Chart settings tab changes.
        /// </summary>
        private (EditorWindow Window, EditorWindowViewModel ViewModel) CreateNewChart(IReadOnlyList<string> audioFiles)
        {
            var chart = EditableChart.CreateNew(new NewChartSettings(
                "", settings.Settings.DefaultChartResolution, NewChartSettings.DefaultBpm, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.Guitar }, DrumsVariant.Pro));
            var audio = NewChartStems.TracksOf(audioFiles);
            var editor = new EditorWindow();
            return (editor, EditorWindowViewModel.ForNewChart(ServicesFor(editor), chart, audio));
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
            new AvaloniaFilePickerService(() => editor),
            new AvaloniaFirstSaveDialogService(() => editor),
            new AvaloniaExportSngDialogService(() => editor),
            new AvaloniaExportProgressDialogService(() => editor),
            new AvaloniaMessagesDialogService(() => editor),
            openOutput,
            latency,
            () => _ = ShowCalibrationAsync(),
            lockFolder,
            chartPackerSettingsFile,
            recovery,
            new AvaloniaAuthoringDialogService(() => editor),
            this,
            input,
            ShowInputDevices,
            new FileRevealService(() => editor));

        /// <summary>Shows the Editor, replacing the window open, and starts its audio.</summary>
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
        /// Opens the Input devices window (charter-spec C19 item 2), where devices are bound to the instruments. Only one is
        /// open at a time; opening it again brings that one to the front. It doesn't hold up the Editor, so a binding can
        /// be tried in a playtest while it's open.
        /// </summary>
        public void ShowInputDevices()
        {
            if (_inputDevices is { } open)
            {
                open.Activate();
                return;
            }

            var window = new InputDevicesWindow { DataContext = new InputDevicesViewModel(input) };
            var owner = desktop.Windows.LastOrDefault(w => w.IsActive) ?? desktop.MainWindow;
            _inputDevices = window;
            window.Closed += (_, _) => _inputDevices = null;
            if (owner is not null)
                WindowChrome.UseOwnerIcon(window, owner);
            window.Show();
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

            // The window this one replaces, if any: a new project keeps its screen, position, size and state.
            var previous = desktop.MainWindow as EditorWindow;
            if (previous is not null && !ReferenceEquals(previous, editor) && previous.WindowState != WindowState.Minimized)
                HandOff(previous, editor);
            else
                RestorePlacement(editor);

            // The song's audio and lock go with the window.
            editor.Closed += (_, _) => viewModel.Dispose();

            // Unsaved edits are asked about first: Save, Discard or Cancel (charter-spec C15 item 7). The close is held
            // while the user answers, and made again once they've chosen to go ahead.
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

            // A minimized editor doesn't say how it was shown or where it was.
            editor.Closing += (_, e) =>
            {
                if (e.Cancel)
                    return;

                if (WindowPlacement.TryCapture(editor, out var state, out var bounds))
                {
                    if (bounds is { } normal)
                    {
                        settings.Settings.EditorX = normal.Position.X;
                        settings.Settings.EditorY = normal.Position.Y;
                        settings.Settings.EditorWidth = normal.Size.Width;
                        settings.Settings.EditorHeight = normal.Size.Height;
                    }

                    settings.Settings.EditorMaximized = state != WindowState.Normal;
                    settings.Settings.EditorFullscreen = state == WindowState.FullScreen;
                }

                settings.Save();
            };

            // The window this one replaces closes once it's showing, so the app, which exits when its last window
            // closes, stays up.
            var replaced = desktop.MainWindow;
            Show(editor);
            if (replaced is not null && !ReferenceEquals(replaced, editor))
                replaced.Close();
        }

        /// <summary>
        /// Puts <paramref name="editor"/> where <paramref name="previous"/> is: the same screen, position and size,
        /// and the same state, fullscreen included. Opening a song or a new chart swaps windows in place.
        /// </summary>
        private static void HandOff(EditorWindow previous, EditorWindow editor)
        {
            editor.Width = previous.Width;
            editor.Height = previous.Height;
            editor.WindowStartupLocation = WindowStartupLocation.Manual;
            editor.Position = previous.Position;
            if (previous.WindowState != WindowState.Normal)
                editor.WindowState = previous.WindowState;
        }

        /// <summary>
        /// Puts <paramref name="editor"/> where it was when it last closed, or leaves it centered when that's
        /// unknown or no longer on a screen.
        /// </summary>
        private void RestorePlacement(EditorWindow editor)
        {
            var saved = settings.Settings;
            if (WindowBounds.FromSaved(saved.EditorX, saved.EditorY, saved.EditorWidth, saved.EditorHeight) is { } bounds)
                WindowPlacement.Restore(editor, bounds);
            ApplySavedState(editor);
        }

        /// <summary>Shows <paramref name="editor"/> maximized or fullscreen when it last closed that way.</summary>
        private void ApplySavedState(EditorWindow editor)
        {
            if (settings.Settings.EditorFullscreen)
                editor.WindowState = WindowState.FullScreen;
            else if (settings.Settings.EditorMaximized)
                editor.WindowState = WindowState.Maximized;
        }

        private void Show(Window window)
        {
            window.Show();
            desktop.MainWindow = window;
        }

        private AvaloniaSettingsWindowService SettingsWindowFor(Window owner) => new(
            () => owner,
            settingsPicker => new SettingsWindowViewModel(
                BuilderSettingsCatalog.Create(settings, () => _ = ShowCalibrationAsync(), () => _ = ShowReductionDefaultsAsync(), ShowInputDevices, ResetBindings), settingsPicker, settings.Save),
            settings.Settings,
            settings.Save,
            Strings.AppName);

        /// <summary>Puts every profile's bindings back to the keyboard's defaults, keeping each device's settings.</summary>
        private void ResetBindings()
        {
            var bindings = input.Settings.Settings;
            bindings.FiveFret = InputPresets.KeyboardFiveFret();
            bindings.Drums = InputPresets.KeyboardDrums();
            input.SaveBindings();
        }

        /// <summary>Opens the Reduce dialog's Settings mode over the window in front, which edits the default rules (charter-spec §10).</summary>
        private async Task ShowReductionDefaultsAsync()
        {
            var owner = desktop.Windows.LastOrDefault(w => w.IsActive) ?? desktop.MainWindow;
            await new AvaloniaAuthoringDialogService(() => owner).ShowReduceAsync(ReduceViewModel.ForDefaults(settings));
        }
    }
}
