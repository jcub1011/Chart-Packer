using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using ChartPacker.Resources;
using ChartPacker.ViewModels;
using ChartPacker.Views;
using ChartShared.Audio.Playback;
using ChartShared.Core.Jobs;
using ChartShared.Core.Metadata;
using ChartShared.Core.Scanning;
using ChartShared.Core.Settings;
using ChartShared.UI.Services;
using ChartShared.UI.Views;

namespace ChartPacker.Services
{
    /// <param name="drafts">Says which songs have unsaved changes, which the merge reads past.</param>
    /// <param name="settings">The settings the preview's player and highway share with the main ones.</param>
    /// <param name="openOutput">Opens an audio device, for the preview's own player.</param>
    /// <param name="mainPlayer">The main window's player, stopped while the preview can play, which takes up the settings the preview's changed.</param>
    /// <param name="mainHighway">The main window's highway, which takes up the settings the preview's changed.</param>
    /// <param name="video">Reads background video lengths, for the merged song's <c>song_length</c>.</param>
    /// <param name="latency">The calibrated audio latency, which the preview's highway allows for.</param>
    /// <param name="isOpenInChartBuilder">Whether Chart Builder has the song at a path open, asked before a merge overwrites it.</param>
    public sealed class AvaloniaMergeWindowService(
        Func<Window?> owner,
        MetadataDraftStore drafts,
        SettingsService settings,
        Func<int, IAudioOutput> openOutput,
        PlayerViewModel mainPlayer,
        HighwayViewModel mainHighway,
        IImagePickerService imagePicker,
        IFilePickerService filePicker,
        IVideoDurationProbe video,
        AudioLatencyFile? latency = null,
        Func<string, bool>? isOpenInChartBuilder = null) : IMergeWindowService
    {
        public async Task<string?> ShowAsync(IReadOnlyList<SongEntry> entries, string outputFolder, OverwritePolicy policy)
        {
            if (owner() is not { } window)
                return null;

            if (mainPlayer.StopCommand.CanExecute(null))
                mainPlayer.StopCommand.Execute(null);

            // The preview is the inspector, read-only but for its highway's offsets, with a player, highway,
            // drafts and job of its own. The offsets it keeps in its draft are what the merge applies.
            var player = new PlayerViewModel(settings, openOutput, latency);
            var highway = new HighwayViewModel(settings, player);
            var previewDrafts = new MetadataDraftStore();
            var preview = new SongDetailViewModel(previewDrafts, new JobViewModel(new NoProgressWindow()), imagePicker, filePicker, player, highway, video)
            {
                PreviewNote = Strings.MergePreviewNote,
            };

            // Its prompt belongs to this window, so it comes up over it.
            var dialog = new MergeWindow();
            var viewModel = new MergeWindowViewModel(
                entries, [.. entries.Select(e => drafts.HasDrafts(e.FullPath))], outputFolder, policy, video, preview, previewDrafts, settings,
                new AvaloniaConfirmationService(() => dialog), isOpenInChartBuilder);
            dialog.DataContext = viewModel;
            _ = viewModel.LoadAsync();
            WindowChrome.UseOwnerIcon(dialog, window);
            await dialog.ShowDialog(window);
            // OnClosing captured the maximized state and split ratio into the settings; persist them now
            // rather than waiting for the main window to close, so they survive a crash.
            settings.Save();
            await viewModel.ShutdownAsync();
            mainPlayer.ReloadSettings();
            mainHighway.ReloadSettings();

            // However the window was closed, a song may already have been merged.
            return viewModel.MergedPath;
        }

        /// <summary>The preview never runs a job, so there's no progress to show.</summary>
        private sealed class NoProgressWindow : IJobProgressWindowService
        {
            public void Show(JobViewModel job)
            {
            }
        }
    }
}
