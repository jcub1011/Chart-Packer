using System;
using System.Collections.Generic;
using System.Globalization;
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
            Patterns = [.. AudioStems.Extensions.Select(e => "*" + e)],
            MimeTypes = ["audio/ogg", "audio/opus", "audio/mpeg", "audio/wav"],
        };

        private static readonly string[] ImageMimeTypes = ["image/png", "image/jpeg"];
        private static readonly string[] VideoMimeTypes = ["video/mp4", "video/x-msvideo", "video/webm", "video/ogg", "video/mpeg"];

        // Both kinds first, so the default filter shows everything that can be a background.
        private static readonly FilePickerFileType[] Backgrounds =
        [
            FileType(Strings.BackgroundFileTypeFormat, [.. SongBackgrounds.ImageExtensions, .. SongBackgrounds.VideoExtensions], [.. ImageMimeTypes, .. VideoMimeTypes]),
            FileType(Strings.ImageFileTypeFormat, SongBackgrounds.ImageExtensions, ImageMimeTypes),
            FileType(Strings.VideoFileTypeFormat, SongBackgrounds.VideoExtensions, VideoMimeTypes),
        ];

        private static readonly FilePickerFileType[] Charts =
        [
            FileType(Strings.ChartFileTypeFormat, ChartFiles.Extensions, ["application/x-chart", "audio/midi"]),
        ];

        private static readonly FilePickerFileType[] LyricsSources =
        [
            FileType(Strings.LyricsSourceFileTypeFormat, [.. ChartFiles.Extensions, ".sng"], ["application/x-chart", "audio/midi"]),
        ];

        public async Task<IReadOnlyList<string>> PickFilesAsync(string title)
        {
            if (topLevel() is not { } owner)
                return [];

            var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = true,
            });

            return [.. files.Select(f => f.TryGetLocalPath()).OfType<string>()];
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

        public async Task<string?> PickBackgroundAsync(string title)
        {
            if (topLevel() is not { } owner)
                return null;

            var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = Backgrounds,
            });

            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        }

        public Task<string?> PickChartAsync(string title) => PickOneAsync(title, Charts);

        public Task<string?> PickLyricsSourceAsync(string title) => PickOneAsync(title, LyricsSources);

        private async Task<string?> PickOneAsync(string title, IReadOnlyList<FilePickerFileType> types)
        {
            if (topLevel() is not { } owner)
                return null;

            var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = types,
            });

            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        }

        private static FilePickerFileType FileType(string nameFormat, IReadOnlyList<string> extensions, IReadOnlyList<string> mimeTypes) =>
            new(string.Format(CultureInfo.CurrentCulture, nameFormat, string.Join(", ", extensions.Select(e => e.TrimStart('.')))))
            {
                Patterns = [.. extensions.Select(e => "*" + e)],
                MimeTypes = mimeTypes,
            };
    }
}
