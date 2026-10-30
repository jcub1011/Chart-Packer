using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using ChartBuilder.ViewModels;
using ChartBuilder.Views;
using ChartShared.UI.Views;

namespace ChartBuilder.Services
{
    /// <summary>Asks what a new chart's first save needs: the song's name and artist when unset, and where its song folder goes (charter-spec C1, The first save).</summary>
    public interface IFirstSaveDialogService
    {
        /// <summary>Shows <paramref name="viewModel"/>; true once the user chose Save, false if they cancelled.</summary>
        Task<bool> AskAsync(FirstSaveViewModel viewModel);

        /// <summary>Shows <paramref name="viewModel"/>, which asks the song's name and artist; true once the user chose Save, false if they cancelled.</summary>
        Task<bool> AskSongInfoAsync(SongInfoViewModel viewModel);
    }

    /// <summary>Shows a <see cref="FirstSaveWindow"/> over the owner window.</summary>
    public sealed class AvaloniaFirstSaveDialogService(Func<Window?> owner) : IFirstSaveDialogService
    {
        public Task<bool> AskAsync(FirstSaveViewModel viewModel) => ShowAsync(() => new FirstSaveWindow { DataContext = viewModel });

        public Task<bool> AskSongInfoAsync(SongInfoViewModel viewModel) => ShowAsync(() => new SongInfoWindow { DataContext = viewModel });

        /// <summary>Shows the dialog over the owner window; false when there's no owner or the user cancelled.</summary>
        private async Task<bool> ShowAsync(Func<Window> create)
        {
            if (owner() is not { } window)
                return false;

            var dialog = create();
            WindowChrome.UseOwnerIcon(dialog, window);
            return await dialog.ShowDialog<bool>(window);
        }
    }
}
