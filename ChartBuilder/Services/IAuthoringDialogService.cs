using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using ChartBuilder.ViewModels;
using ChartBuilder.Views;
using ChartPacker.Core.Charting.Editing;
using ChartPacker.UI.Views;

namespace ChartBuilder.Services
{
    /// <summary>The authoring tools' dialogs (charter-spec C9, C15), shown over the Editor.</summary>
    public interface IAuthoringDialogService
    {
        /// <summary>Shows the Reduce dialog for <paramref name="viewModel"/>, finishing when it closes.</summary>
        Task ShowReduceAsync(ReduceViewModel viewModel);

        /// <summary>Asks for the lane mapping of a paste between 5-fret and GHL: the one confirmed, or null if cancelled.</summary>
        Task<LaneMapping?> AskLaneMappingAsync(LaneMappingViewModel viewModel);

        /// <summary>
        /// Shows the highway preview in a window of its own beside the Editor, which can go full screen (charter-spec C18);
        /// one at a time, so showing it again brings it to the front.
        /// </summary>
        void ShowPreview(PreviewViewModel viewModel);
    }

    /// <summary>Shows the authoring dialogs over the owner window.</summary>
    public sealed class AvaloniaAuthoringDialogService(Func<Window?> owner) : IAuthoringDialogService
    {
        // The preview's window, while it's open.
        private PreviewWindow? _preview;

        public void ShowPreview(PreviewViewModel viewModel)
        {
            if (_preview is { } open)
            {
                open.Activate();
                return;
            }

            if (owner() is not { } window)
                return;
            var preview = new PreviewWindow { DataContext = viewModel };
            WindowChrome.UseOwnerIcon(preview, window);
            preview.Closed += (_, _) => _preview = null;
            _preview = preview;
            preview.Show(window);
        }

        public async Task ShowReduceAsync(ReduceViewModel viewModel)
        {
            if (owner() is not { } window)
            {
                viewModel.Cancel();
                return;
            }

            var dialog = new ReduceWindow { DataContext = viewModel };
            WindowChrome.UseOwnerIcon(dialog, window);
            await dialog.ShowDialog(window);
        }

        public async Task<LaneMapping?> AskLaneMappingAsync(LaneMappingViewModel viewModel)
        {
            if (owner() is not { } window)
                return null;
            var dialog = new LaneMappingWindow { DataContext = viewModel };
            WindowChrome.UseOwnerIcon(dialog, window);
            return await dialog.ShowDialog<LaneMapping?>(window);
        }
    }
}
