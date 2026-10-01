using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace ChartPacker.Services
{
    public sealed class AvaloniaFolderPickerService(Func<TopLevel?> topLevel) : IFolderPickerService
    {
        public async Task<string?> PickFolderAsync(string title, string? startFolder = null)
        {
            if (topLevel() is not { } owner)
                return null;

            var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                SuggestedStartLocation = await StorageStartFolder.TryGetAsync(owner.StorageProvider, startFolder),
            });

            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        }
    }
}
