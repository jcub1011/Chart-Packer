using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using ChartBuilder.ViewModels;
using ChartBuilder.Views;
using ChartShared.UI.Views;

namespace ChartBuilder.Services
{
    /// <summary>Shows what the Editor has said this session, from its message bar.</summary>
    public interface IMessagesDialogService
    {
        /// <summary>Shows <paramref name="viewModel"/> until the user closes it.</summary>
        Task ShowAsync(MessagesDialogViewModel viewModel);
    }

    /// <summary>Shows a <see cref="MessagesWindow"/> over the owner window.</summary>
    public sealed class AvaloniaMessagesDialogService(Func<Window?> owner) : IMessagesDialogService
    {
        public async Task ShowAsync(MessagesDialogViewModel viewModel)
        {
            if (owner() is not { } window)
                return;

            var dialog = new MessagesWindow { DataContext = viewModel };
            WindowChrome.UseOwnerIcon(dialog, window);
            await dialog.ShowDialog(window);
        }
    }
}
