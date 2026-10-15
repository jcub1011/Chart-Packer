using ChartShared.Core.Metadata;
using ChartShared.Core.Sng;

namespace ChartShared.Core.Scanning;

/// <summary>Finds and reads a song's album art: <c>album.png</c>, <c>album.jpg</c> or <c>album.jpeg</c> (sng-spec §8).</summary>
public static class AlbumArt
{
    /// <summary>Images larger than this aren't read. Real album art is well under a few MB.</summary>
    public const long DefaultMaxBytes = 16 * 1024 * 1024;

    // In preference order, matched ignoring case.
    private static readonly string[] Names = ["album.png", "album.jpg", "album.jpeg"];

    /// <summary>Whether <paramref name="name"/> is an album-art name, ignoring case. Names with a folder never are.</summary>
    public static bool IsAlbumArtName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Names.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The name to store the image at <paramref name="sourcePath"/> under: <c>album.</c> plus its
    /// lowercased extension, or <see langword="null"/> when it isn't a png, jpg or jpeg.
    /// </summary>
    public static string? StoredNameFor(string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        string name = "album" + Path.GetExtension(sourcePath).ToLowerInvariant();
        return IsAlbumArtName(name) ? name : null;
    }

    /// <summary>
    /// Applies <paramref name="change"/> to the song folder <paramref name="folder"/>: every
    /// album-art file directly in it is deleted, and a replacement is copied in as
    /// <see cref="StoredNameFor"/> names it. Art in subfolders is left alone.
    /// </summary>
    /// <remarks>See <see cref="FileSlot.ApplyToFolderAsync"/>: a failed copy leaves the old art in place.</remarks>
    /// <exception cref="ArgumentException">The replacement isn't a png, jpg or jpeg.</exception>
    /// <exception cref="IOException">A file can't be read, written or deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">Cancelled before anything was deleted.</exception>
    public static Task ApplyToFolderAsync(string folder, AlbumArtChange change, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        string? name = null;
        if (change.SourcePath is { } source)
        {
            name = StoredNameFor(source)
                ?? throw new ArgumentException($"'{source}' isn't a png, jpg or jpeg image.", nameof(change));
        }

        return FileSlot.ApplyToFolderAsync(folder, IsAlbumArtName, name, change.SourcePath, cancellationToken);
    }

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
                ? ReadFromSng(entry.FullPath, maxBytes, cancellationToken)
                : ReadFromFolderAsync(entry.FullPath, maxBytes, cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Reads the album art of the <c>.sng</c> in <paramref name="sng"/>, which must be readable and seekable,
    /// and stays open. For a <c>.sng</c> that isn't a file of its own, such as the stream Explorer hands a
    /// thumbnail handler.
    /// </summary>
    /// <returns>
    /// The image file's bytes, unmasked, or <see langword="null"/> when the <c>.sng</c>'s header can't be read,
    /// it has no art, or the art is larger than <paramref name="maxBytes"/>.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="sng"/> can't be read or can't seek.</exception>
    /// <exception cref="IOException">The stream can't be read, or the stored image runs past its end.</exception>
    /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
    public static byte[]? ReadFromSng(Stream sng, long maxBytes = DefaultMaxBytes, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        return ReadFromSng(SngInput.Of(sng), maxBytes, cancellationToken);
    }

    private static Task<byte[]?> ReadFromFolderAsync(string folder, long maxBytes, CancellationToken cancellationToken)
    {
        var files = Directory.EnumerateFiles(folder).ToList();
        string? path = Pick(files, Path.GetFileName);
        return path is null ? Task.FromResult<byte[]?>(null) : SongFile.ReadFileAsync(path, maxBytes, cancellationToken);
    }

    private static Task<byte[]?> ReadFromSng(string path, long maxBytes, CancellationToken cancellationToken)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Task.FromResult(ReadFromSng(SngInput.Of(handle), maxBytes, cancellationToken));
    }

    private static byte[]? ReadFromSng(SngInput sng, long maxBytes, CancellationToken cancellationToken)
    {
        var read = SngReader.Read(sng, cancellationToken);
        if (read.Header is not { } header)
            return null;

        var file = Pick(read.Files, f => f.Name);
        return file is null ? null : SongFile.ReadFromSng(sng, header, file, maxBytes, cancellationToken);
    }

    /// <summary>The first of <paramref name="items"/> whose name is the most preferred album-art name.</summary>
    public static T? Pick<T>(IEnumerable<T> items, Func<T, string> name) where T : class
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(name);

        var list = items as IReadOnlyList<T> ?? [.. items];
        foreach (string candidate in Names)
        {
            foreach (T item in list)
            {
                if (name(item).Equals(candidate, StringComparison.OrdinalIgnoreCase))
                    return item;
            }
        }

        return null;
    }
}
