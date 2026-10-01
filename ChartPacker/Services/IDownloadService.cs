using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Scanning;

namespace ChartPacker.Services
{
    /// <summary>A song to save as a folder: its entry, its unsaved changes, and the name its folder takes.</summary>
    /// <param name="Draft">Its unsaved changes, saved into the copy; <see langword="null"/> for none.</param>
    /// <param name="FolderName">The folder's name, e.g. <c>Artist - Title</c>.</param>
    public sealed record SongDownload(SongEntry Entry, MetadataDraft? Draft, string FolderName);

    /// <summary>
    /// Saves files out of songs to the download folder, or where the user picks when the settings
    /// say to ask, and says what it did in the main window's notice. Nothing is ever overwritten
    /// without asking: a name that's taken gets a number.
    /// </summary>
    public interface IDownloadService
    {
        /// <summary>Saves one file of a song.</summary>
        /// <param name="song">The song <paramref name="open"/> reads, so that it isn't read while it's being saved; <see langword="null"/> when it reads no song.</param>
        /// <param name="prefix">Says which song the file is from, e.g. <c>Artist - Title</c>; it goes before <paramref name="storedName"/>.</param>
        /// <param name="storedName">The file's name in the song.</param>
        /// <param name="open">Opens the file's contents. Run off the UI thread; an <see cref="IOException"/> is reported in the notice.</param>
        Task SaveFileAsync(SongEntry? song, string prefix, string storedName, Func<CancellationToken, Task<Stream>> open);

        /// <summary>Saves each of <paramref name="songs"/> as a song folder of its own, with its unsaved changes.</summary>
        Task SaveSongFoldersAsync(IReadOnlyList<SongDownload> songs);
    }
}
