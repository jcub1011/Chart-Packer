using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace ChartPacker.Services
{
    public sealed class AvaloniaFolderPickerService(Func<TopLevel?> topLevel) : IFolderPickerService
    {
        public async Task<string?> PickFolderAsync(string title)
        {
            if (topLevel() is not { } owner)
                return null;

            var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
            });

            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        }
    }
}
