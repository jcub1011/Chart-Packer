using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using ChartBuilder.Controls;
using ChartBuilder.ViewModels;
using ChartBuilder.Views;
using ChartShared.Core.Charting.Editing;
using ChartShared.UI.Views;

namespace ChartBuilder.Services
{
    /// <summary>The authoring tools' dialogs (charter-spec C9, C15), shown over the Editor.</summary>
    public interface IAuthoringDialogService
    {
        /// <summary>Shows the Reduce dialog for <paramref name="viewModel"/>, finishing when it closes.</summary>
        Task ShowReduceAsync(ReduceViewModel viewModel);

        /// <summary>Asks for the lane mapping of a paste between 5-fret and GHL: the one confirmed, or null if cancelled.</summary>
        Task<LaneMapping?> AskLaneMappingAsync(LaneMappingViewModel viewModel);

        /// <summary>Asks for Generate fills' options, picked in <paramref name="viewModel"/>: whether Generate was pressed.</summary>
        Task<bool> AskGenerateFillsAsync(GenerateFillsViewModel viewModel);

        /// <summary>Asks for Generate star power's options, picked in <paramref name="viewModel"/>: whether Generate was pressed.</summary>
        Task<bool> AskGenerateStarPowerAsync(GenerateStarPowerViewModel viewModel);

        /// <summary>
        /// Shows the highway preview in a window of its own beside the Editor, which can go full screen (charter-spec C18);
        /// one at a time, so showing it again brings it to the front.
        /// </summary>
        void ShowPreview(PreviewViewModel viewModel);

        /// <summary>
        /// Shows the difficulty analysis in a window of its own beside the Editor; one at a time, so showing it again brings
        /// it to the front.
        /// </summary>
        void ShowDifficulty(DifficultyViewModel viewModel);
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
            ShareLayout(preview, window);
            preview.Closed += (_, _) => _preview = null;
            _preview = preview;
            preview.Show(window);
        }

        /// <summary>
        /// Moves the preview's window, if it's open, on to <paramref name="viewModel"/>, the preview of the song just
        /// opened in the Editor, so it stays open showing that song.
        /// </summary>
        public void Follow(PreviewViewModel viewModel)
        {
            if (_preview is { } open)
            {
                open.DataContext = viewModel;
                if (owner() is { } window)
                    ShareLayout(open, window);
            }
        }

        // The difficulty analysis's window, while it's open.
        private DifficultyWindow? _difficulty;

        public void ShowDifficulty(DifficultyViewModel viewModel)
        {
            if (_difficulty is { } open)
            {
                open.Activate();
                return;
            }

            if (owner() is not { } window)
                return;
            var difficulty = new DifficultyWindow { DataContext = viewModel };
            WindowChrome.UseOwnerIcon(difficulty, window);
            ShareLayout(difficulty, window);
            difficulty.Closed += (_, _) => _difficulty = null;
            _difficulty = difficulty;
            difficulty.Show(window);
        }

        /// <summary>
        /// Moves the difficulty analysis's window, if it's open, on to <paramref name="viewModel"/>, the analysis of the song
        /// just opened in the Editor, so it stays open showing that song.
        /// </summary>
        public void Follow(DifficultyViewModel viewModel)
        {
            if (_difficulty is { } open)
            {
                open.DataContext = viewModel;
                if (owner() is { } window)
                    ShareLayout(open, window);
            }
        }

        // A tab in a window of its own has its sections arranged as they are in the Editor's panel, and arranging them in
        // either arranges both.
        private static void ShareLayout(Window tab, Window editor) => PanelLayout.SetStore(tab, PanelLayout.GetStore(editor));

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

        public Task<bool> AskGenerateFillsAsync(GenerateFillsViewModel viewModel) => AskAsync(new GenerateFillsWindow { DataContext = viewModel });

        public Task<bool> AskGenerateStarPowerAsync(GenerateStarPowerViewModel viewModel) => AskAsync(new GenerateStarPowerWindow { DataContext = viewModel });

        // Shows a dialog that closes with whether it was confirmed; false when there's no window to show it over.
        private async Task<bool> AskAsync(Window dialog)
        {
            if (owner() is not { } window)
                return false;
            WindowChrome.UseOwnerIcon(dialog, window);
            return await dialog.ShowDialog<bool>(window);
        }
    }
}
