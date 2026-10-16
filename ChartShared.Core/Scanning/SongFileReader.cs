using ChartShared.Core.Sng;
using Microsoft.Win32.SafeHandles;

namespace ChartShared.Core.Scanning;

/// <summary>
/// Reads several files of one song, opening it once: a folder is listed once, and a <c>.sng</c> is opened and its index
/// read once, rather than once for each file as <see cref="SongFile"/>'s reads are.
/// </summary>
/// <remarks>A <c>.sng</c> stays open until it's disposed, and its files are read from that same open file.</remarks>
public sealed class SongFileReader : IDisposable
{
    // How much of a file a stream it opens reads at once. The buffer is rented, so the many small reads of a song's
    // audio headers don't each make one.
    private const int BufferSize = 64 * 1024;

    private readonly SafeFileHandle? _handle;

    private SongFileReader(SongEntry entry, IReadOnlyList<StoredFile> topLevel, SafeFileHandle? handle, SngReadResult? sng)
    {
        Entry = entry;
        TopLevel = topLevel;
        _handle = handle;
        Sng = sng;
    }

    public SongEntry Entry { get; }

    /// <summary>The <c>.sng</c> as read, its metadata and index; null for a folder.</summary>
    public SngReadResult? Sng { get; }

    /// <summary>The files directly in the song, as <see cref="SongFile.ListTopLevel"/> lists them.</summary>
    public IReadOnlyList<StoredFile> TopLevel { get; }

    /// <summary>Lists the folder <paramref name="entry"/>, or opens the <c>.sng</c> and reads its index.</summary>
    /// <exception cref="IOException">The folder or file can't be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
    public static SongFileReader Open(SongEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.Kind != SongEntryKind.SngFile)
            return new SongFileReader(entry, SongFile.ListFolder(entry.FullPath), null, null);

        var handle = File.OpenHandle(entry.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var read = SngReader.Read(handle, cancellationToken);
            return new SongFileReader(entry, SongFile.TopLevelOf(read), handle, read);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Reads the file <paramref name="storedName"/>, matched ignoring case, as <see cref="SongFile.ReadAsync"/> does.</summary>
    /// <returns>Its bytes, or <see langword="null"/> when there's no such file or it's larger than <paramref name="maxBytes"/>.</returns>
    /// <exception cref="IOException">The file can't be read, or a stored file runs past the end of the <c>.sng</c>.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
    public async Task<byte[]?> ReadAsync(string storedName, long maxBytes, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storedName);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);

        if (Sng is null)
        {
            string? path = FindInFolder(storedName);
            return path is null ? null : await SongFile.ReadFileAsync(path, maxBytes, cancellationToken).ConfigureAwait(false);
        }

        return Sng.Header is { } header && FindInSng(storedName) is { } file
            ? SongFile.ReadFromSng(_handle!, header, file, maxBytes, cancellationToken)
            : null;
    }

    /// <summary>
    /// Opens the file <paramref name="storedName"/>, matched ignoring case, for reading and seeking, as
    /// <see cref="SongFile.OpenRead"/> does, through a rented buffer. A <c>.sng</c>'s is read from the open file, so the
    /// stream must be disposed before this is.
    /// </summary>
    /// <returns>The stream, or <see langword="null"/> when there's no such file.</returns>
    /// <exception cref="IOException">The file can't be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    public Stream? OpenRead(string storedName)
    {
        ArgumentException.ThrowIfNullOrEmpty(storedName);

        if (Sng is null)
        {
            string? path = FindInFolder(storedName);
            return path is null ? null : new PooledReadStream(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0), BufferSize);
        }

        return Sng.Header is { } header && FindInSng(storedName) is { } file
            ? new PooledReadStream(new SngEntryStream(_handle!, file, header.Mask.Span), BufferSize)
            : null;
    }

    public void Dispose() => _handle?.Dispose();

    // A file directly in the folder is found in the listing; one in a subfolder is looked for.
    private string? FindInFolder(string storedName)
    {
        if (storedName.Contains('/'))
            return SongFile.FindInFolder(Entry.FullPath, storedName);

        var file = TopLevel.FirstOrDefault(f => f.Name.Equals(storedName, StringComparison.OrdinalIgnoreCase));
        return file is null ? null : Path.Combine(Entry.FullPath, file.Name);
    }

    private SngFileEntry? FindInSng(string storedName) =>
        Sng!.Files.FirstOrDefault(f => f.Name.Equals(storedName, StringComparison.OrdinalIgnoreCase));
}
