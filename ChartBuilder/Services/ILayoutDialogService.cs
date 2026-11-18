using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using ChartBuilder.ViewModels;
using ChartBuilder.Views;
using ChartShared.Core.Charting.Layout;
using ChartShared.UI.Services;
using ChartShared.UI.Views;

namespace ChartBuilder.Services
{
    /// <summary>The layout templates' dialogs (issue 263): naming one, and Manage Layouts.</summary>
    public interface ILayoutDialogService
    {
        /// <summary>Asks for a layout's name, as <paramref name="viewModel"/> sets out: the name confirmed, trimmed, or null if cancelled.</summary>
        Task<string?> AskNameAsync(LayoutNameViewModel viewModel);

        /// <summary>
        /// Shows Manage Layouts for <paramref name="store"/>, finishing when it closes. A new layout, or one overwritten, is
        /// the lanes <paramref name="current"/> gives.
        /// </summary>
        Task ShowManageLayoutsAsync(LayoutTemplateStore store, Func<LaneDockState> current);
    }

    /// <summary>Shows the layout templates' dialogs over the owner window.</summary>
    public sealed class AvaloniaLayoutDialogService(Func<Window?> owner) : ILayoutDialogService
    {
        public async Task<string?> AskNameAsync(LayoutNameViewModel viewModel)
        {
            if (owner() is not { } window)
                return null;
            var dialog = new LayoutNameWindow { DataContext = viewModel };
            WindowChrome.UseOwnerIcon(dialog, window);
            return await dialog.ShowDialog<string?>(window);
        }

        public async Task ShowManageLayoutsAsync(LayoutTemplateStore store, Func<LaneDockState> current)
        {
            if (owner() is not { } window)
                return;

            // Its prompts are its own, over it, rather than over the Editor it's in front of.
            var dialog = new ManageLayoutsWindow();
            using var viewModel = new ManageLayoutsViewModel(store, current, new AvaloniaConfirmationService(() => dialog), new AvaloniaLayoutDialogService(() => dialog));
            dialog.DataContext = viewModel;
            WindowChrome.UseOwnerIcon(dialog, window);
            await dialog.ShowDialog(window);
        }
    }
}
