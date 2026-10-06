using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Sng;
using Microsoft.Win32.SafeHandles;

namespace ChartPacker.Core.Dedup;

/// <summary>A song changed on disk after it was read, so what was read about it no longer holds.</summary>
public sealed class SongChangedException(string message) : IOException(message);

/// <summary>
/// Hashes the files of fingerprinted songs, unmasked for a <c>.sng</c>, and remembers each hash so
/// no file is read twice. A hash that fails or is cancelled isn't remembered.
/// </summary>
/// <remarks>
/// Every <c>.sng</c> has its own random mask (sng-spec §5), so the same song packed twice has
/// different bytes on disk. Only the unmasked contents can be compared.
/// </remarks>
/// <param name="bytesHashed">Told how many bytes each chunk read adds, if given.</param>
public sealed class ContentHasher(IProgress<long>? bytesHashed = null)
{
    private const int BufferSize = 1024 * 1024;

    private readonly ConcurrentDictionary<(string Path, string Name), Lazy<Task<string>>> _hashes = new();
    private readonly IProgress<long>? _bytes = bytesHashed;
    private long _bytesHashed;
    private int _filesHashed;

    /// <summary>Bytes read and hashed so far.</summary>
    public long BytesHashed => Interlocked.Read(ref _bytesHashed);

    /// <summary>Files read and hashed so far.</summary>
    public int FilesHashed => Volatile.Read(ref _filesHashed);

    /// <summary>The SHA-256, as hex, of <paramref name="storedName"/> in <paramref name="song"/>, matched ignoring case.</summary>
    /// <exception cref="ArgumentException">The song has no such file.</exception>
    /// <exception cref="SongChangedException">The song changed on disk since it was fingerprinted.</exception>
    /// <exception cref="IOException">The file can't be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">Hashing was cancelled.</exception>
    public Task<string> HashAsync(SongFingerprint song, string storedName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentException.ThrowIfNullOrEmpty(storedName);

        if (!song.Sources.TryGetValue(storedName, out var source))
            throw new ArgumentException($"'{song.Entry.FullPath}' has no file '{storedName}'.", nameof(storedName));

        return HashCachedAsync((song.Entry.FullPath, storedName.ToLowerInvariant()), song, source, cancellationToken);
    }

    /// <summary>Waits for the cached hash, hashing again when the one cached failed or was cancelled.</summary>
    private async Task<string> HashCachedAsync((string Path, string Name) key, SongFingerprint song, StoredFileSource source, CancellationToken cancellationToken)
    {
        while (true)
        {
            var hash = _hashes.GetOrAdd(key, _ => new Lazy<Task<string>>(() => Task.Run(() => Hash(song, source, cancellationToken), cancellationToken)));
            try
            {
                return await hash.Value.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Only hashes are remembered, never failures, so a later caller can try again.
                _hashes.TryRemove(KeyValuePair.Create(key, hash));

                // Another caller's hash was cancelled, not this one's.
                if (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                    continue;
                throw;
            }
        }
    }

    private string Hash(SongFingerprint song, StoredFileSource source, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            if (song.Entry.Kind == SongEntryKind.SngFile)
            {
                using SafeFileHandle handle = File.OpenHandle(song.Entry.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (RandomAccess.GetLength(handle) != song.Stamp.TotalBytes || File.GetLastWriteTimeUtc(song.Entry.FullPath) != song.Stamp.LastWriteTimeUtc)
                    throw Changed(song.Entry.FullPath);

                using var stream = new SngEntryStream(handle, source.SngEntry!, song.Mask.Span);
                Copy(stream, hash, buffer, cancellationToken);
            }
            else
            {
                string path = source.SourcePath!;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.SequentialScan);
                if (stream.Length != source.Length || File.GetLastWriteTimeUtc(path) != source.LastWriteTimeUtc)
                    throw Changed(path);

                Copy(stream, hash, buffer, cancellationToken);
            }
        }
        catch (FileNotFoundException)
        {
            throw Changed(song.Entry.FullPath);
        }
        catch (DirectoryNotFoundException)
        {
            throw Changed(song.Entry.FullPath);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        Interlocked.Increment(ref _filesHashed);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private void Copy(Stream stream, IncrementalHash hash, byte[] buffer, CancellationToken cancellationToken)
    {
        int read;
        while ((read = stream.Read(buffer, 0, BufferSize)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
            Interlocked.Add(ref _bytesHashed, read);
            _bytes?.Report(read);
        }
    }

    private static SongChangedException Changed(string path) => new($"'{path}' changed on disk while it was being compared.");
}
