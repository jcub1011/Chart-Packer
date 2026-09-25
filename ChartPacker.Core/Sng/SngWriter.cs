using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using ChartPacker.Core.Validation;

namespace ChartPacker.Core.Sng;

/// <summary>A file to store in a <c>.sng</c>.</summary>
/// <param name="StoredName">The name to store, with <c>/</c> separators.</param>
/// <param name="SourcePath">Where the file's bytes are read from.</param>
/// <param name="Length">The file's size in bytes when it was planned. A different size at write time is an error.</param>
public sealed record SngSourceFile(string StoredName, string SourcePath, long Length);

/// <summary>Streams a <c>.sng</c> to disk.</summary>
/// <remarks>
/// <para>
/// Written from <c>docs/sng-spec.md</c> §3–§5. The section order and field widths match
/// <c>SngLib/SngSerializer.cs</c> <c>SaveSngFile</c> in
/// <see href="https://github.com/mdsitton/SngFileFormat">mdsitton/SngFileFormat</see> at commit
/// <c>6683799</c> (MIT License, © Matthew Sitton; see <c>THIRD-PARTY-NOTICES.md</c>).
/// </para>
/// <para>
/// Unlike the reference writer, this one stores files contiguously in index order (A5), writes to
/// a temp file and renames it into place so a target is never left half written (A6), and never
/// holds a whole file in memory. It does no validation of names or metadata: callers check those
/// first (see <c>PackPlanner</c>).
/// </para>
/// </remarks>
public static class SngWriter
{
    /// <summary>Suffix of the temp file written next to the target.</summary>
    public const string TempSuffix = ".tmp";

    private const uint Version = 1;
    private const int ChunkSize = 1024 * 1024;

    private static ReadOnlySpan<byte> Identifier => "SNGPKG"u8;

    /// <summary>Writes a <c>.sng</c> to <paramref name="targetPath"/> on the thread pool.</summary>
    /// <param name="targetPath">The file to create.</param>
    /// <param name="mask">The 16-byte mask to store and apply.</param>
    /// <param name="metadata">Metadata pairs, written in order.</param>
    /// <param name="files">Files, written in order.</param>
    /// <param name="overwrite">Whether an existing <paramref name="targetPath"/> is replaced.</param>
    /// <param name="verify">
    /// Whether to re-read the temp file with <see cref="SngReader"/> before renaming it into place.
    /// If that finds an error, the temp file is removed and the target is left untouched.
    /// </param>
    /// <param name="bytesWritten">Receives the running count of file-data bytes written.</param>
    /// <param name="cancellationToken">Cancels the write; the temp file is removed and any existing target is untouched.</param>
    /// <returns>The verify pass's findings; empty when <paramref name="verify"/> is false. Any error means nothing was written.</returns>
    /// <exception cref="IOException">A source can't be read, changed size, or the target exists and <paramref name="overwrite"/> is false.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to a source or the target is denied.</exception>
    /// <exception cref="OperationCanceledException">The write was cancelled.</exception>
    public static Task<IReadOnlyList<Finding>> WriteAsync(
        string targetPath,
        ReadOnlyMemory<byte> mask,
        IReadOnlyList<KeyValuePair<string, string>> metadata,
        IReadOnlyList<SngSourceFile> files,
        bool overwrite,
        bool verify = false,
        IProgress<long>? bytesWritten = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(files);
        if (mask.Length != SngMask.MaskSize)
            throw new ArgumentException($"The mask must be {SngMask.MaskSize} bytes.", nameof(mask));

        return Task.Run(() => Write(targetPath, mask, metadata, files, overwrite, verify, bytesWritten, cancellationToken), cancellationToken);
    }

    private static IReadOnlyList<Finding> Write(
        string targetPath,
        ReadOnlyMemory<byte> mask,
        IReadOnlyList<KeyValuePair<string, string>> metadata,
        IReadOnlyList<SngSourceFile> files,
        bool overwrite,
        bool verify,
        IProgress<long>? bytesWritten,
        CancellationToken cancellationToken)
    {
        if (!overwrite && File.Exists(targetPath))
            throw new IOException($"The file '{targetPath}' already exists.");

        string tempPath = targetPath + TempSuffix;
        try
        {
            using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024))
            {
                WriteHeaderAndIndex(output, mask.Span, metadata, files);
                WriteFileData(output, mask.Span, files, bytesWritten, cancellationToken);
                output.Flush(flushToDisk: true);
            }

            IReadOnlyList<Finding> findings = [];
            if (verify)
            {
                SngReadResult check = SngReader.Read(tempPath, cancellationToken);
                findings = check.Findings;
                if (check.HasErrors)
                {
                    TryDelete(tempPath);
                    return findings;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, targetPath, overwrite);
            return findings;
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void WriteHeaderAndIndex(
        Stream output,
        ReadOnlySpan<byte> mask,
        IReadOnlyList<KeyValuePair<string, string>> metadata,
        IReadOnlyList<SngSourceFile> files)
    {
        var buffer = new ArrayBufferWriter<byte>();

        buffer.Write(Identifier);
        WriteUInt32(buffer, Version);
        buffer.Write(mask);

        // Metadata: metadataLen counts the count field and every pair (sng-spec §4.2).
        var keys = new byte[metadata.Count][];
        var values = new byte[metadata.Count][];
        ulong metadataLen = sizeof(ulong);
        for (int i = 0; i < metadata.Count; i++)
        {
            keys[i] = Encoding.UTF8.GetBytes(metadata[i].Key);
            values[i] = Encoding.UTF8.GetBytes(metadata[i].Value);
            metadataLen += (ulong)(sizeof(int) + keys[i].Length + sizeof(int) + values[i].Length);
        }

        WriteUInt64(buffer, metadataLen);
        WriteUInt64(buffer, (ulong)metadata.Count);
        for (int i = 0; i < metadata.Count; i++)
        {
            WriteInt32(buffer, keys[i].Length);
            buffer.Write(keys[i]);
            WriteInt32(buffer, values[i].Length);
            buffer.Write(values[i]);
        }

        // FileIndex: fileMetaLen counts the count field and every entry (sng-spec §4.3).
        var names = new byte[files.Count][];
        ulong fileMetaLen = sizeof(ulong);
        for (int i = 0; i < files.Count; i++)
        {
            names[i] = Encoding.UTF8.GetBytes(files[i].StoredName);
            if (names[i].Length > byte.MaxValue)
                throw new ArgumentException($"The stored name '{files[i].StoredName}' is longer than 255 bytes.", nameof(files));
            fileMetaLen += (ulong)(1 + names[i].Length + sizeof(ulong) + sizeof(ulong));
        }

        // contentsIndex is absolute (A4); files are contiguous from the start of FileData (A5).
        ulong next = (ulong)SngReader.HeaderSize + sizeof(ulong) + metadataLen + sizeof(ulong) + fileMetaLen + sizeof(ulong);
        ulong fileDataLen = 0;

        WriteUInt64(buffer, fileMetaLen);
        WriteUInt64(buffer, (ulong)files.Count);
        for (int i = 0; i < files.Count; i++)
        {
            ulong length = (ulong)files[i].Length;
            buffer.GetSpan(1)[0] = (byte)names[i].Length;
            buffer.Advance(1);
            buffer.Write(names[i]);
            WriteUInt64(buffer, length);
            WriteUInt64(buffer, next);
            next += length;
            fileDataLen += length;
        }

        WriteUInt64(buffer, fileDataLen);
        output.Write(buffer.WrittenSpan);
    }

    private static void WriteFileData(
        Stream output,
        ReadOnlySpan<byte> mask,
        IReadOnlyList<SngSourceFile> files,
        IProgress<long>? bytesWritten,
        CancellationToken cancellationToken)
    {
        byte[] chunk = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            long total = 0;
            foreach (SngSourceFile file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var input = new FileStream(file.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1);
                if (input.Length != file.Length)
                    throw SizeChanged(file);

                long position = 0;
                while (position < file.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int wanted = (int)Math.Min(ChunkSize, file.Length - position);
                    int read = input.ReadAtLeast(chunk.AsSpan(0, wanted), wanted, throwOnEndOfStream: false);
                    if (read != wanted)
                        throw SizeChanged(file);

                    Span<byte> data = chunk.AsSpan(0, read);
                    SngMask.Apply(data, mask, position);
                    output.Write(data);

                    position += read;
                    total += read;
                    bytesWritten?.Report(total);
                }

                if (input.ReadByte() != -1)
                    throw SizeChanged(file);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    private static IOException SizeChanged(SngSourceFile file) =>
        new($"'{file.SourcePath}' changed size while it was being packed.");

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

    private static void WriteUInt32(ArrayBufferWriter<byte> buffer, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.GetSpan(sizeof(uint)), value);
        buffer.Advance(sizeof(uint));
    }

    private static void WriteInt32(ArrayBufferWriter<byte> buffer, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buffer.GetSpan(sizeof(int)), value);
        buffer.Advance(sizeof(int));
    }

    private static void WriteUInt64(ArrayBufferWriter<byte> buffer, ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.GetSpan(sizeof(ulong)), value);
        buffer.Advance(sizeof(ulong));
    }
}
