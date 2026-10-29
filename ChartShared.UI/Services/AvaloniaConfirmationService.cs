using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using ChartShared.UI.Views;

namespace ChartShared.UI.Services
{
    /// <summary>Shows a <see cref="ConfirmDialog"/> over the owner window. Avalonia has no built-in message box.</summary>
    public sealed class AvaloniaConfirmationService(Func<Window?> owner) : IConfirmationService
    {
        public async Task<bool> ConfirmAsync(string title, string message, string confirmLabel, string? confirmIcon = null) =>
            await ShowAsync(new ConfirmDialog(title, message, confirmLabel, confirmIcon: confirmIcon)) == ConfirmChoice.First;

        public Task<ConfirmChoice> ChooseAsync(string title, string message, string firstLabel, string secondLabel, string? firstIcon = null, string? secondIcon = null) =>
            ShowAsync(new ConfirmDialog(title, message, firstLabel, secondLabel, firstIcon, secondIcon));

        public Task AlertAsync(string title, string message) => ShowAsync(ConfirmDialog.Alert(title, message));

        private async Task<ConfirmChoice> ShowAsync(ConfirmDialog dialog)
        {
            if (owner() is not { } window)
                return ConfirmChoice.Cancel;

            WindowChrome.UseOwnerIcon(dialog, window);
            return await dialog.ShowDialog<ConfirmChoice>(window);
        }
    }
}
