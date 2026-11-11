using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using ChartPacker.Resources;
using ChartPacker.ViewModels;
using ChartPacker.Views;
using ChartShared.Audio.Encoding;
using ChartShared.Core.Optimization;
using ChartShared.Core.Scanning;
using ChartShared.Core.Settings;
using ChartShared.UI.Services;
using ChartShared.UI.Views;
using ChartShared.Video;

namespace ChartPacker.Services
{
    /// <param name="drafts">Says which songs have unsaved changes, which are left alone.</param>
    /// <param name="mainDetail">
    /// The main window's panel. It lets go of its songs' files while the window is open, as the window rewrites them, and
    /// shows them again afterwards.
    /// </param>
    /// <param name="songFiles">Keeps rewriting a song apart from copies of it being downloaded.</param>
    /// <param name="defaults">The defaults the window starts from; the path is <see cref="UserConfigPaths.OptimizerSettingsFile"/>.</param>
    /// <param name="locks">The songs this process holds and watches, shared with the main window (charter-spec §4.8).</param>
    public sealed class AvaloniaOptimizeWindowService(
        Func<Window?> owner,
        MetadataDraftStore drafts,
        SongDetailViewModel mainDetail,
        SongFileGate songFiles,
        OptimizeSettingsFile defaults,
        SongLocks? locks = null) : IOptimizeWindowService
    {
        // The backends are made fresh for each run's parallelism, but what they can do is the same throughout.
        private static readonly OptimizeBackends Backends = new(
            parallelism => new OptimizerSet(new OpusAudioOptimizer(), new VideoOptimizer(expectedConcurrency: parallelism), new AvaloniaImageOptimizer()),
            new AssetProbes(Video: new FfmpegVideoProbe(), Image: new ImageHeaderProbe()),
            UnavailableReason);

        public async Task<OptimizeWindowResult> ShowAsync(OptimizeRequest request)
        {
            if (owner() is not { } window)
                return OptimizeWindowResult.None;

            var dialog = new OptimizeWindow();
            var services = new OptimizeJobServices(locks?.Set, songFiles, drafts.HasDrafts);

            // The prompts and the folder picker belong to this window, so they come up over it.
            var viewModel = new OptimizeWindowViewModel(
                request, defaults, Backends, services, new AvaloniaConfirmationService(() => dialog), new AvaloniaFolderPickerService(() => dialog));
            dialog.DataContext = viewModel;

            // The main panel lets go of its songs' files, as the window rewrites them, and its player stops.
            await mainDetail.SuspendAsync();
            try
            {
                _ = viewModel.LoadAsync();
                WindowChrome.UseOwnerIcon(dialog, window);
                await dialog.ShowDialog(window);
                await viewModel.ShutdownAsync();
            }
            finally
            {
                mainDetail.Player.ReloadSettings();
                await mainDetail.ResumeAsync();
            }

            return viewModel.Result;
        }

        private static string? UnavailableReason(OptimizeStrategy strategy) => strategy switch
        {
            OptimizeStrategy.Video => VideoReencoder.UnavailableReason,
            OptimizeStrategy.AlbumArt or OptimizeStrategy.BackgroundArt => new AvaloniaImageOptimizer().IsAvailable ? null : Strings.OptimizeImageUnavailable,
            OptimizeStrategy.Audio or OptimizeStrategy.StemMixdown => new OpusAudioOptimizer().IsAvailable ? null : Strings.OptimizeAudioUnavailable,
            _ => null,
        };
    }
}
