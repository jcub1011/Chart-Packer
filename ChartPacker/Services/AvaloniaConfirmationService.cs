using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using ChartPacker.Views;

namespace ChartPacker.Services
{
    /// <summary>Shows a <see cref="ConfirmDialog"/> over the owner window. Avalonia has no built-in message box.</summary>
    public sealed class AvaloniaConfirmationService(Func<Window?> owner) : IConfirmationService
    {
        public async Task<bool> ConfirmAsync(string title, string message, string confirmLabel)
        {
            if (owner() is not { } window)
                return false;

            var dialog = new ConfirmDialog(title, message, confirmLabel);
            return await dialog.ShowDialog<bool>(window);
        }
    }
}
