using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using ChartBuilder.ViewModels;
using ChartBuilder.Views;
using ChartPacker.Audio.Playback;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Launching;
using ChartPacker.Core.Scanning;
using ChartPacker.Services;
using ChartPacker.ViewModels.Settings;

namespace ChartBuilder.Services
{
    /// <summary>
    /// Chart Builder's top-level windows: the launcher (home screen) and the Editor. Only one is open at a time,
    /// and closing the Editor brings the launcher back.
    /// </summary>
    /// <param name="openOutput">Opens the audio output at a rate, for the Editor's playback.</param>
    /// <param name="lockFolder">Where the songs open are locked (<see cref="SongLock"/>).</param>
    public sealed class BuilderWindows(
        IClassicDesktopStyleApplicationLifetime desktop,
        BuilderSettingsService settings,
        Func<int, IAudioOutput> openOutput,
        string lockFolder) : IEditorWindowService
    {
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

        public void OpenEditor(IReadOnlyList<string> audioFiles)
        {
            var editor = new EditorWindow();
            Open(editor, new EditorWindowViewModel(settings, SettingsWindowFor(editor), audioFiles));
        }

        public void OpenSong(OpenedSong song, SongLock songLock)
        {
            var editor = new EditorWindow();
            var viewModel = new EditorWindowViewModel(settings, SettingsWindowFor(editor), song, songLock, openOutput);
            Open(editor, viewModel);
            _ = viewModel.Playback.OpenAsync();
        }

        private void Open(EditorWindow editor, EditorWindowViewModel viewModel)
        {
            editor.DataContext = viewModel;

            // The song's audio and lock go with the window.
            editor.Closed += (_, _) => viewModel.Dispose();

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
                lockFolder);
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
            settingsPicker => new SettingsWindowViewModel(BuilderSettingsCatalog.Create(settings), settingsPicker, settings.Save));
    }
}
