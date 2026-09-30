using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Sng;

namespace ChartPacker.Core.Dedup;

/// <summary>Takes <see cref="EntryStamp"/>s, so a song can be checked for changes since it was compared.</summary>
public static class EntryStamps
{
    /// <summary>
    /// Stamps the song at <paramref name="path"/>, or returns <see langword="null"/> when it no
    /// longer exists. A folder is listed in full, so a changed file anywhere in it changes the stamp.
    /// </summary>
    /// <exception cref="IOException">The song can't be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    public static EntryStamp? Capture(string path, SongEntryKind kind, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (kind == SongEntryKind.SngFile)
        {
            var file = new FileInfo(path);
            return file.Exists ? ForFile(file.Length, file.LastWriteTimeUtc) : null;
        }

        if (!Directory.Exists(path))
            return null;

        try
        {
            return ForListing(SongFolderListing.ListStamped(path, cancellationToken));
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The stamp of a <c>.sng</c> of <paramref name="length"/> bytes last written at <paramref name="lastWriteTimeUtc"/>.</summary>
    public static EntryStamp ForFile(long length, DateTime lastWriteTimeUtc) => new(length, lastWriteTimeUtc, 1, "");

    /// <summary>
    /// The stamp of a folder whose files are <paramref name="listing"/>. OS and tool junk such as
    /// <c>Thumbs.db</c> is left out: it's never compared, and the OS rewrites it whenever the
    /// folder is browsed.
    /// </summary>
    public static EntryStamp ForListing(IReadOnlyList<FolderFileStamp> listing)
    {
        ArgumentNullException.ThrowIfNull(listing);

        long total = 0;
        int count = 0;
        DateTime newest = DateTime.MinValue;
        var lines = new StringBuilder();
        foreach (var file in listing)
        {
            if (file.RelativePath.Split('/').Any(FilenameRules.IsJunk))
                continue;

            count++;
            total += file.Length;
            if (file.LastWriteTimeUtc > newest)
                newest = file.LastWriteTimeUtc;
            lines.Append(file.RelativePath).Append('|')
                .Append(file.Length.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lines.ToString())));
        return new EntryStamp(total, newest, count, digest);
    }
}
