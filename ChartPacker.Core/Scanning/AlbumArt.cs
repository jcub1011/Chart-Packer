using ChartPacker.Core.Sng;
using Microsoft.Win32.SafeHandles;

namespace ChartPacker.Core.Scanning;

/// <summary>Finds and reads a song's album art: <c>album.png</c>, <c>album.jpg</c> or <c>album.jpeg</c> (sng-spec §8).</summary>
public static class AlbumArt
{
    /// <summary>Images larger than this aren't read. Real album art is well under a few MB.</summary>
    public const long DefaultMaxBytes = 16 * 1024 * 1024;

    // In preference order, matched ignoring case.
    private static readonly string[] Names = ["album.png", "album.jpg", "album.jpeg"];

    /// <summary>
    /// Reads <paramref name="entry"/>'s album art on the thread pool. A folder's art must be
    /// directly in it; a <c>.sng</c>'s art is unmasked from the container.
    /// </summary>
    /// <returns>The image file's bytes, or <see langword="null"/> when there is none or it's larger than <paramref name="maxBytes"/>.</returns>
    /// <exception cref="IOException">The file can't be read, or a stored image runs past its end.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
    public static Task<byte[]?> ReadAsync(SongEntry entry, long maxBytes = DefaultMaxBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);

        return Task.Run(
            () => entry.Kind == SongEntryKind.SngFile
                ? Task.FromResult(ReadFromSng(entry.FullPath, maxBytes, cancellationToken))
                : ReadFromFolderAsync(entry.FullPath, maxBytes, cancellationToken),
            cancellationToken);
    }

    private static async Task<byte[]?> ReadFromFolderAsync(string folder, long maxBytes, CancellationToken cancellationToken)
    {
        var files = Directory.EnumerateFiles(folder).ToList();
        string? path = Pick(files, Path.GetFileName);
        if (path is null || new FileInfo(path).Length > maxBytes)
            return null;

        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private static byte[]? ReadFromSng(string path, long maxBytes, CancellationToken cancellationToken)
    {
        var read = SngReader.Read(path, cancellationToken);
        if (read.Header is not { } header)
            return null;

        // Capped so the length always fits a byte array.
        var file = Pick(read.Files, f => f.Name);
        if (file is null || file.Length > (ulong)Math.Min(maxBytes, Array.MaxLength))
            return null;

        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var data = new MemoryStream((int)file.Length);
        SngFileData.CopyUnmasked(handle, file, header.Mask.Span, data, cancellationToken);
        return data.ToArray();
    }

    /// <summary>The first of <paramref name="items"/> whose name is the most preferred album-art name.</summary>
    private static T? Pick<T>(IReadOnlyList<T> items, Func<T, string> name) where T : class
    {
        foreach (string candidate in Names)
        {
            foreach (T item in items)
            {
                if (name(item).Equals(candidate, StringComparison.OrdinalIgnoreCase))
                    return item;
            }
        }

        return null;
    }
}
