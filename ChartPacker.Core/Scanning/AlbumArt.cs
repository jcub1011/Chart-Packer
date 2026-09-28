using ChartPacker.Core.Metadata;
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
    /// <remarks>
    /// The replacement is copied to a temp file before anything is deleted, so a copy that fails
    /// leaves the old art in place, and the source may be one of the files being replaced.
    /// </remarks>
    /// <exception cref="ArgumentException">The replacement isn't a png, jpg or jpeg.</exception>
    /// <exception cref="IOException">A file can't be read, written or deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">Cancelled before anything was deleted.</exception>
    public static async Task ApplyToFolderAsync(string folder, AlbumArtChange change, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        string? target = null;
        string? tempPath = null;
        if (change.SourcePath is { } source)
        {
            string name = StoredNameFor(source)
                ?? throw new ArgumentException($"'{source}' isn't a png, jpg or jpeg image.", nameof(change));
            target = Path.Combine(folder, name);
            tempPath = target + ".tmp";

            try
            {
                await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, useAsync: true))
                await using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024, useAsync: true))
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string path in Directory.EnumerateFiles(folder).Where(p => IsAlbumArtName(Path.GetFileName(p))).ToList())
                File.Delete(path);

            if (tempPath is not null)
                File.Move(tempPath, target!, overwrite: true);
        }
        catch
        {
            if (tempPath is not null)
                TryDelete(tempPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
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
