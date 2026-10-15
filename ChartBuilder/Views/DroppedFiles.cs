using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using ChartPacker.Core.Scanning;

namespace ChartBuilder.Views
{
    /// <summary>What's being dragged onto the app.</summary>
    internal static class DroppedFiles
    {
        /// <summary>
        /// The song being dragged: the first folder or <c>.sng</c>, or else the first song.ini or chart, which stands
        /// for its folder. <see langword="null"/> when there's none.
        /// </summary>
        public static string? SongOf(DragEventArgs e)
        {
            if (e.DataTransfer.TryGetFiles() is not { } items)
                return null;

            string? folderFile = null;
            foreach (var item in items)
            {
                if (item.TryGetLocalPath() is not { } path)
                    continue;
                if (item is IStorageFolder || Path.GetExtension(path).Equals(".sng", StringComparison.OrdinalIgnoreCase))
                    return path;

                string name = Path.GetFileName(path);
                if (folderFile is null && (ChartFiles.IsChartName(name) || name.Equals("song.ini", StringComparison.OrdinalIgnoreCase)))
                    folderFile = path;
            }

            return folderFile;
        }

        /// <summary>The local paths of the audio files (ogg, opus, mp3 or wav) being dragged; folders and other files are left out.</summary>
        public static IReadOnlyList<string> AudioOf(DragEventArgs e) =>
            e.DataTransfer.TryGetFiles() is { } items
                ? [.. items.OfType<IStorageFile>()
                    .Select(f => f.TryGetLocalPath())
                    .OfType<string>()
                    .Where(p => AudioStems.IsAudioExtension(Path.GetExtension(p)))]
                : [];
    }
}
