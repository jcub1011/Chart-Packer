using System.Buffers;
using Microsoft.Win32.SafeHandles;

namespace ChartPacker.Core.Sng;

/// <summary>Streaming access to the data of files stored in a <c>.sng</c>.</summary>
public static class SngFileData
{
    private const int ChunkSize = 1024 * 1024;

    /// <summary>
    /// Copies <paramref name="entry"/>'s bytes from <paramref name="sng"/> to
    /// <paramref name="destination"/>, unmasking them on the way (sng-spec §5).
    /// </summary>
    /// <remarks>The entry's range must already have been checked against the file, as <see cref="SngReader"/> does.</remarks>
    /// <exception cref="IOException">The file can't be read, or ended early.</exception>
    /// <exception cref="OperationCanceledException">The copy was cancelled.</exception>
    public static void CopyUnmasked(
        SafeFileHandle sng,
        SngFileEntry entry,
        ReadOnlySpan<byte> mask,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sng);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(destination);

        byte[] chunk = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            ulong done = 0;
            while (done < entry.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int wanted = (int)Math.Min(ChunkSize, entry.Length - done);
                Span<byte> data = chunk.AsSpan(0, wanted);
                ReadExact(sng, data, checked((long)(entry.Offset + done)));

                SngMask.Apply(data, mask, (long)done);
                destination.Write(data);
                done += (ulong)wanted;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    private static void ReadExact(SafeFileHandle handle, Span<byte> buffer, long offset)
    {
        while (!buffer.IsEmpty)
        {
            int read = RandomAccess.Read(handle, buffer, offset);
            if (read == 0)
                throw new EndOfStreamException("The file got shorter while it was being read.");
            buffer = buffer[read..];
            offset += read;
        }
    }
}
