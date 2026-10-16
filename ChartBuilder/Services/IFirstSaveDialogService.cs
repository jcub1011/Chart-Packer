using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using ChartBuilder.ViewModels;
using ChartBuilder.Views;
using ChartShared.UI.Views;

namespace ChartBuilder.Services
{
    /// <summary>Asks where a new chart's first save makes its song folder (charter-spec C1, The first save).</summary>
    public interface IFirstSaveDialogService
    {
        /// <summary>Shows <paramref name="viewModel"/>; true once the user chose Save, false if they cancelled.</summary>
        Task<bool> AskAsync(FirstSaveViewModel viewModel);
    }

    /// <summary>Shows a <see cref="FirstSaveWindow"/> over the owner window.</summary>
    public sealed class AvaloniaFirstSaveDialogService(Func<Window?> owner) : IFirstSaveDialogService
    {
        public async Task<bool> AskAsync(FirstSaveViewModel viewModel)
        {
            if (owner() is not { } window)
                return false;

            var dialog = new FirstSaveWindow { DataContext = viewModel };
            WindowChrome.UseOwnerIcon(dialog, window);
            return await dialog.ShowDialog<bool>(window);
        }
    }
}
