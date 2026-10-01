using System.Globalization;
using System.IO;
using ChartPacker.Core.Scanning;
using ChartPacker.Resources;

namespace ChartPacker.ViewModels
{
    /// <summary>
    /// A file of a song as the app shows it, to save a copy of: a staged file a draft adds, or the
    /// song's own stored file.
    /// </summary>
    /// <param name="StoredName">The name the file has, or will have, in the song.</param>
    /// <param name="StagedPath">The staged file, when a draft adds it; otherwise the stored file is read.</param>
    public sealed record SongFileSource(SongEntry Entry, string StoredName, string? StagedPath = null)
    {
        /// <summary>The right-click menu's item that saves the file <paramref name="storedName"/>: <c>Download "guitar.ogg"</c>.</summary>
        /// <remarks>A file in a subfolder is named by its last segment, as it's saved.</remarks>
        public static string DownloadHeaderFor(string storedName) =>
            string.Format(CultureInfo.CurrentCulture, Strings.MenuDownloadFileFormat, storedName[(storedName.LastIndexOf('/') + 1)..]);

        /// <summary>Opens the file for reading.</summary>
        /// <exception cref="IOException">The file can't be read, or isn't there any more.</exception>
        /// <exception cref="System.UnauthorizedAccessException">Access is denied.</exception>
        public Stream Open() => StagedPath is { } path
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024)
            : SongFile.OpenRead(Entry, StoredName) ?? throw new FileNotFoundException($"'{StoredName}' isn't in the song any more.", StoredName);
    }
}
