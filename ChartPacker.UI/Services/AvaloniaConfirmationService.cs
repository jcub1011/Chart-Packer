using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using ChartPacker.Views;

namespace ChartPacker.Services
{
    /// <summary>Shows a <see cref="ConfirmDialog"/> over the owner window. Avalonia has no built-in message box.</summary>
    public sealed class AvaloniaConfirmationService(Func<Window?> owner) : IConfirmationService
    {
        public async Task<bool> ConfirmAsync(string title, string message, string confirmLabel) =>
            await ShowAsync(new ConfirmDialog(title, message, confirmLabel)) == ConfirmChoice.First;

        public Task<ConfirmChoice> ChooseAsync(string title, string message, string firstLabel, string secondLabel) =>
            ShowAsync(new ConfirmDialog(title, message, firstLabel, secondLabel));

        private async Task<ConfirmChoice> ShowAsync(ConfirmDialog dialog)
        {
            if (owner() is not { } window)
                return ConfirmChoice.Cancel;

            WindowChrome.UseOwnerIcon(dialog, window);
            return await dialog.ShowDialog<ConfirmChoice>(window);
        }
    }
}
