using ChartShared.Ffmpeg;

namespace ChartShared.Audio.Decoding;

/// <summary>Packets decoded by one of FFmpeg's native decoders, through <see cref="FfmpegAudioDecoder"/>.</summary>
/// <param name="errorsAreSilence">
/// Whether a packet that can't be decoded gives no frames rather than being damage: for MP3, whose source makes
/// such a frame silent, as NLayer's decoder gives nothing for a frame it can't decode.
/// </param>
internal sealed class FfmpegPacketDecoder(FfmpegAudioDecoder decoder, bool errorsAreSilence) : IPacketDecoder
{
    /// <summary>
    /// Opens FFmpeg's decoder called <paramref name="name"/>; see <see cref="FfmpegAudioDecoder(string, int, ReadOnlySpan{byte}, bool)"/>.
    /// </summary>
    /// <returns>
    /// On <see cref="AudioBackend.Auto"/>, <see langword="null"/> when FFmpeg won't open it with that setup, so a
    /// managed decoder can try instead.
    /// </returns>
    public static FfmpegPacketDecoder? Open(AudioBackend backend, string name, int channels, ReadOnlySpan<byte> extradata, bool trimsStart, bool errorsAreSilence)
    {
        try
        {
            return new FfmpegPacketDecoder(new FfmpegAudioDecoder(name, channels, extradata, trimsStart), errorsAreSilence);
        }
        catch (InvalidDataException) when (backend == AudioBackend.Auto)
        {
            return null;
        }
    }

    public int Decode(ReadOnlySpan<byte> packet, Span<float> output)
    {
        try
        {
            return decoder.Decode(packet, output);
        }
        catch (InvalidDataException) when (errorsAreSilence)
        {
            return 0;
        }
    }

    public void Reset() => decoder.Flush();

    public void Dispose() => decoder.Dispose();
}
