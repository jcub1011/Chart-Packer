namespace ChartShared.Audio.Decoding;

/// <summary>A stem decoded once (<see cref="DecodedStem"/>), read as a mono <see cref="IPcmSource"/>.</summary>
/// <remarks>
/// It takes no file or decoder, so any number may be opened over the same stem, each read by its own thread. Its audio
/// can't be damaged, so reading and seeking never throw. Over a stem still being decoded, it reads what's been written,
/// frames not yet decoded silent, to <see cref="DecodedStem.LengthFrames"/> as it is at each read: read only what
/// <see cref="DecodedStem.WhenReadable"/> says is.
/// </remarks>
public sealed class CachedMonoSource(DecodedStem stem) : IPcmSource
{
    private long _position;

    public int SampleRate => stem.SampleRate;

    public int Channels => 1;

    public long LengthFrames => stem.LengthFrames;

    public int ReadFrames(Span<float> buffer)
    {
        int count = stem.Read(_position, buffer);
        _position += count;
        return count;
    }

    public void SeekToFrame(long frame) => _position = Math.Max(0, frame);

    public void Dispose()
    {
    }
}
