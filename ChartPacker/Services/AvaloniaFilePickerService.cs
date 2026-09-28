using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace ChartPacker.Services
{
    public sealed class AvaloniaFilePickerService(Func<TopLevel?> topLevel) : IFilePickerService
    {
        public async Task<IReadOnlyList<string>> PickFilesAsync(string title)
        {
            if (topLevel() is not { } owner)
                return [];

            var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = true,
            });

            return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        }
    }
}
