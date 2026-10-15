using NVorbis;

namespace ChartShared.Audio.Decoding;

/// <summary>Decodes Ogg Vorbis with <see href="https://github.com/NVorbis/NVorbis">NVorbis</see>.</summary>
/// <remarks>
/// NVorbis lands a seek on the right sample, but what it decodes for the first packets after one
/// isn't what a straight decode gives. So a seek goes to <see cref="PreRollFrames"/> before the
/// target and decodes up to it, which makes the result sample-exact.
/// </remarks>
public sealed class VorbisSource : IPcmSource
{
    // Two of Vorbis's longest blocks.
    private const int PreRollFrames = 8192;

    private readonly VorbisReader _reader;
    private float[] _discard = [];

    /// <exception cref="InvalidDataException">The stream isn't Ogg Vorbis.</exception>
    public VorbisSource(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            _reader = new VorbisReader(stream, closeOnDispose: true)
            {
                // The mixer clips the sum; clipping each stem first would distort it.
                ClipSamples = false,
            };
        }
        catch (Exception ex) when (AudioDecoder.IsDecoderFault(ex))
        {
            throw AudioDecoder.Damaged(ex);
        }

        if (_reader.Channels <= 0 || _reader.SampleRate <= 0)
        {
            _reader.Dispose();
            throw new InvalidDataException("The Vorbis header is invalid.");
        }
    }

    public int SampleRate => _reader.SampleRate;

    public int Channels => _reader.Channels;

    public long LengthFrames => _reader.TotalSamples > 0 ? _reader.TotalSamples : -1;

    public int ReadFrames(Span<float> buffer)
    {
        int whole = buffer.Length - (buffer.Length % Channels);
        try
        {
            return _reader.ReadSamples(buffer[..whole]) / Channels;
        }
        catch (Exception ex) when (AudioDecoder.IsDecoderFault(ex))
        {
            throw AudioDecoder.Damaged(ex);
        }
    }

    public void SeekToFrame(long frame)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        long target = LengthFrames >= 0 ? Math.Min(frame, LengthFrames) : frame;
        try
        {
            long from = Math.Max(0, target - PreRollFrames);
            _reader.SamplePosition = from;
            DiscardFrames(target - from);
        }
        catch (Exception ex) when (AudioDecoder.IsDecoderFault(ex))
        {
            throw AudioDecoder.Damaged(ex);
        }
    }

    public void Dispose() => _reader.Dispose();

    private void DiscardFrames(long frames)
    {
        if (_discard.Length == 0)
            _discard = new float[1024 * Channels];

        while (frames > 0)
        {
            int count = (int)Math.Min(frames, _discard.Length / Channels);
            int read = _reader.ReadSamples(_discard.AsSpan(0, count * Channels)) / Channels;
            if (read == 0)
                return;
            frames -= read;
        }
    }
}
