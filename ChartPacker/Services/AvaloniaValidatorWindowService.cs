using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Controls;
using ChartPacker.Resources;
using ChartPacker.ViewModels;
using ChartPacker.Views;
using ChartShared.Audio.Playback;
using ChartShared.Core.Metadata;
using ChartShared.Core.Scanning;
using ChartShared.Core.Settings;
using ChartShared.UI.Services;
using ChartShared.UI.Views;

namespace ChartPacker.Services
{
    /// <param name="drafts">The drafts the main window edits, which the Validator's inspector shares.</param>
    /// <param name="job">The main window's job: nothing is saved while one runs.</param>
    /// <param name="settings">The settings the inspector's player and highway share with the main ones.</param>
    /// <param name="openOutput">Opens an audio device, for the inspector's own player.</param>
    /// <param name="mainDetail">
    /// The main window's panel. It lets go of its songs' files while the Validator is open, as the Validator may save
    /// them, and shows them again afterwards; its player and highway then take up the settings the Validator's changed.
    /// </param>
    /// <param name="notice">Where downloads report, along the bottom of the main window.</param>
    /// <param name="songFiles">Keeps saving a song apart from copies of it being downloaded, in either window.</param>
    /// <param name="latency">The calibrated audio latency, which the inspector's highway allows for.</param>
    public sealed class AvaloniaValidatorWindowService(
        Func<Window?> owner,
        MetadataDraftStore drafts,
        JobViewModel job,
        SettingsService settings,
        Func<int, IAudioOutput> openOutput,
        SongDetailViewModel mainDetail,
        IVideoDurationProbe video,
        NoticeViewModel notice,
        SongFileGate songFiles,
        Func<string, bool>? isOpenInChartBuilder = null,
        AudioLatencyFile? latency = null) : IValidatorWindowService
    {
        // How long the check runs before its progress is shown, so a quick one doesn't flash a dialog.
        private static readonly TimeSpan ProgressDelay = TimeSpan.FromMilliseconds(300);

        /// <summary>
        /// Checks <paramref name="entries"/>, and opens the Validator window once one fails, or says that none do. A
        /// check that takes a while shows its progress, and can be canceled, until then.
        /// </summary>
        public async Task ShowAsync(IReadOnlyList<SongEntry> entries)
        {
            if (owner() is not { } window)
                return;

            // The inspector is the main one in full, with a player and highway of its own. Its pickers and prompts
            // belong to this window, so they come up over it; its drafts and file gate are the main window's.
            var dialog = new ValidatorWindow();
            var confirmation = new AvaloniaConfirmationService(() => dialog);
            var saver = new DraftSaver(songFiles);
            var player = new PlayerViewModel(settings, openOutput, latency);
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
                saver,
                isOpenInChartBuilder,
                new AvaloniaChartIssuesWindowService(() => dialog));

            // Songs are read as many at once as a library scan reads.
            int parallelism = Math.Max(settings.Settings.ScanParallelism, AppSettings.MinScanParallelism);
            var viewModel = new ValidatorWindowViewModel(entries, drafts, inspector, parallelism, job, saver, settings, confirmation, isOpenInChartBuilder);
            dialog.DataContext = viewModel;
            _ = viewModel.LoadAsync();

            if (!await WaitForFirstFailureAsync(viewModel, window))
            {
                await viewModel.ShutdownAsync();
                return;
            }

            bool found;
            try
            {
                found = await viewModel.FirstFailureFound;
            }
            catch (Exception ex)
            {
                await viewModel.ShutdownAsync();
                string error = string.Format(CultureInfo.CurrentCulture, Strings.ValidatorCheckFailedFormat, ex.Message);
                var failed = new MessageDialog(Strings.ValidatorWindowTitle, error);
                WindowChrome.UseOwnerIcon(failed, window);
                await failed.ShowDialog(window);
                return;
            }

            if (!found)
            {
                await viewModel.ShutdownAsync();
                string message = string.Format(CultureInfo.CurrentCulture, Strings.ValidatorNoIssuesFormat, entries.Count);
                var done = new MessageDialog(Strings.ValidatorWindowTitle, message);
                WindowChrome.UseOwnerIcon(done, window);
                await done.ShowDialog(window);
                return;
            }

            // The rest of the songs go on being checked in the window.
            await mainDetail.SuspendAsync();
            WindowChrome.UseOwnerIcon(dialog, window);
            await dialog.ShowDialog(window);
            // OnClosing captured the splitter position into the settings; persist it now rather than waiting for
            // the main window to close, so it survives a crash.
            settings.Save();

            await viewModel.ShutdownAsync();
            mainDetail.Player.ReloadSettings();
            mainDetail.Highway.ReloadSettings();
            await mainDetail.ResumeAsync();
        }

        /// <summary>
        /// Waits until the check finds a song that fails or finishes without one, showing its progress if that takes
        /// a while. Returns <see langword="false"/> when the progress is canceled, which stops the check.
        /// </summary>
        private static async Task<bool> WaitForFirstFailureAsync(ValidatorWindowViewModel viewModel, Window window)
        {
            var settled = viewModel.FirstFailureFound;
            if (await Task.WhenAny(settled, Task.Delay(ProgressDelay)) == settled)
                return true;

            var progress = new ValidatorProgressDialog { DataContext = viewModel };
            WindowChrome.UseOwnerIcon(progress, window);
            var shown = progress.ShowDialog(window);
            await Task.WhenAny(settled, shown);

            // Only Cancel closes the dialog before this does, and it wins over a song failing at the same moment.
            if (shown.IsCompleted)
            {
                viewModel.CancelCheck();
                return false;
            }

            progress.Close();
            await shown;
            return true;
        }
    }
}
