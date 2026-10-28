using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using ChartShared.UI.Resources;
using ChartShared.UI.Services;
using ChartShared.UI.ViewModels.Settings;
using ChartShared.UI.Views;
using Serilog;

namespace ChartBuilder.Services
{
    /// <summary>
    /// Chart Builder's top-level window: the Editor, one for the app's lifetime. Opening a song or a new chart swaps
    /// what it edits, in place; closing it exits the app.
    /// </summary>
    /// <param name="openOutput">Opens the audio output at a rate, for the Editor's playback and the calibration tool.</param>
    /// <param name="latency">The audio/visual latency, which the calibration tool measures.</param>
    /// <param name="chartPackerSettingsFile">Chart Packer's settings, which a save reads for the library and packing, never writes.</param>
    /// <param name="recovery">Where the Editor autosaves the chart open, and where what's left to recover is found on startup.</param>
    /// <param name="input">The instrument devices and their bindings (charter-spec C19).</param>
    public sealed class BuilderWindows(
        IClassicDesktopStyleApplicationLifetime desktop,
        BuilderSettingsService settings,
        Func<int, IAudioOutput> openOutput,
        AudioLatencyService latency,
        string chartPackerSettingsFile,
        RecoveryStore recovery,
        InputService input) : IEditorWindowService
    {
        // The calibration tool, shared with Chart Packer, which pauses the Editor's playback as it opens, so its notes are
        // the only ones moving, and stops a Bind waiting in the Input devices window, so its taps don't bind a control. Its
        // audio tap test hears every instrument device.
        private readonly CalibrationWindowService _calibration = new(
            openOutput,
            latency,
            () =>
            {
                if (desktop.MainWindow is EditorWindow { DataContext: EditorWindowViewModel editor })
                    editor.Playback.Pause();
                if (desktop.Windows.OfType<InputDevicesWindow>().FirstOrDefault() is { DataContext: InputDevicesViewModel devices })
                    devices.StopBinding();
            },
            () => new CalibrationTapInput(input));

        private static ILogger Logger => Log.ForContext<BuilderWindows>();

        // How long WaitForRenderAsync waits for the window's next frame.
        private static readonly TimeSpan FrameWaitLimit = TimeSpan.FromMilliseconds(100);

        // The Input devices window, while it's open.
        private InputDevicesWindow? _inputDevices;

        // The Editor window, made as the app starts; what its Editors work with, the same for every one opened in it; and
        // its authoring dialogs, whose preview window follows the Editor open.
        private EditorWindow? _editor;
        private EditorServices? _services;
        private AvaloniaAuthoringDialogService? _authoring;

        private EditorServices Services => _services ?? throw new InvalidOperationException("The Editor window isn't open yet.");

        /// <summary>Makes the Editor the app's main window, opening <paramref name="args"/> in it once it's showing.</summary>
        /// <remarks>Called before the app has started, so the lifetime shows it.</remarks>
        public void StartWith(BuilderArgs args)
        {
            var editor = new EditorWindow();
            _editor = editor;
            _authoring = new AvaloniaAuthoringDialogService(() => editor);
            _services = ServicesFor(editor, _authoring);
            WatchWindow(editor);

            var viewModel = CreateNewChart([]);
            editor.DataContext = viewModel;
            editor.Opened += OnOpened;
            Show(editor);
            _ = viewModel.Playback.OpenAsync();

            void OnOpened(object? sender, EventArgs e)
            {
                editor.Opened -= OnOpened;
                // Posted, since it can swap what's open (a song to open replaces the blank chart) and Opened is still running.
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
                default:
                    if (settings.Settings.StartupAction == StartupAction.ReopenRecent
                        && settings.Settings.RecentSongs.FirstOrDefault() is { Path: string recent }
                        && !string.IsNullOrWhiteSpace(recent))
                        await viewModel.OpenSongAsync(recent);
                    break;
            }

            if (_editor?.DataContext is EditorWindowViewModel current)
                await current.OfferUnsavedChartsAsync();
        }

        public void OpenNewChart(IReadOnlyList<string> audioFiles) => Load(() => CreateNewChart(audioFiles));

        /// <summary>
        /// A new chart (charter-spec C1), with <paramref name="audioFiles"/>' tracks as its stems. It has no folder,
        /// and so no lock, until it's first saved. The chart starts on the default resolution, 120 BPM and 4/4 with a
        /// Lead part, all of which the Chart settings tab changes.
        /// </summary>
        private EditorWindowViewModel CreateNewChart(IReadOnlyList<string> audioFiles)
        {
            var chart = EditableChart.CreateNew(new NewChartSettings(
                "", settings.Settings.DefaultChartResolution, NewChartSettings.DefaultBpm, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.Guitar }, DrumsVariant.Pro));
            var audio = NewChartStems.TracksOf(audioFiles);
            return EditorWindowViewModel.ForNewChart(Services, chart, audio);
        }

        public void OpenSong(OpenedSong song, SongLock songLock) =>
            Load(() => EditorWindowViewModel.ForSong(Services, song, songLock), songLock);

        public void OpenRecovered(EditorSong song, SongLock? songLock, string recoveryKey) =>
            Load(() => EditorWindowViewModel.ForRecovered(Services, song, songLock, recoveryKey), songLock);

        /// <summary>What the Editors in <paramref name="editor"/> work with, their prompts and pickers shown over it.</summary>
        private EditorServices ServicesFor(EditorWindow editor, AvaloniaAuthoringDialogService authoring) => new(
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
            chartPackerSettingsFile,
            recovery,
            authoring,
            this,
            input,
            ShowInputDevices,
            new FileRevealService(() => editor));

        /// <summary>
        /// Opens the Editor <paramref name="create"/> makes in the Editor window in place of what's open, letting go of that
        /// one's song, and starts its audio. The window stays as it is: its screen, position, size and state.
        /// </summary>
        /// <param name="songLock">
        /// The lock the new Editor is given, let go of here if the window has closed by the time it's opened, as nothing
        /// is opened then.
        /// </param>
        private void Load(Func<EditorWindowViewModel> create, SongLock? songLock = null)
        {
            if (_editor is not { } editor)
            {
                Logger.Information("The Editor window closed before what was opening in it was ready");
                songLock?.Dispose();
                return;
            }

            var time = Stopwatch.StartNew();

            // The new Editor starts from the layout saved, so the one open saves its own first.
            editor.CaptureLayout();
            var viewModel = create();
            long made = time.ElapsedMilliseconds;

            // The views move on to the new Editor before the old one is disposed, so none is left on a disposed one.
            var previous = editor.DataContext as EditorWindowViewModel;
            editor.DataContext = viewModel;

            // From here the new Editor is the one on screen, so what goes wrong is said on its message bar.
            try
            {
                _authoring?.Follow(viewModel.Editor.Preview);
                _authoring?.Follow(viewModel.Editor.Difficulty);
                long bound = time.ElapsedMilliseconds;
                previous?.Dispose();
                long disposed = time.ElapsedMilliseconds;
                _ = viewModel.Playback.OpenAsync();

                // The frame after the next has the new Editor laid out and drawn, which the next one starts on.
                editor.RequestAnimationFrame(_ => editor.RequestAnimationFrame(_ => Logger.Information(
                    "Opened {Title} in the Editor: made in {MadeMs} ms, bound in {BoundMs} ms, the last disposed in {DisposedMs} ms, drawn after {DrawnMs} ms",
                    viewModel.Song.Title, made, bound - made, disposed - bound, time.ElapsedMilliseconds)));
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Opening {Title} in the Editor failed after it was shown", viewModel.Song.Title);
                viewModel.Messages.Error(string.Format(CultureInfo.CurrentCulture, Strings.UnexpectedErrorFormat, ex.Message));
            }
        }

        /// <summary>
        /// Opens the calibration tool (charter-spec C11) over the window in front, pausing the Editor's playback. Only
        /// one is open at a time; opening it again brings that one to the front.
        /// </summary>
        public async Task ShowCalibrationAsync() =>
            await _calibration.ShowAsync(desktop.Windows.LastOrDefault(w => w.IsActive) ?? desktop.MainWindow);

        public async Task WaitForRenderAsync()
        {
            if (_editor is not { IsVisible: true } editor)
                return;

            // The next frame's callbacks run before its changes go to the renderer, in the same pass, so a yield after
            // them finds the frame handed over. Not waited on for long: a window that isn't drawing, minimized say,
            // may not ask for frames.
            var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            editor.RequestAnimationFrame(_ => frame.TrySetResult());
            await Task.WhenAny(frame.Task, Task.Delay(FrameWaitLimit));
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        }

        /// <summary>
        /// Opens the calibration tool's input-device test (charter-spec C11 item 2) for <paramref name="device"/> over the
        /// Input devices window, pausing the Editor's playback, so its notes are the only ones moving.
        /// </summary>
        /// <returns>Whether it ran: false when the tool was open already, which is brought to the front instead.</returns>
        private Task<bool> ShowDeviceCalibrationAsync(InputDevicesWindow owner, InputDeviceInfo device) =>
            _calibration.ShowDeviceAsync(owner, new DeviceCalibrationInput(input, device));

        /// <summary>
        /// Opens the Input devices window (charter-spec C19 item 2), where devices are bound to the instruments, centred over
        /// the Editor and kept above it. Only one is open at a time; opening it again brings that one to the front. It
        /// doesn't hold up the Editor, so a binding can be tried in a playtest while it's open.
        /// </summary>
        /// <remarks>
        /// The Editor owns it, never the window in front, which may be the Settings dialog it was opened from: an owned
        /// window closes with its owner. It stays open as a song or a new chart opens, as that's done in the same window.
        /// </remarks>
        public void ShowInputDevices()
        {
            if (_inputDevices is { } open)
            {
                open.Activate();
                return;
            }

            var window = new InputDevicesWindow();
            window.DataContext = new InputDevicesViewModel(input, device => ShowDeviceCalibrationAsync(window, device));
            var owner = desktop.MainWindow;
            _inputDevices = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_inputDevices, window))
                    _inputDevices = null;
            };
            if (owner is null)
            {
                window.Show();
                return;
            }

            WindowChrome.UseOwnerIcon(window, owner);
            window.Show(owner);
        }

        /// <summary>
        /// Offers the calibration tool the first time a song plays on a device that has no calibrated latency, once for
        /// each device, unless the user turned that off.
        /// </summary>
        private void OfferCalibration(EditorWindow editor)
        {
            if (!settings.Settings.PromptForCalibration
                || _calibration.IsOpen
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
            string name = string.IsNullOrEmpty(device.Name) ? SharedStrings.CalibrationUnknownDevice : device.Name;
            if (await confirmations.ConfirmAsync(
                    Strings.CalibrationPromptTitle,
                    string.Format(CultureInfo.CurrentCulture, Strings.CalibrationPromptFormat, name),
                    Strings.CalibrationPromptConfirm))
                await ShowCalibrationAsync();
        }

        /// <summary>
        /// Follows the Editor window for as long as it's open: the calibration offer as the audio's device changes, and
        /// the Editor open's unsaved edits and layout as it closes. Where the window was, WindowMemory keeps.
        /// </summary>
        private void WatchWindow(EditorWindow editor)
        {
            // The song's output says which device it's on, which may never have been calibrated.
            void OnLatencyChanged(object? sender, EventArgs e) => OfferCalibration(editor);
            latency.Changed += OnLatencyChanged;

            // The song's audio and lock go with the Editor open when the window closes. Nothing opens in it after that.
            editor.Closed += (_, _) =>
            {
                latency.Changed -= OnLatencyChanged;
                (editor.DataContext as EditorWindowViewModel)?.Dispose();
                if (ReferenceEquals(_editor, editor))
                    _editor = null;
            };

            // Unsaved edits are asked about first: Save, Discard or Cancel (charter-spec C15 item 7). The close is held
            // while the user answers, and made again once they've chosen to go ahead.
            bool closeAllowed = false;
            editor.Closing += (_, e) =>
            {
                if (closeAllowed || e.Cancel || editor.DataContext is not EditorWindowViewModel viewModel
                    || !viewModel.HasUnsavedChanges && !viewModel.IsSaving)
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

            // Anything else the settings hold that's changed, on every close, even one cancelled above, as the window's
            // placement and layout are: the OS won't wait for an answer. With nothing changed, nothing's written.
            editor.Closing += (_, _) => settings.Save();
        }

        private void Show(Window window)
        {
            window.Show();
            desktop.MainWindow = window;
        }

        private AvaloniaSettingsWindowService SettingsWindowFor(Window owner) => new(
            () => owner,
            settingsPicker => new SettingsWindowViewModel(
                BuilderSettingsCatalog.Create(settings, latency, () => _ = ShowCalibrationAsync(), () => _ = ShowReductionDefaultsAsync(), ShowInputDevices, ResetBindings), settingsPicker, settings.Save),
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
