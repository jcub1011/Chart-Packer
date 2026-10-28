namespace ChartShared.Audio.Playback;

/// <summary>
/// A read-only, seekable stream over a file that can fail for a moment and be opened again, as one on a cloud or network
/// drive does: a read that fails is tried again, on a stream opened afresh and put back where it was, before it's given up on.
/// </summary>
/// <remarks>
/// <para>
/// Playback reads the audio as it plays, so a file on such a drive that drops out for a second would otherwise stop the song
/// with the drive's error. Here the read waits and tries again, up to <see cref="Backoff"/>'s attempts: the player's queue
/// may run dry for the while, and then carries on. A file that really is gone fails after the last, with that attempt's error.
/// </para>
/// <para>
/// The length is read when the stream is opened and kept, since asking a dead handle for it fails too. Like the stream
/// it wraps, it's read by one thread at a time; <see cref="Dispose(bool)"/> from another ends a wait early.
/// </para>
/// </remarks>
internal sealed class ReopeningStream : Stream
{
    /// <summary>How long each retry waits before it reopens the file; the number of retries is the number of waits.</summary>
    public static readonly IReadOnlyList<TimeSpan> Backoff =
        [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(700), TimeSpan.FromMilliseconds(1000)];

    private readonly Func<Stream> _open;
    private readonly IReadOnlyList<TimeSpan> _backoff;
    private readonly Action<TimeSpan>? _wait;
    private readonly ManualResetEventSlim _disposed = new();
    private readonly long _length;
    private Stream? _inner;
    private long _position;

    /// <param name="open">Opens the stream, each time with the same contents. It's called again after a read fails.</param>
    /// <param name="backoff">How long each retry waits; <see cref="Backoff"/> when <see langword="null"/>.</param>
    /// <param name="wait">How a retry waits, for tests; by default it waits until the time passes or this is disposed.</param>
    /// <exception cref="IOException">The first open fails: that's not retried, so a file that can't be opened is told so at once.</exception>
    public ReopeningStream(Func<Stream> open, IReadOnlyList<TimeSpan>? backoff = null, Action<TimeSpan>? wait = null)
    {
        ArgumentNullException.ThrowIfNull(open);
        _open = open;
        _backoff = backoff ?? Backoff;
        _wait = wait;
        _inner = open();
        _length = _inner.Length;
    }

    public override bool CanRead => !_disposed.IsSet;

    public override bool CanSeek => !_disposed.IsSet;

    public override bool CanWrite => false;

    public override long Length => _length;

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
        ObjectDisposedException.ThrowIf(_disposed.IsSet, this);
        if (buffer.IsEmpty)
            return 0;

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var inner = _inner ??= Reopen();
                if (inner.Position != _position)
                    inner.Position = _position;

                int read = inner.Read(buffer);
                _position += read;
                return read;
            }
            catch (IOException) when (attempt < _backoff.Count && !_disposed.IsSet)
            {
                // The stream is let go of, so the next try opens the file afresh: a handle the drive dropped doesn't recover.
                DropInner();
                Wait(_backoff[attempt]);
            }
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        ArgumentOutOfRangeException.ThrowIfNegative(target, nameof(offset));
        _position = target;
        return target;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _disposed.Set();
            DropInner();
        }

        base.Dispose(disposing);
    }

    private Stream Reopen()
    {
        ObjectDisposedException.ThrowIf(_disposed.IsSet, this);
        var stream = _open();
        if (stream.Length == _length)
            return stream;

        // Not the file that was open: reading on from the same place would play something else.
        stream.Dispose();
        throw new IOException("The file changed while it was being played.");
    }

    private void DropInner()
    {
        var inner = Interlocked.Exchange(ref _inner, null);
        try
        {
            inner?.Dispose();
        }
        catch (IOException)
        {
            // It's already failing; what matters is that the handle is let go of.
        }
    }

    private void Wait(TimeSpan time)
    {
        if (_wait is { } wait)
            wait(time);
        else
            _disposed.Wait(time);
    }
}
