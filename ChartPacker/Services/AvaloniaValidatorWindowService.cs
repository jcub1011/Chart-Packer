using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using ChartPacker.Audio.Playback;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Scanning;
using ChartPacker.ViewModels;
using ChartPacker.Views;

namespace ChartPacker.Services
{
    /// <param name="drafts">The drafts the main window edits, which the Validator's inspector shares.</param>
    /// <param name="job">The main window's job: nothing is saved while one runs.</param>
    /// <param name="settings">The settings the inspector's player and highway share with the main ones.</param>
    /// <param name="openOutput">Opens an audio device, for the inspector's own player.</param>
    /// <param name="mainPlayer">The main window's player, stopped while the Validator can play, which takes up the settings it changed.</param>
    /// <param name="mainHighway">The main window's highway, which takes up the settings the Validator's changed.</param>
    /// <param name="notice">Where downloads report, under the main window's toolbar.</param>
    /// <param name="songFiles">Keeps saving a song apart from copies of it being downloaded, in either window.</param>
    public sealed class AvaloniaValidatorWindowService(
        Func<Window?> owner,
        MetadataDraftStore drafts,
        JobViewModel job,
        SettingsService settings,
        Func<int, IAudioOutput> openOutput,
        PlayerViewModel mainPlayer,
        HighwayViewModel mainHighway,
        IVideoDurationProbe video,
        NoticeViewModel notice,
        SongFileGate songFiles) : IValidatorWindowService
    {
        public async Task ShowAsync(IReadOnlyList<SongEntry> entries)
        {
            if (owner() is not { } window)
                return;

            if (mainPlayer.StopCommand.CanExecute(null))
                mainPlayer.StopCommand.Execute(null);

            // The inspector is the main one in full, with a player and highway of its own. Its pickers and prompts
            // belong to this window, so they come up over it; its drafts and file gate are the main window's.
            var dialog = new ValidatorWindow();
            var confirmation = new AvaloniaConfirmationService(() => dialog);
            var saver = new DraftSaver(songFiles);
            var player = new PlayerViewModel(settings, openOutput);
            var highway = new HighwayViewModel(settings, player);
            var inspector = new SongDetailViewModel(
                drafts,
                job,
                new AvaloniaImagePickerService(() => dialog),
                new AvaloniaFilePickerService(() => dialog),
                player,
                highway,
                video,
                new DownloadService(settings, () => dialog, notice, songFiles),
                new FileRevealService(() => dialog),
                songFiles,
                confirmation,
                saver);

            // Songs are read as many at once as a library scan reads.
            int parallelism = Math.Max(settings.Settings.ScanParallelism, AppSettings.MinScanParallelism);
            var viewModel = new ValidatorWindowViewModel(entries, drafts, inspector, parallelism, job, saver, confirmation);
            dialog.DataContext = viewModel;
            _ = viewModel.LoadAsync();
            WindowChrome.UseOwnerIcon(dialog, window);
            await dialog.ShowDialog(window);

            await viewModel.ShutdownAsync();
            mainPlayer.ReloadSettings();
            mainHighway.ReloadSettings();
        }
    }
}
