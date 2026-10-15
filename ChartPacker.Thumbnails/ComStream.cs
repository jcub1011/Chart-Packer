namespace ChartPacker.Thumbnails;

/// <summary>
/// A read-only, seekable <see cref="Stream"/> over a COM <c>IStream</c>, so <see cref="Core.Sng.SngReader"/> can
/// read the <c>.sng</c> Explorer hands the thumbnail handler. It doesn't own the <c>IStream</c>.
/// </summary>
internal sealed unsafe class ComStream : Stream
{
    // ISequentialStream: Read, Write; IStream: Seek, ... (after IUnknown's three).
    private const int ReadSlot = 3;
    private const int SeekSlot = 5;

    private readonly IntPtr _stream;
    private long _position;

    public ComStream(IntPtr stream)
    {
        _stream = stream;
        Length = SeekTo(0, SeekOrigin.End);
        _position = SeekTo(0, SeekOrigin.Begin);
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length { get; }

    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            // Each seek is a call into Explorer's process, so one to where the stream already is is skipped.
            if (value != _position)
                _position = SeekTo(value, SeekOrigin.Begin);
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
            return 0;

        uint read = 0;
        int hr;
        fixed (byte* p = buffer)
            hr = ((delegate* unmanaged<IntPtr, byte*, uint, uint*, int>)Com.Slot(_stream, ReadSlot))(_stream, p, (uint)buffer.Length, &read);
        if (hr < 0)
            throw new IOException($"The stream couldn't be read (0x{hr:X8}).", hr);

        _position += read;
        return (int)read;
    }

    public override long Seek(long offset, SeekOrigin origin) => _position = SeekTo(offset, origin);

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    // STREAM_SEEK_SET, _CUR and _END have SeekOrigin's values.
    private long SeekTo(long offset, SeekOrigin origin)
    {
        ulong position;
        int hr = ((delegate* unmanaged<IntPtr, long, uint, ulong*, int>)Com.Slot(_stream, SeekSlot))(_stream, offset, (uint)origin, &position);
        if (hr < 0)
            throw new IOException($"The stream couldn't seek (0x{hr:X8}).", hr);
        return (long)position;
    }
}
