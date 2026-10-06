using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ChartPacker.Resources;

namespace ChartPacker.Services
{
    public sealed class AvaloniaImagePickerService(Func<TopLevel?> topLevel) : IImagePickerService
    {
        private static readonly FilePickerFileType Images = new(SharedStrings.ArtFileTypeName)
        {
            Patterns = ["*.png", "*.jpg", "*.jpeg"],
            MimeTypes = ["image/png", "image/jpeg"],
        };

        public async Task<string?> PickImageAsync(string title)
        {
            if (topLevel() is not { } owner)
                return null;

            var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = [Images],
            });

            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        }
    }
}
