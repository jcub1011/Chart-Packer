using System.Buffers;

namespace ChartShared.Core.Scanning;

/// <summary>
/// A read-only, seekable stream over another, read through a buffer rented from <see cref="ArrayPool{T}.Shared"/> and
/// given back once it's disposed, so reading many files a few bytes at a time, as ATL reads audio headers, doesn't make
/// a new buffer for each. Disposing it disposes the stream under it.
/// </summary>
/// <remarks>The stream under it must be seekable, and is read from wherever this one is, so nothing else may move it.</remarks>
internal sealed class PooledReadStream : Stream
{
    private readonly Stream _inner;
    private byte[]? _buffer;

    // Where in the stream under it the buffer starts, and how much of it holds what was read there.
    private long _bufferStart;
    private int _filled;
    private long _position;

    public PooledReadStream(Stream inner, int bufferSize)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);
        if (!inner.CanRead || !inner.CanSeek)
            throw new ArgumentException("The stream must be readable and seekable.", nameof(inner));
        _inner = inner;
        _position = inner.Position;
        _buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
    }

    public override bool CanRead => _buffer is not null;

    public override bool CanSeek => _buffer is not null;

    public override bool CanWrite => false;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_buffer is null, this);
        if (destination.IsEmpty)
            return 0;

        if (_position < _bufferStart || _position >= _bufferStart + _filled)
        {
            // A read as large as the buffer goes straight to the stream under it.
            _inner.Position = _position;
            if (destination.Length >= _buffer.Length)
            {
                int direct = _inner.Read(destination);
                _position += direct;
                return direct;
            }

            _bufferStart = _position;
            _filled = _inner.Read(_buffer);
            if (_filled == 0)
                return 0;
        }

        int at = (int)(_position - _bufferStart);
        int count = Math.Min(destination.Length, _filled - at);
        _buffer.AsSpan(at, count).CopyTo(destination);
        _position += count;
        return count;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        // As FileStream throws, which the readers of the streams this one stands in for may be ready for.
        if (position < 0)
            throw new IOException("An attempt was made to move the position before the beginning of the stream.");
        Position = position;
        return position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && _buffer is { } buffer)
        {
            _buffer = null;
            ArrayPool<byte>.Shared.Return(buffer);
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
