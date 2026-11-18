using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ChartShared.Core.Lyrics;
using ChartShared.Core.Scanning;
using ChartShared.UI.Resources;

namespace ChartShared.UI.Services
{
    public sealed class AvaloniaFilePickerService(Func<TopLevel?> topLevel) : IFilePickerService
    {
        private static readonly FilePickerFileType Audio = new(SharedStrings.AudioFileTypeName)
        {
            Patterns = [.. AudioStems.Extensions.Select(e => "*" + e)],
            MimeTypes = ["audio/ogg", "audio/opus", "audio/mpeg", "audio/wav"],
        };

        private static readonly string[] ImageMimeTypes = ["image/png", "image/jpeg"];
        private static readonly string[] VideoMimeTypes = ["video/mp4", "video/x-msvideo", "video/webm", "video/ogg", "video/mpeg"];

        // Both kinds first, so the default filter shows everything that can be a background.
        private static readonly FilePickerFileType[] Backgrounds =
        [
            FileType(SharedStrings.BackgroundFileTypeFormat, [.. SongBackgrounds.ImageExtensions, .. SongBackgrounds.VideoExtensions], [.. ImageMimeTypes, .. VideoMimeTypes]),
            FileType(SharedStrings.ImageFileTypeFormat, SongBackgrounds.ImageExtensions, ImageMimeTypes),
            FileType(SharedStrings.VideoFileTypeFormat, SongBackgrounds.VideoExtensions, VideoMimeTypes),
        ];

        private static readonly FilePickerFileType[] Charts =
        [
            FileType(SharedStrings.ChartFileTypeFormat, ChartFiles.Extensions, ["application/x-chart", "audio/midi"]),
        ];

        private static readonly FilePickerFileType[] LyricsSources =
        [
            FileType(SharedStrings.LyricsSourceFileTypeFormat, [.. ChartFiles.Extensions, ".sng"], ["application/x-chart", "audio/midi"]),
        ];

        // Lyrics files first, so the default filter shows everything that can be imported; the audio files' tags are the second.
        private static readonly FilePickerFileType[] LyricsFiles =
        [
            FileType(SharedStrings.LyricsFileTypeFormat, LyricFiles.TextExtensions, ["text/plain", "application/xml", "audio/midi"]),
            FileType(SharedStrings.LyricsTagsFileTypeFormat, LyricFiles.AudioExtensions, ["audio/mpeg", "audio/ogg", "audio/opus", "audio/flac"]),
        ];

        private static readonly FilePickerFileType Lrc = FileType(SharedStrings.LrcFileTypeFormat, [".lrc"], ["text/plain"]);

        private static readonly FilePickerFileType UltraStar = FileType(SharedStrings.UltraStarFileTypeFormat, [".txt"], ["text/plain"]);

        // A folder can't be picked in the same dialog as a file, so a song folder is picked by a file in it.
        private static readonly FilePickerFileType[] Songs =
        [
            new(SharedStrings.SongFileTypeName)
            {
                Patterns = ["*.sng", "song.ini", ChartFiles.MidiName, ChartFiles.ChartName],
                MimeTypes = ["application/octet-stream", "audio/midi"],
            },
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

        public async Task<IReadOnlyList<string>> PickAudioFilesAsync(string title)
        {
            if (topLevel() is not { } owner)
                return [];

            var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = true,
                FileTypeFilter = [Audio],
            });

            return [.. files.Select(f => f.TryGetLocalPath()).OfType<string>()];
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

        public Task<string?> PickLyricsFileAsync(string title) => PickOneAsync(title, LyricsFiles);

        public async Task<string?> PickLyricsSaveAsync(string title, string suggestedName, LyricFormat format)
        {
            var (type, extension) = format switch
            {
                LyricFormat.UltraStar => (UltraStar, "txt"),
                LyricFormat.Lrc => (Lrc, "lrc"),
                _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Lyrics are written as UltraStar or LRC."),
            };
            if (topLevel() is not { } owner)
                return null;

            var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = suggestedName,
                DefaultExtension = extension,
                FileTypeChoices = [type],
                ShowOverwritePrompt = true,
            });

            return file?.TryGetLocalPath();
        }

        public Task<string?> PickSongAsync(string title) => PickOneAsync(title, Songs);

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
