using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using ChartPacker.Audio.Playback;
using ChartPacker.Core.Jobs;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Scanning;
using ChartPacker.Resources;
using ChartPacker.ViewModels;
using ChartPacker.Views;

namespace ChartPacker.Services
{
    /// <param name="drafts">Says which songs have unsaved changes, which the merge reads past.</param>
    /// <param name="settings">The settings the preview's player and highway share with the main ones.</param>
    /// <param name="openOutput">Opens an audio device, for the preview's own player.</param>
    /// <param name="mainPlayer">The main window's player, stopped while the preview can play, which takes up the settings the preview's changed.</param>
    /// <param name="mainHighway">The main window's highway, which takes up the settings the preview's changed.</param>
    /// <param name="video">Reads background video lengths, for the merged song's <c>song_length</c>.</param>
    public sealed class AvaloniaMergeWindowService(
        Func<Window?> owner,
        MetadataDraftStore drafts,
        SettingsService settings,
        Func<int, IAudioOutput> openOutput,
        PlayerViewModel mainPlayer,
        HighwayViewModel mainHighway,
        IImagePickerService imagePicker,
        IFilePickerService filePicker,
        IVideoDurationProbe video) : IMergeWindowService
    {
        public async Task<string?> ShowAsync(IReadOnlyList<SongEntry> entries, string outputFolder, OverwritePolicy policy)
        {
            if (owner() is not { } window)
                return null;

            if (mainPlayer.StopCommand.CanExecute(null))
                mainPlayer.StopCommand.Execute(null);

            // The preview is the inspector, read-only but for its highway's offsets, with a player, highway,
            // drafts and job of its own. The offsets it keeps in its draft are what the merge applies.
            var player = new PlayerViewModel(settings, openOutput);
            var highway = new HighwayViewModel(settings, player);
            var previewDrafts = new MetadataDraftStore();
            var preview = new SongDetailViewModel(previewDrafts, new JobViewModel(new NoProgressWindow()), imagePicker, filePicker, player, highway, video)
            {
                PreviewNote = Strings.MergePreviewNote,
            };

            var viewModel = new MergeWindowViewModel(entries, [.. entries.Select(e => drafts.HasDrafts(e.FullPath))], outputFolder, policy, video, preview, previewDrafts);
            var dialog = new MergeWindow { DataContext = viewModel };
            _ = viewModel.LoadAsync();
            WindowChrome.UseOwnerIcon(dialog, window);
            await dialog.ShowDialog(window);
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
