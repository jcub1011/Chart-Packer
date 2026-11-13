using ChartShared.Audio.Decoding;
using ChartShared.Ffmpeg;
using Concentus;
using Concentus.Enums;

namespace ChartShared.Audio.Encoding;

/// <summary>Which encoder <see cref="OpusTranscoder"/> uses.</summary>
internal enum OpusEncoderBackend
{
    /// <summary>libopus through FFmpeg when its libraries have it, Concentus otherwise.</summary>
    Auto,

    /// <summary>Concentus, the managed encoder.</summary>
    Managed,

    /// <summary>libopus through FFmpeg; opening one throws when FFmpeg can't provide it.</summary>
    Native,
}

/// <summary>Encodes 48 kHz float audio to Opus packets of 20 ms (960 samples per channel) each.</summary>
internal interface IOpusPacketEncoder : IDisposable
{
    /// <summary>Whether this is libopus (through FFmpeg), not the managed encoder.</summary>
    bool IsNative { get; }

    /// <summary>How many 48 kHz samples the encoder holds back: the Ogg Opus pre-skip.</summary>
    int Lookahead { get; }

    /// <summary>Encodes one frame of interleaved samples into <paramref name="packet"/>, returning the packet's length.</summary>
    /// <exception cref="InvalidDataException">The frame can't be encoded.</exception>
    int EncodeFrame(ReadOnlySpan<float> frame, Span<byte> packet);
}

/// <summary>Opens the encoder <see cref="OpusTranscoder"/> asks for.</summary>
internal static class OpusPacketEncoders
{
    /// <summary>Whether libopus is available through FFmpeg.</summary>
    public static bool NativeAvailable => FfmpegOpusEncoder.IsAvailable;

    /// <param name="channels">1 for mono, 2 for stereo.</param>
    /// <param name="bitrateBps">The bitrate in bits per second, for all channels.</param>
    /// <exception cref="InvalidDataException">The audio can't be encoded.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="backend"/> is <see cref="OpusEncoderBackend.Native"/> and can't be had.</exception>
    public static IOpusPacketEncoder Create(int channels, int bitrateBps, OpusEncoderBackend backend = OpusEncoderBackend.Auto)
    {
        if (backend != OpusEncoderBackend.Managed && NativeAvailable)
        {
            try
            {
                return new FfmpegPacketEncoder(new FfmpegOpusEncoder(channels, bitrateBps));
            }
            catch (Exception ex) when (backend == OpusEncoderBackend.Auto
                && ex is InvalidOperationException or InvalidDataException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                // Fall through to the managed encoder, as the resampler falls back from swresample.
            }
        }
        else if (backend == OpusEncoderBackend.Native)
        {
            throw new InvalidOperationException("This build of FFmpeg has no libopus encoder; see build/ffmpeg/README.md.");
        }

        return new ConcentusPacketEncoder(channels, bitrateBps);
    }

    private sealed class FfmpegPacketEncoder(FfmpegOpusEncoder encoder) : IOpusPacketEncoder
    {
        public bool IsNative => true;

        public int Lookahead => encoder.Lookahead;

        public int EncodeFrame(ReadOnlySpan<float> frame, Span<byte> packet) => encoder.EncodeFrame(frame, packet);

        public void Dispose() => encoder.Dispose();
    }

    private sealed class ConcentusPacketEncoder : IOpusPacketEncoder
    {
        private const int FrameSize = 960;

        private readonly IOpusEncoder _encoder;

        public ConcentusPacketEncoder(int channels, int bitrateBps)
        {
            try
            {
                _encoder = OpusCodecFactory.CreateEncoder(FfmpegOpusEncoder.SampleRate, channels, OpusApplication.OPUS_APPLICATION_AUDIO);
                _encoder.Bitrate = bitrateBps;
            }
            catch (Exception ex) when (AudioDecoder.IsDecoderFault(ex))
            {
                throw AudioDecoder.Damaged(ex);
            }
        }

        public bool IsNative => false;

        public int Lookahead => _encoder.Lookahead;

        public int EncodeFrame(ReadOnlySpan<float> frame, Span<byte> packet)
        {
            int length;
            try
            {
                length = _encoder.Encode(frame, FrameSize, packet, packet.Length);
            }
            catch (Exception ex) when (AudioDecoder.IsDecoderFault(ex))
            {
                throw AudioDecoder.Damaged(ex);
            }

            if (length <= 0)
                throw new InvalidDataException("An audio frame couldn't be encoded to Opus.");
            return length;
        }

        // Concentus holds nothing that needs freeing.
        public void Dispose()
        {
        }
    }
}
