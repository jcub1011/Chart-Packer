using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Sng;

namespace ChartBuilder.ViewModels
{
    /// <summary>The sizes of a song's files and of files staged for it, as Chart settings' Tracks and the Art tab show them.</summary>
    internal static class SongFileSizes
    {
        /// <summary>
        /// Each file's size: the song's by stored name, and each of <paramref name="stagedPaths"/> by path. One that can't
        /// be read has none. Reads the disk, so off the UI thread.
        /// </summary>
        public static IReadOnlyDictionary<string, long> Of(OpenedSong? opened, IEnumerable<string> stagedPaths, CancellationToken cancellationToken)
        {
            var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (opened?.Entry is { Kind: SongEntryKind.SngFile } sng)
                {
                    foreach (var file in SngReader.Read(sng.FullPath, cancellationToken).Files)
                        sizes[file.Name] = (long)file.Length;
                }
                else if (opened?.Entry is { } folder)
                {
                    foreach (string name in opened.StoredNames)
                    {
                        var info = new FileInfo(Path.Combine(folder.FullPath, name));
                        if (info.Exists)
                            sizes[name] = info.Length;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
            }

            foreach (string path in stagedPaths)
            {
                try
                {
                    if (new FileInfo(path) is { Exists: true } info)
                        sizes[path] = info.Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                }
            }

            return sizes;
        }
    }
}
