namespace ScanBench;

/// <summary>Passes reads through to another stream, counting the calls, the bytes, and the reads that didn't follow on from the last.</summary>
internal sealed class CountingStream(Stream inner) : Stream
{
    private long _nextExpected;

    public long Calls { get; private set; }

    public long Bytes { get; private set; }

    /// <summary>Reads that started somewhere other than where the previous one ended.</summary>
    public long Jumps { get; private set; }

    /// <summary>The furthest byte read, as a share of the stream's length.</summary>
    public double Reach => inner.Length == 0 ? 0 : (double)_furthest / inner.Length;

    private long _furthest;

    public override bool CanRead => true;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        long start = inner.Position;
        if (Calls > 0 && start != _nextExpected)
            Jumps++;

        int read = inner.Read(buffer);
        Calls++;
        Bytes += read;
        _nextExpected = start + read;
        _furthest = Math.Max(_furthest, _nextExpected);
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }
}
