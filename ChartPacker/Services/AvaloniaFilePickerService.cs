using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ChartPacker.Core.Scanning;
using ChartPacker.Resources;

namespace ChartPacker.Services
{
    public sealed class AvaloniaFilePickerService(Func<TopLevel?> topLevel) : IFilePickerService
    {
        private static readonly FilePickerFileType Audio = new(Strings.AudioFileTypeName)
        {
            Patterns = AudioStems.Extensions.Select(e => "*" + e).ToList(),
            MimeTypes = ["audio/ogg", "audio/opus", "audio/mpeg", "audio/wav"],
        };

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

        public async Task<string?> PickAudioAsync(string title)
        {
            if (topLevel() is not { } owner)
                return null;

            var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = [Audio],
            });

            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        }
    }
}
