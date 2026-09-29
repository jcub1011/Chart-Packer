using NLayer;

namespace ChartPacker.Audio.Decoding;

/// <summary>MPEG audio frames decoded by <see href="https://github.com/naudio/NLayer">NLayer</see>'s frame decoder.</summary>
internal sealed class NLayerDecoder : IPacketDecoder
{
    private readonly MpegFrameDecoder _decoder = new() { StereoMode = StereoMode.Both };
    private readonly MpegFrame _frame = new();

    /// <summary>Decodes one whole frame, header included.</summary>
    /// <returns>0 when NLayer can't decode it, as when its bit reservoir is missing after a seek.</returns>
    public int Decode(ReadOnlySpan<byte> packet, Span<float> output)
    {
        var header = MpegFrame.ParseHeader(packet) ?? throw new InvalidDataException("An MPEG audio frame has no header.");
        _frame.Load(packet, header);
        try
        {
            return _decoder.DecodeFrame(_frame, output) / header.Channels;
        }
        catch (Exception ex) when (AudioDecoder.IsDecoderFault(ex))
        {
            throw AudioDecoder.Damaged(ex);
        }
    }

    public void Reset() => _decoder.Reset();

    public void Dispose()
    {
        // NLayer holds nothing that needs freeing.
    }
}
