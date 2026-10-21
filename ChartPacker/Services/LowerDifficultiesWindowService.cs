using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using ChartPacker.ViewModels;
using ChartPacker.Views;
using ChartShared.Audio.Playback;
using ChartShared.Core.Charting.Saving;
using ChartShared.Core.Scanning;
using ChartShared.Core.Settings;
using ChartShared.UI.Views;

namespace ChartPacker.Services
{
    /// <summary>Opens the Generate lower difficulties window (issue #120) for songs picked in the library.</summary>
    public interface ILowerDifficultiesWindowService
    {
        /// <summary>Whether the window can generate at all: there's somewhere to keep the charts it makes.</summary>
        bool IsAvailable { get; }

        /// <summary>Opens the window for <paramref name="entries"/>, and waits for it to close.</summary>
        Task ShowAsync(IReadOnlyList<SongEntry> entries);
    }

    /// <param name="drafts">The drafts the main window edits, which the new charts are staged in.</param>
    /// <param name="settings">The settings the window's player shares with the main one.</param>
    /// <param name="openOutput">Opens an audio device, for the window's own player.</param>
    /// <param name="mainDetail">
    /// The main window's panel. It lets go of its songs' files while the window is open, as the window may save them, and
    /// shows them again afterwards.
    /// </param>
    /// <param name="songFiles">Keeps saving a song apart from copies of it being downloaded.</param>
    /// <param name="generated">Keeps the charts the window makes until their songs are saved; none to make none.</param>
    /// <param name="reductionRules">The rules the difficulties are made by, shared with Chart Builder.</param>
    /// <param name="locks">The songs this process holds and watches, shared with the main window (charter-spec §4.8).</param>
    /// <param name="latency">The calibrated audio latency, which the preview's highways allow for.</param>
    public sealed class AvaloniaLowerDifficultiesWindowService(
        Func<Window?> owner,
        MetadataDraftStore drafts,
        SettingsService settings,
        Func<int, IAudioOutput> openOutput,
        SongDetailViewModel mainDetail,
        SongFileGate songFiles,
        GeneratedChartStash? generated,
        ReductionRulesFile reductionRules,
        SongLocks? locks = null,
        AudioLatencyFile? latency = null) : ILowerDifficultiesWindowService
    {
        public bool IsAvailable => generated is not null;

        public async Task ShowAsync(IReadOnlyList<SongEntry> entries)
        {
            if (owner() is not { } window || generated is not { } stash || entries.Count == 0)
                return;

            // Songs are read as many at once as a library scan reads them.
            int parallelism = Math.Max(settings.Settings.ScanParallelism, AppSettings.MinScanParallelism);
            var player = new PlayerViewModel(settings, openOutput, latency);
            try
            {
                using var viewModel = new LowerDifficultiesViewModel(entries, drafts, stash, reductionRules, new DraftSaver(songFiles), player, parallelism, locks);
                var dialog = new LowerDifficultiesWindow { DataContext = viewModel };

                // The main panel lets go of its songs' files, as the window may save them, and its player stops.
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
            }
            finally
            {
                player.Unload();
                await player.WhenFilesClosedAsync();
                player.Dispose();
            }
        }
    }
}
