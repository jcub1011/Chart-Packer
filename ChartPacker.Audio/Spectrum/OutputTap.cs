namespace ChartPacker.Audio.Spectrum;

/// <summary>
/// The last <see cref="Capacity"/> frames of what was mixed, channels averaged, by their frame on the mixer's
/// timeline, so that what's being heard can be looked at. Thread-safe: written by the playback thread, read by
/// whoever draws it.
/// </summary>
public sealed class OutputTap
{
    private readonly Lock _lock = new();
    private readonly float[] _ring;

    // Frames _start up to _end are held, the frame f at _ring[f % Capacity].
    private long _start;
    private long _end;

    /// <param name="capacity">How many frames are held: enough for what's queued ahead of the device and a window behind it.</param>
    public OutputTap(int capacity = 32768)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _ring = new float[capacity];
    }

    public int Capacity => _ring.Length;

    /// <summary>
    /// Keeps <paramref name="stereo"/> (interleaved left, right), whose first frame is <paramref name="firstFrame"/>.
    /// Frames that don't follow on from those held replace them.
    /// </summary>
    public void Write(ReadOnlySpan<float> stereo, long firstFrame)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(firstFrame);
        int frames = stereo.Length / 2;
        lock (_lock)
        {
            if (firstFrame != _end)
                _start = _end = firstFrame;

            for (int n = 0; n < frames; n++)
                _ring[(_end + n) % _ring.Length] = (stereo[2 * n] + stereo[(2 * n) + 1]) / 2;

            _end += frames;
            _start = Math.Max(_start, _end - _ring.Length);
        }
    }

    /// <summary>Forgets every frame held, as after a seek.</summary>
    public void Clear()
    {
        lock (_lock)
            _start = _end;
    }

    /// <summary>Copies the <paramref name="mono"/>.Length frames that end just before <paramref name="endFrame"/>.</summary>
    /// <returns><see langword="false"/>, leaving <paramref name="mono"/> as it was, unless every one of those frames is held.</returns>
    public bool TryRead(long endFrame, Span<float> mono)
    {
        lock (_lock)
        {
            long first = endFrame - mono.Length;
            if (mono.Length > _ring.Length || first < _start || endFrame > _end)
                return false;

            for (int n = 0; n < mono.Length; n++)
                mono[n] = _ring[(first + n) % _ring.Length];
            return true;
        }
    }
}
