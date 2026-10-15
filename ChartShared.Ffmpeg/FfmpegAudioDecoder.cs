using System.Collections.Concurrent;
using ChartShared.Ffmpeg.Interop;

namespace ChartShared.Ffmpeg;

/// <summary>
/// One of FFmpeg's audio decoders, fed packets the caller has read from the container itself, giving
/// interleaved float frames. The container, the timeline and any trimming stay the caller's.
/// </summary>
/// <remarks>
/// Each decoder runs on one thread (<c>threads=1</c>): callers read several streams, and chunks of one
/// stream, side by side already. Not thread-safe: one thread uses it at a time.
/// </remarks>
public sealed unsafe class FfmpegAudioDecoder : IDisposable
{
    private static readonly ConcurrentDictionary<string, bool> Found = new(StringComparer.Ordinal);

    private void* _codec;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private byte* _buffer;
    private int _bufferSize;

    /// <summary>Opens FFmpeg's decoder called <paramref name="decoder"/>, such as <c>opus</c> or <c>mp3float</c>.</summary>
    /// <param name="channels">How many channels the stream's headers say it has; every frame decoded must have as many.</param>
    /// <param name="extradata">The codec's setup, as FFmpeg's decoder takes it: the <c>OpusHead</c> packet, say. May be empty.</param>
    /// <param name="trimsStart">
    /// Whether FFmpeg drops the samples its decoder asks it to at the start and after <see cref="Flush"/>, such as the
    /// output of Vorbis's first packet, which only primes the decoder. Otherwise nothing is dropped, and the caller
    /// trims what it should, such as Opus's pre-skip.
    /// </param>
    /// <exception cref="InvalidOperationException">FFmpeg, or that decoder, isn't available; see <see cref="IsAvailable"/>.</exception>
    /// <exception cref="InvalidDataException">The decoder won't open with <paramref name="extradata"/>.</exception>
    public FfmpegAudioDecoder(string decoder, int channels, ReadOnlySpan<byte> extradata, bool trimsStart)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        FfmpegLibrary.EnsureAvailable();

        AVCodec* codec = Native.avcodec_find_decoder_by_name(decoder);
        if (codec == null)
            throw new InvalidOperationException($"FFmpeg has no '{decoder}' decoder.");

        Channels = channels;
        AVCodecParameters* parameters = null;
        try
        {
            parameters = Native.avcodec_parameters_alloc();
            if (parameters == null)
                throw new OutOfMemoryException();
            parameters->codec_type = Native.MediaTypeAudio;
            parameters->codec_id = codec->id;

            if (!extradata.IsEmpty)
            {
                // Freed with the parameters. Decoders may read past the end, so it's padded with zeroes.
                parameters->extradata = (byte*)Native.av_mallocz((nuint)(extradata.Length + Native.InputBufferPadding));
                if (parameters->extradata == null)
                    throw new OutOfMemoryException();
                extradata.CopyTo(new Span<byte>(parameters->extradata, extradata.Length));
                parameters->extradata_size = extradata.Length;
            }

            _codec = Native.avcodec_alloc_context3(codec);
            if (_codec == null)
                throw new OutOfMemoryException();
            Check(Native.avcodec_parameters_to_context(_codec, parameters), $"The '{decoder}' decoder can't be set up");

            void* options = null;
            Native.av_dict_set(&options, "threads", "1", 0);
            if (!trimsStart)
                Native.av_dict_set(&options, "flags2", "+skip_manual", 0);
            int result = Native.avcodec_open2(_codec, codec, &options);
            Native.av_dict_free(&options);
            Check(result, $"The '{decoder}' decoder can't be opened");

            _packet = Native.av_packet_alloc();
            _frame = Native.av_frame_alloc();
            if (_packet == null || _frame == null)
                throw new OutOfMemoryException();
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            if (parameters != null)
                Native.avcodec_parameters_free(&parameters);
        }
    }

    public int Channels { get; }

    /// <summary>Whether FFmpeg's libraries loaded and have the decoder called <paramref name="decoder"/>.</summary>
    /// <remarks>Libraries built for video alone have no audio decoders.</remarks>
    public static bool IsAvailable(string decoder)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        return FfmpegLibrary.IsAvailable && Found.GetOrAdd(decoder, static name => Native.avcodec_find_decoder_by_name(name) != null);
    }

    /// <summary>Decodes <paramref name="packet"/> into <paramref name="output"/> from its start, interleaved.</summary>
    /// <returns>How many frames were written. It can be 0, while the decoder primes itself or drops samples.</returns>
    /// <exception cref="InvalidDataException">The packet can't be decoded, or decodes to more than <paramref name="output"/> holds.</exception>
    public int Decode(ReadOnlySpan<byte> packet, Span<float> output)
    {
        ObjectDisposedException.ThrowIf(_codec == null, this);

        // An empty packet would tell the decoder the stream has ended.
        if (packet.IsEmpty)
            return 0;

        if (_bufferSize < packet.Length)
            Grow(packet.Length);

        packet.CopyTo(new Span<byte>(_buffer, packet.Length));
        new Span<byte>(_buffer + packet.Length, Native.InputBufferPadding).Clear();

        // Not reference-counted, so FFmpeg copies it and it stays ours.
        _packet->data = _buffer;
        _packet->size = packet.Length;
        Check(Native.avcodec_send_packet(_codec, _packet), "The packet can't be decoded");

        int written = 0;
        while (true)
        {
            int received = Native.avcodec_receive_frame(_codec, _frame);
            if (received == Native.ErrorAgain || received == Native.ErrorEof)
                return written;
            Check(received, "The packet can't be decoded");

            try
            {
                written += Interleave(output[(written * Channels)..]);
            }
            finally
            {
                Native.av_frame_unref(_frame);
            }
        }
    }

    /// <summary>Forgets what's been decoded, as before the first packet, for a seek.</summary>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_codec == null, this);
        Native.avcodec_flush_buffers(_codec);
    }

    public void Dispose()
    {
        if (_buffer != null)
        {
            byte* buffer = _buffer;
            Native.av_freep(&buffer);
            _buffer = null;
            _bufferSize = 0;
        }

        if (_frame != null)
        {
            AVFrame* frame = _frame;
            Native.av_frame_free(&frame);
            _frame = null;
        }

        if (_packet != null)
        {
            AVPacket* packet = _packet;
            Native.av_packet_free(&packet);
            _packet = null;
        }

        if (_codec != null)
        {
            void* codec = _codec;
            Native.avcodec_free_context(&codec);
            _codec = null;
        }
    }

    /// <summary>Replaces the packet buffer with one of at least <paramref name="length"/> bytes, plus the padding.</summary>
    private void Grow(int length)
    {
        int size = Math.Max(length, _bufferSize * 2);
        byte* old = _buffer;
        Native.av_freep(&old);
        _buffer = null;
        _bufferSize = 0;

        _buffer = (byte*)Native.av_malloc((nuint)(size + Native.InputBufferPadding));
        if (_buffer == null)
            throw new OutOfMemoryException();
        _bufferSize = size;
    }

    private int Interleave(Span<float> output)
    {
        int count = _frame->nb_samples;
        if (_frame->ch_layout.nb_channels != Channels)
            throw new InvalidDataException($"The decoder gave {_frame->ch_layout.nb_channels} channels, not {Channels}.");
        if ((long)count * Channels > output.Length)
            throw new InvalidDataException("A packet decoded to more frames than it can hold.");

        if (_frame->format == Native.SampleFormatFloatPlanar)
        {
            for (int c = 0; c < Channels; c++)
            {
                float* plane = (float*)_frame->extended_data[c];
                for (int i = 0; i < count; i++)
                    output[(i * Channels) + c] = plane[i];
            }
        }
        else if (_frame->format == Native.SampleFormatFloat)
        {
            new ReadOnlySpan<float>(_frame->extended_data[0], count * Channels).CopyTo(output);
        }
        else
        {
            throw new InvalidDataException($"The decoder gave samples in format {_frame->format}, not floats.");
        }

        return count;
    }

    private static void Check(int result, string message)
    {
        if (result < 0)
            throw new InvalidDataException($"{message} (FFmpeg error {result}).");
    }
}
