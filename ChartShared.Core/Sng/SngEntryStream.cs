using Microsoft.Win32.SafeHandles;

namespace ChartShared.Core.Sng;

/// <summary>
/// A read-only, seekable view of one file stored in a <c>.sng</c>, unmasked as it's read
/// (sng-spec §5), so a parser can seek around it without extracting the whole file.
/// </summary>
/// <remarks>
/// The entry's range must already have been checked against the file, as <see cref="SngReader"/>
/// does. The stream doesn't own <see cref="SafeFileHandle"/>; the caller disposes it.
/// </remarks>
public sealed class SngEntryStream : Stream
{
    private readonly SafeFileHandle _handle;
    private readonly SngFileEntry _entry;
    private readonly byte[] _mask;
    private long _position;

    public SngEntryStream(SafeFileHandle handle, SngFileEntry entry, ReadOnlySpan<byte> mask)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(entry);
        if (mask.Length != SngMask.MaskSize)
            throw new ArgumentException($"The mask must be {SngMask.MaskSize} bytes.", nameof(mask));
        if (entry.Length > long.MaxValue || entry.Offset > long.MaxValue - entry.Length)
            throw new ArgumentOutOfRangeException(nameof(entry), "The entry's range doesn't fit in a file.");

        _handle = handle;
        _entry = entry;
        _mask = mask.ToArray();
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => (long)_entry.Length;

    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        long remaining = Length - _position;
        if (remaining <= 0 || buffer.IsEmpty)
            return 0;

        if (buffer.Length > remaining)
            buffer = buffer[..(int)remaining];

        int read = RandomAccess.Read(_handle, buffer, (long)_entry.Offset + _position);
        if (read == 0)
            throw new EndOfStreamException("The file got shorter while it was being read.");

        SngMask.Apply(buffer[..read], _mask, _position);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0)
            throw new IOException("An attempt was made to move the position before the beginning of the stream.");

        _position = target;
        return target;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
