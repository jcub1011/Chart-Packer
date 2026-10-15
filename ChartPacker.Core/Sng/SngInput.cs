using Microsoft.Win32.SafeHandles;

namespace ChartPacker.Core.Sng;

/// <summary>
/// What a <c>.sng</c> is read from: an open file or a stream. Reads take an offset and may return fewer bytes than
/// asked, so the reader and the stored-file reads share one way in for both.
/// </summary>
internal abstract class SngInput
{
    public abstract long Length { get; }

    public abstract int Read(Span<byte> buffer, long offset);

    /// <summary>Fills <paramref name="buffer"/> from <paramref name="offset"/>.</summary>
    /// <exception cref="EndOfStreamException">The input ends first.</exception>
    public void ReadExactly(Span<byte> buffer, long offset)
    {
        while (!buffer.IsEmpty)
        {
            int read = Read(buffer, offset);
            if (read == 0)
                throw new EndOfStreamException("The file got shorter while it was being read.");
            buffer = buffer[read..];
            offset += read;
        }
    }

    /// <summary>The file open as <paramref name="handle"/>, which stays open.</summary>
    public static SngInput Of(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return new HandleInput(handle);
    }

    /// <summary><paramref name="stream"/>, which stays open with its position moved.</summary>
    /// <exception cref="ArgumentException"><paramref name="stream"/> can't be read or can't seek.</exception>
    public static SngInput Of(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
            throw new ArgumentException("The stream must be readable and seekable.", nameof(stream));
        return new StreamInput(stream);
    }

    private sealed class HandleInput(SafeFileHandle handle) : SngInput
    {
        public override long Length => RandomAccess.GetLength(handle);

        public override int Read(Span<byte> buffer, long offset) => RandomAccess.Read(handle, buffer, offset);
    }

    private sealed class StreamInput(Stream stream) : SngInput
    {
        public override long Length => stream.Length;

        public override int Read(Span<byte> buffer, long offset)
        {
            stream.Position = offset;
            return stream.Read(buffer);
        }
    }
}
