using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using ChartBuilder.ViewModels;
using ChartBuilder.Views;
using ChartPacker.UI.Views;

namespace ChartBuilder.Services
{
    /// <summary>Shows the Export to <c>.sng</c> dialog: preview, location and if-exists choice before anything is written.</summary>
    public interface IExportSngDialogService
    {
        /// <summary>Shows <paramref name="viewModel"/>; true once the user chose Export, false if they cancelled.</summary>
        Task<bool> AskAsync(ExportSngViewModel viewModel);
    }

    /// <summary>Shows an <see cref="ExportSngWindow"/> over the owner window.</summary>
    public sealed class AvaloniaExportSngDialogService(Func<Window?> owner) : IExportSngDialogService
    {
        public async Task<bool> AskAsync(ExportSngViewModel viewModel)
        {
            if (owner() is not { } window)
                return false;

            var dialog = new ExportSngWindow { DataContext = viewModel };
            WindowChrome.UseOwnerIcon(dialog, window);
            return await dialog.ShowDialog<bool>(window);
        }
    }
}
