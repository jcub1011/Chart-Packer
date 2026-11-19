namespace ChartShared.Audio.Decoding;

/// <summary>Exposes one stretch of an <see cref="IPcmSource"/>, from <c>start</c> for up to <c>frames</c> frames, as a stream of its own.</summary>
/// <remarks>The source is seeked to the window's start when this is made, and is read, not owned: disposing the window leaves it open.</remarks>
internal sealed class PcmWindow : IPcmSource
{
    private readonly IPcmSource _source;
    private readonly long _start;
    private readonly long _frames;
    private long _position;

    /// <param name="source">The audio to take a window of.</param>
    /// <param name="start">The first frame of the window.</param>
    /// <param name="frames">The window's length; less when the source ends first.</param>
    /// <exception cref="InvalidDataException">The audio is damaged.</exception>
    public PcmWindow(IPcmSource source, long start, long frames)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(frames);

        _source = source;
        _start = start;
        _frames = source.LengthFrames < 0 ? frames : Math.Clamp(source.LengthFrames - start, 0, frames);
        source.SeekToFrame(start);
    }

    public int SampleRate => _source.SampleRate;

    public int Channels => _source.Channels;

    /// <summary>The window's length, or -1 when the source's length isn't known, since then it can't be said where the source ends.</summary>
    public long LengthFrames => _source.LengthFrames < 0 ? -1 : _frames;

    /// <summary>How many frames have been read from the window, or are skipped to by seeking: how much of it there really was, once it's read to its end.</summary>
    public long FramesRead => _position;

    public int ReadFrames(Span<float> buffer)
    {
        int channels = _source.Channels;
        if (buffer.Length < channels)
            throw new ArgumentException("The buffer has no room for a whole frame.", nameof(buffer));

        long left = _frames - _position;
        if (left <= 0)
            return 0;

        int frames = (int)Math.Min(buffer.Length / channels, left);
        int read = _source.ReadFrames(buffer[..(frames * channels)]);
        _position += read;
        return read;
    }

    public void SeekToFrame(long frame)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        _position = Math.Min(frame, _frames);
        _source.SeekToFrame(_start + _position);
    }

    public void Dispose()
    {
    }
}
