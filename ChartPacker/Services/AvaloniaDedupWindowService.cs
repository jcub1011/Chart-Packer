using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using ChartPacker.ViewModels;
using ChartPacker.Views;
using ChartShared.Core.Dedup;
using ChartShared.Core.Trash;
using ChartShared.UI.Services;
using ChartShared.UI.Views;

namespace ChartPacker.Services
{
    /// <param name="drafts">Says which songs have unsaved changes, which are never deleted.</param>
    /// <param name="settings">Holds the keep preference.</param>
    /// <param name="mainPlayer">The main window's player, stopped so no song's files are held open while copies are deleted.</param>
    /// <param name="mergeWindowFor">Makes a Merge window service whose window belongs to the given owner.</param>
    /// <param name="logPath">The deletion log.</param>
    /// <param name="locks">Holds the copies while they're deleted (charter-spec §4.8).</param>
    public sealed class AvaloniaDedupWindowService(
        Func<Window?> owner,
        MetadataDraftStore drafts,
        SettingsService settings,
        PlayerViewModel mainPlayer,
        Func<Func<Window?>, IMergeWindowService> mergeWindowFor,
        string logPath,
        SongLocks? locks = null) : IDedupWindowService
    {
        public async Task<DedupWindowResult> ShowAsync(DedupRequest request)
        {
            if (owner() is not { } window)
                return DedupWindowResult.None;

            if (mainPlayer.StopCommand.CanExecute(null))
                mainPlayer.StopCommand.Execute(null);
            await mainPlayer.WhenFilesClosedAsync();

            var dialog = new DedupWindow();
            nint handle = 0;
            dialog.Opened += (_, _) => handle = dialog.TryGetPlatformHandle()?.Handle ?? 0;

            // The prompts and the Merge window it opens belong to this window, so they come up over it.
            var viewModel = new DedupWindowViewModel(
                request,
                drafts.HasDrafts,
                settings,
                new AvaloniaConfirmationService(() => dialog),
                mergeWindowFor(() => dialog),
                SystemTrash.ForCurrentOS(() => handle),
                new DeletionLog(logPath),
                locks);
            dialog.DataContext = viewModel;

            _ = viewModel.AnalyzeAsync();
            WindowChrome.UseOwnerIcon(dialog, window);
            await dialog.ShowDialog(window);
            await viewModel.ShutdownAsync();
            return viewModel.Result;
        }
    }
}
