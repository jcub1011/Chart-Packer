using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using ChartPacker.Core.Scanning;

namespace ChartPacker.Views
{
    /// <summary>What's being dragged onto the app.</summary>
    internal static class DroppedFiles
    {
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
