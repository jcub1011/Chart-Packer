using Concentus;

namespace ChartPacker.Audio.Decoding;

/// <summary>Opus packets decoded by <see href="https://github.com/lostromb/concentus">Concentus</see>, at 48 kHz.</summary>
internal sealed class ConcentusDecoder : IPacketDecoder
{
    private const int Rate = 48000;

    private readonly IOpusDecoder? _decoder;
    private readonly IOpusMultiStreamDecoder? _multiStreamDecoder;
    private readonly int _channels;

    /// <param name="mapping">
    /// The channel mapping table of <c>OpusHead</c> (RFC 7845 §5.1.1), with its stream counts; <see langword="null"/>
    /// for mapping family 0, one mono or stereo stream.
    /// </param>
    /// <exception cref="InvalidDataException">Concentus won't decode with that setup.</exception>
    public ConcentusDecoder(int channels, (int Streams, int Coupled, byte[] Table)? mapping)
    {
        _channels = channels;
        try
        {
            if (mapping is { } map)
                _multiStreamDecoder = OpusCodecFactory.CreateMultiStreamDecoder(Rate, channels, map.Streams, map.Coupled, map.Table, null);
            else
                _decoder = OpusCodecFactory.CreateDecoder(Rate, channels, null);
        }
        catch (Exception ex) when (AudioDecoder.IsDecoderFault(ex))
        {
            throw AudioDecoder.Damaged(ex);
        }
    }

    public int Decode(ReadOnlySpan<byte> packet, Span<float> output)
    {
        int capacity = output.Length / _channels;
        int frames;
        try
        {
            frames = _decoder is not null
                ? _decoder.Decode(packet, output, capacity, false)
                : _multiStreamDecoder!.DecodeMultistream(packet, output, capacity, false);
        }
        catch (Exception ex) when (AudioDecoder.IsDecoderFault(ex))
        {
            throw AudioDecoder.Damaged(ex);
        }

        return frames >= 0 ? frames : throw new InvalidDataException("An Opus packet couldn't be decoded.");
    }

    public void Reset()
    {
        _decoder?.ResetState();
        _multiStreamDecoder?.ResetState();
    }

    public void Dispose()
    {
        // Concentus holds nothing that needs freeing.
    }
}
