using Serilog;

namespace ChartShared.Video;

/// <summary>
/// Shows a video in step with a clock: <see cref="RequestTime"/> says where in the video to be, and
/// a thread of its own decodes to there and publishes the frame due, which the UI copies out with
/// <see cref="TryCopyFrame"/>. The video is opened on that thread, so nothing here blocks.
/// </summary>
/// <remarks>
/// Moving forward decodes on from the last frame. Moving back, or far ahead (the first time asked
/// for included), seeks to the keyframe before the time and decodes forward from it; the frames
/// passed on the way aren't converted. The clock running on while that happens is followed rather
/// than sought again: a seek for a little later would land on the same keyframe and start over, so
/// with keyframes far apart it would never catch up.
/// </remarks>
public sealed class VideoPlayback : IDisposable
{
    // Further ahead than this, seeking beats decoding every frame in between.
    private static readonly TimeSpan SeekAhead = TimeSpan.FromSeconds(1.5);

    // A frame due within this of the time asked for counts as due: about half a frame at 60 fps.
    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(8);

    // Catching up for longer than this, in milliseconds, shows how far it has got meanwhile.
    private const long PublishInterval = 100;

    private static ILogger Logger => Log.ForContext<VideoPlayback>();

    private readonly Func<Stream> _open;
    private readonly int _maxWidth;
    private readonly string _name;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _frameLock = new();

    // The time asked for, in ticks; long.MinValue until the first request.
    private long _target = long.MinValue;
    private volatile bool _stopping;

    // The last published frame, and how many have been published.
    private byte[]? _front;
    private int _version;

    /// <param name="open">Opens the video; called once, on the playback thread. The stream is disposed with the playback.</param>
    /// <param name="maxWidth">Frames wider than this are scaled down to it.</param>
    /// <param name="name">What the video is called in the log, e.g. its file name.</param>
    public VideoPlayback(Func<Stream> open, int maxWidth, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWidth, 1);
        _open = open;
        _maxWidth = maxWidth;
        _name = name ?? "a video";
        _thread = new Thread(Run) { IsBackground = true, Name = "Video playback" };
        _thread.Start();
    }

    /// <summary>Raised on the playback thread when the video is opened, fails, or publishes a frame.</summary>
    public event EventHandler? Changed;

    /// <summary>The frames' width, once the video is open; zero until then.</summary>
    public int Width { get; private set; }

    /// <summary>The frames' height, once the video is open; zero until then.</summary>
    public int Height { get; private set; }

    /// <summary>The video's length once it's open, or zero when it isn't known.</summary>
    public TimeSpan Duration { get; private set; }

    /// <summary>Why the video can't be shown, if it can't.</summary>
    public string? Error { get; private set; }

    /// <summary>Asks for the frame due at <paramref name="time"/>, from the start of the video.</summary>
    public void RequestTime(TimeSpan time)
    {
        if (Interlocked.Exchange(ref _target, time.Ticks) != time.Ticks && !_stopping)
            Wake();
    }

    /// <summary>
    /// Copies the latest frame into <paramref name="bgra"/> (<see cref="Width"/> × <see cref="Height"/>
    /// BGRA, rows packed) if it's newer than <paramref name="version"/>, which is then updated.
    /// </summary>
    /// <returns>Whether a newer frame was copied.</returns>
    public bool TryCopyFrame(Span<byte> bgra, ref int version)
    {
        lock (_frameLock)
        {
            if (_front is null || _version == version || bgra.Length < _front.Length)
                return false;

            _front.CopyTo(bgra);
            version = _version;
            return true;
        }
    }

    /// <summary>Whether there's a frame newer than <paramref name="version"/>, for <see cref="TryCopyFrame(nint, int, int, int, ref int)"/>.</summary>
    public bool HasFrameNewerThan(int version)
    {
        lock (_frameLock)
            return _front is not null && _version != version;
    }

    /// <summary>
    /// Copies the latest frame, row by row, into the <paramref name="width"/> × <paramref name="height"/> BGRA pixels at
    /// <paramref name="destination"/>, <paramref name="rowBytes"/> apart, such as a locked bitmap's, if it's newer than
    /// <paramref name="version"/>, which is then updated; as <see cref="TryCopyFrame(Span{byte}, ref int)"/>, without a buffer between.
    /// </summary>
    /// <returns>Whether a newer frame was copied: not when it isn't that size, as when the video changed meanwhile.</returns>
    public unsafe bool TryCopyFrame(nint destination, int rowBytes, int width, int height, ref int version)
    {
        int frameRowBytes = width * 4;
        if (destination == 0 || rowBytes < frameRowBytes)
            return false;

        lock (_frameLock)
        {
            if (_front is null || _version == version || _front.Length != frameRowBytes * height)
                return false;

            for (int y = 0; y < height; y++)
                _front.AsSpan(y * frameRowBytes, frameRowBytes).CopyTo(new Span<byte>((byte*)destination + ((long)y * rowBytes), frameRowBytes));
            version = _version;
            return true;
        }
    }

    /// <summary>Completes once the playback thread has stopped and closed the video, after <see cref="Dispose"/>.</summary>
    public Task WhenClosedAsync() => _closed.Task;

    /// <summary>Stops the playback thread, which then closes the video; see <see cref="WhenClosedAsync"/>.</summary>
    public void Dispose()
    {
        _stopping = true;
        Wake();
    }

    private void Wake()
    {
        try
        {
            _wake.Set();
        }
        catch (ObjectDisposedException)
        {
            // The thread has already stopped.
        }
    }

    private void Run()
    {
        VideoDecoder? decoder = null;
        try
        {
            try
            {
                decoder = VideoDecoder.Open(_open(), _maxWidth);
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                Logger.Warning(ex, "{Video} couldn't be opened", _name);
                Fail(ex.Message);
                return;
            }

            Width = decoder.Width;
            Height = decoder.Height;
            Duration = decoder.Duration;
            Logger.Information("{Video} opened: {Width}×{Height}, {Duration}", _name, Width, Height, Duration);
            Changed?.Invoke(this, EventArgs.Empty);
            Play(decoder);
        }
        catch (InvalidDataException ex)
        {
            Logger.Warning(ex, "{Video} stopped decoding", _name);
            Fail(ex.Message);
        }
        catch (Exception ex)
        {
            // A bug, not a bad file; but on this thread it would end the app, so the video shows it failed instead.
            Logger.Error(ex, "{Video} playback failed unexpectedly", _name);
            Fail(ex.Message);
        }
        finally
        {
            decoder?.Dispose();
            _wake.Dispose();
            _closed.TrySetResult();
            Logger.Information("{Video} closed", _name);
        }
    }

    private void Fail(string error)
    {
        Error = error;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Play(VideoDecoder decoder)
    {
        byte[] back = new byte[decoder.Width * decoder.Height * 4];
        TimeSpan keptTime = TimeSpan.Zero;
        TimeSpan nextTime = TimeSpan.Zero;

        // The time last sought; the frames before it are on the way there, not a reason to seek again.
        TimeSpan soughtTime = TimeSpan.Zero;
        bool hasNext = false;
        bool atEnd = false;
        long publishedAt = Environment.TickCount64;

        while (!_stopping)
        {
            long ticks = Interlocked.Read(ref _target);
            if (ticks == long.MinValue)
            {
                _wake.WaitOne();
                continue;
            }

            var target = TimeSpan.FromTicks(ticks);
            var reached = hasNext ? nextTime : keptTime;
            bool behind = decoder.HasKept && target < keptTime - Tolerance;
            bool far = target > Later(reached, soughtTime) + SeekAhead;

            // A seek that found nothing to decode: try again from the new time.
            bool stuck = !decoder.HasKept && atEnd;
            if (behind || far || stuck)
            {
                decoder.Seek(target);
                soughtTime = target;
                hasNext = false;
                atEnd = false;
                publishedAt = Environment.TickCount64;
            }

            // Decode on to the last frame due, keeping each one due; only that last one is converted.
            bool changed = false;
            bool catchingUp = false;
            while (!_stopping)
            {
                if (!hasNext)
                {
                    if (atEnd || !decoder.Next(out nextTime))
                    {
                        atEnd = true;
                        break;
                    }

                    hasNext = true;
                }

                // Before the time asked for, or the first frame after a seek: it's shown.
                if (nextTime > target + Tolerance && decoder.HasKept)
                    break;

                decoder.Keep();
                keptTime = nextTime;
                hasNext = false;
                changed = true;

                // A long way still to go: show this frame, then carry on.
                if (Environment.TickCount64 - publishedAt >= PublishInterval)
                {
                    catchingUp = true;
                    break;
                }

                // A new time asked for meanwhile: follow it, unless it's back or far enough ahead to seek.
                long latest = Interlocked.Read(ref _target);
                if (latest != ticks)
                {
                    var moved = TimeSpan.FromTicks(latest);
                    if (moved < keptTime - Tolerance || moved > Later(keptTime, soughtTime) + SeekAhead)
                        break;

                    ticks = latest;
                    target = moved;
                }
            }

            if (changed && decoder.CopyKept(back))
            {
                lock (_frameLock)
                {
                    (_front, back) = (back, _front ?? new byte[back.Length]);
                    _version++;
                }

                publishedAt = Environment.TickCount64;
                Changed?.Invoke(this, EventArgs.Empty);
            }

            if (!catchingUp && Interlocked.Read(ref _target) == ticks)
                _wake.WaitOne();
        }
    }

    private static TimeSpan Later(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
