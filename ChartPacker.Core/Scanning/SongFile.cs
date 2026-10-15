using System.Security.Cryptography;
using ChartPacker.Core.Sng;
using Microsoft.Win32.SafeHandles;

namespace ChartPacker.Core.Scanning;

/// <summary>A file directly in a song, as stored: its name and size.</summary>
public sealed record StoredFile(string Name, long Length);

/// <summary>Reads single files of a song, from its folder or, unmasked, out of its <c>.sng</c>.</summary>
public static class SongFile
{
    /// <summary>
    /// The files directly in <paramref name="entry"/>: a folder's own files, or the <c>.sng</c>'s
    /// stored files whose names have no folder. A <c>.sng</c> that can't be parsed has none.
    /// </summary>
    /// <exception cref="IOException">The folder or file can't be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    public static IReadOnlyList<StoredFile> ListTopLevel(SongEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.Kind != SongEntryKind.SngFile)
        {
            return [.. new DirectoryInfo(entry.FullPath).EnumerateFiles().Select(f => new StoredFile(f.Name, f.Length))];
        }

        var read = SngReader.Read(entry.FullPath, cancellationToken);
        return read.Header is null
            ? []
            : read.Files.Where(f => !f.Name.Contains('/')).Select(f => new StoredFile(f.Name, (long)f.Length)).ToList();
    }

    /// <summary>
    /// Reads the file <paramref name="storedName"/> of <paramref name="entry"/>, matched ignoring case.
    /// </summary>
    /// <returns>Its bytes, or <see langword="null"/> when there's no such file or it's larger than <paramref name="maxBytes"/>.</returns>
    /// <exception cref="IOException">The file can't be read, or a stored file runs past the end of the <c>.sng</c>.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
    public static async Task<byte[]?> ReadAsync(SongEntry entry, string storedName, long maxBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrEmpty(storedName);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);

        if (entry.Kind != SongEntryKind.SngFile)
        {
            string? path = FindInFolder(entry.FullPath, storedName);
            return path is null ? null : await ReadFileAsync(path, maxBytes, cancellationToken).ConfigureAwait(false);
        }

        return await Task.Run(() =>
        {
            using SafeFileHandle handle = File.OpenHandle(entry.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var read = SngReader.Read(handle, cancellationToken);
            if (read.Header is not { } header)
                return null;

            var file = read.Files.FirstOrDefault(f => f.Name.Equals(storedName, StringComparison.OrdinalIgnoreCase));
            return file is null ? null : ReadFromSng(handle, header, file, maxBytes, cancellationToken);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the file <paramref name="storedName"/> of <paramref name="entry"/>, matched ignoring
    /// case, for reading and seeking: a folder's file as it is, or a <c>.sng</c>'s unmasked as
    /// it's read. A <c>/</c>-separated name opens a file in a subfolder. Disposing the stream closes the file.
    /// </summary>
    /// <returns>The stream, or <see langword="null"/> when there's no such file.</returns>
    /// <exception cref="IOException">The file can't be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    public static Stream? OpenRead(SongEntry entry, string storedName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrEmpty(storedName);

        if (entry.Kind != SongEntryKind.SngFile)
        {
            string? path = FindInFolder(entry.FullPath, storedName);
            return path is null ? null : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024);
        }

        // The handle is opened first, so the index read is of the file it reads.
        SafeFileHandle handle = File.OpenHandle(entry.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var read = SngReader.Read(handle, cancellationToken);
            var file = read.Header is null ? null : read.Files.FirstOrDefault(f => f.Name.Equals(storedName, StringComparison.OrdinalIgnoreCase));
            if (file is null)
            {
                handle.Dispose();
                return null;
            }

            return new HandleOwningStream(new BufferedStream(new SngEntryStream(handle, file, read.Header!.Mask.Span), 64 * 1024), handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>The SHA-256 of the file <paramref name="storedName"/> of <paramref name="entry"/>, as hex, read in pieces.</summary>
    /// <returns>The hash, or <see langword="null"/> when there's no such file.</returns>
    /// <exception cref="IOException">The file can't be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
    public static async Task<string?> HashAsync(SongEntry entry, string storedName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrEmpty(storedName);

        if (entry.Kind != SongEntryKind.SngFile)
        {
            string? path = FindInFolder(entry.FullPath, storedName);
            return path is null ? null : await HashFileAsync(path, cancellationToken).ConfigureAwait(false);
        }

        await using var stream = OpenRead(entry, storedName, cancellationToken);
        return stream is null ? null : Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The SHA-256 of the file at <paramref name="path"/>, as hex, read in pieces.</summary>
    /// <exception cref="IOException">The file can't be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
    public static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Reads the file at <paramref name="path"/>, or returns <see langword="null"/> when it's larger than <paramref name="maxBytes"/>.</summary>
    internal static async Task<byte[]?> ReadFileAsync(string path, long maxBytes, CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length > maxBytes)
            return null;

        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Unmasks the stored <paramref name="file"/> of the <c>.sng</c> at <paramref name="path"/>, or returns <see langword="null"/> when it's larger than <paramref name="maxBytes"/>.</summary>
    internal static byte[]? ReadFromSng(string path, SngHeader header, SngFileEntry file, long maxBytes, CancellationToken cancellationToken)
    {
        if (!Fits(file, maxBytes))
            return null;

        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadFromSng(handle, header, file, maxBytes, cancellationToken);
    }

    /// <summary>
    /// Unmasks the stored <paramref name="file"/> of the <c>.sng</c> open as <paramref name="sng"/>, or returns
    /// <see langword="null"/> when it's larger than <paramref name="maxBytes"/>. The handle stays open.
    /// </summary>
    /// <exception cref="IOException">The file can't be read, or the stored file runs past its end.</exception>
    /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
    internal static byte[]? ReadFromSng(SafeFileHandle sng, SngHeader header, SngFileEntry file, long maxBytes, CancellationToken cancellationToken) =>
        ReadFromSng(SngInput.Of(sng), header, file, maxBytes, cancellationToken);

    /// <summary>
    /// Unmasks the stored <paramref name="file"/> of the <c>.sng</c> in <paramref name="sng"/>, or returns
    /// <see langword="null"/> when it's larger than <paramref name="maxBytes"/>.
    /// </summary>
    /// <exception cref="IOException">The input can't be read, or the stored file runs past its end.</exception>
    /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
    internal static byte[]? ReadFromSng(SngInput sng, SngHeader header, SngFileEntry file, long maxBytes, CancellationToken cancellationToken)
    {
        if (!Fits(file, maxBytes))
            return null;

        cancellationToken.ThrowIfCancellationRequested();

        // Read straight into the result and unmasked there, rather than through a copy buffer.
        var data = new byte[(int)file.Length];
        sng.ReadExactly(data, checked((long)file.Offset));
        SngMask.Apply(data, header.Mask.Span);
        return data;
    }

    // Capped so the length always fits a byte array.
    private static bool Fits(SngFileEntry file, long maxBytes) => file.Length <= (ulong)Math.Min(maxBytes, Array.MaxLength);

    /// <summary>
    /// The path of the file in <paramref name="folder"/> named <paramref name="name"/>, ignoring case:
    /// directly in it, or, for a <c>/</c>-separated name as a <c>.sng</c> stores one, in its subfolders.
    /// A name that leads outside the folder finds nothing.
    /// </summary>
    private static string? FindInFolder(string folder, string name)
    {
        string[] segments = name.Split('/');
        if (segments.Any(s => s is "" or "." or ".." || s.Contains('\\')))
            return null;

        string? current = folder;
        foreach (string segment in segments[..^1])
        {
            current = Directory.EnumerateDirectories(current).FirstOrDefault(p => Path.GetFileName(p).Equals(segment, StringComparison.OrdinalIgnoreCase));
            if (current is null)
                return null;
        }

        return Directory.EnumerateFiles(current).FirstOrDefault(p => Path.GetFileName(p).Equals(segments[^1], StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A read-only stream that closes a file handle along with itself.</summary>
    private sealed class HandleOwningStream(Stream inner, SafeFileHandle handle) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                handle.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
