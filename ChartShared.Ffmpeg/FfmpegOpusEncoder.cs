using ChartShared.Ffmpeg.Interop;

namespace ChartShared.Ffmpeg;

/// <summary>
/// Encodes 48 kHz float audio to Opus packets with libopus, through FFmpeg's <c>libopus</c> encoder: one packet per
/// 20 ms frame. The caller frames the stream and writes the container (<c>OggOpusWriter</c>), as it does for the managed
/// encoder this stands in for. Not thread-safe: one thread uses an encoder at a time.
/// </summary>
/// <remarks>
/// <see cref="IsAvailable"/> is false when FFmpeg isn't bundled or was built without libopus (<c>--enable-libopus
/// --enable-encoder=libopus</c>; see <c>build/ffmpeg/README.md</c>), and callers fall back to the managed encoder.
/// </remarks>
public sealed unsafe class FfmpegOpusEncoder : IDisposable
{
    /// <summary>FFmpeg's name for the libopus encoder.</summary>
    public const string EncoderName = "libopus";

    /// <summary>Opus always encodes at 48 kHz.</summary>
    public const int SampleRate = 48000;

    /// <summary>The frame length, in samples per channel: 20 ms.</summary>
    public const int FrameSize = 960;

    private void* _context;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private long _pts;

    /// <param name="channels">1 for mono, 2 for stereo.</param>
    /// <param name="bitrateBps">The bitrate in bits per second, for all channels.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channels"/> isn't 1 or 2, or the bitrate isn't positive.</exception>
    /// <exception cref="InvalidOperationException">FFmpeg can't be used, or has no libopus encoder.</exception>
    /// <exception cref="InvalidDataException">The encoder can't be set up.</exception>
    public FfmpegOpusEncoder(int channels, int bitrateBps)
    {
        if (channels is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(channels), "Only mono and stereo are encoded.");
        ArgumentOutOfRangeException.ThrowIfLessThan(bitrateBps, 1);

        FfmpegLibrary.EnsureAvailable();
        AVCodec* codec = Native.avcodec_find_encoder_by_name(EncoderName);
        if (codec == null)
            throw new InvalidOperationException("This build of FFmpeg has no libopus encoder; see build/ffmpeg/README.md.");

        Channels = channels;
        try
        {
            _context = Native.avcodec_alloc_context3(codec);
            if (_context == null)
                throw new OutOfMemoryException();

            // The stream's shape isn't an option, so it goes in through codec parameters.
            AVCodecParameters* parameters = Native.avcodec_parameters_alloc();
            if (parameters == null)
                throw new OutOfMemoryException();
            try
            {
                parameters->codec_type = Native.MediaTypeAudio;
                parameters->codec_id = codec->id;
                parameters->format = Native.SampleFormatFloat;
                parameters->sample_rate = SampleRate;
                parameters->ch_layout = Layout(channels);
                Check(Native.avcodec_parameters_to_context(_context, parameters), "The encoder's audio can't be set up");
            }
            finally
            {
                Native.avcodec_parameters_free(&parameters);
            }

            // The rest are options of the context itself, then libopus's own, which av_opt_set_* only reaches with
            // AV_OPT_SEARCH_CHILDREN. A sample is the time base, so a frame's pts counts samples.
            Check(Native.av_opt_set_q(_context, "time_base", new AVRational { num = 1, den = SampleRate }, Native.OptSearchChildren),
                "The encoder's time base can't be set");
            SetInt("b", bitrateBps);
            Check(Native.av_opt_set(_context, "application", "audio", Native.OptSearchChildren), "The encoder's application can't be set");
            Check(Native.av_opt_set(_context, "frame_duration", "20", Native.OptSearchChildren), "The encoder's frame duration can't be set");
            Check(Native.av_opt_set(_context, "vbr", "on", Native.OptSearchChildren), "The encoder's rate control can't be set");

            Check(Native.avcodec_open2(_context, codec, null), "The Opus encoder can't be opened");

            // What the stream needs to know: the encoder's lookahead, which is the Ogg Opus pre-skip.
            AVCodecParameters* opened = Native.avcodec_parameters_alloc();
            if (opened == null)
                throw new OutOfMemoryException();
            try
            {
                Check(Native.avcodec_parameters_from_context(opened, _context), "The encoder's parameters can't be read");
                Lookahead = opened->initial_padding;
            }
            finally
            {
                Native.avcodec_parameters_free(&opened);
            }

            _packet = Native.av_packet_alloc();
            _frame = Native.av_frame_alloc();
            if (_packet == null || _frame == null)
                throw new OutOfMemoryException();

            _frame->nb_samples = FrameSize;
            _frame->format = Native.SampleFormatFloat;
            _frame->sample_rate = SampleRate;
            _frame->ch_layout = Layout(channels);
            Check(Native.av_frame_get_buffer(_frame, 0), "The encoder's frame can't be allocated");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>1 for mono, 2 for stereo.</summary>
    public int Channels { get; }

    /// <summary>How many 48 kHz samples the encoder holds back: what a decoder skips from the start (the Ogg Opus pre-skip).</summary>
    public int Lookahead { get; }

    /// <summary>Whether FFmpeg's libraries are bundled and have the libopus encoder.</summary>
    public static bool IsAvailable => FfmpegLibrary.HasEncoder(EncoderName);

    /// <summary>Encodes one frame of <see cref="FrameSize"/> samples per channel.</summary>
    /// <param name="frame">Interleaved float samples, <see cref="FrameSize"/> × <see cref="Channels"/> of them.</param>
    /// <param name="packet">Receives the encoded packet; room for the largest an encoder makes (4000 bytes is plenty).</param>
    /// <returns>How many bytes of <paramref name="packet"/> were written.</returns>
    /// <exception cref="ArgumentException">The frame isn't one frame long, or the packet doesn't fit.</exception>
    /// <exception cref="InvalidDataException">The encoder fails, or gives no packet for the frame.</exception>
    public int EncodeFrame(ReadOnlySpan<float> frame, Span<byte> packet)
    {
        ObjectDisposedException.ThrowIf(_context == null, this);
        if (frame.Length != FrameSize * Channels)
            throw new ArgumentException($"A frame is {FrameSize * Channels} samples, not {frame.Length}.", nameof(frame));

        Check(Native.av_frame_make_writable(_frame), "The encoder's frame can't be written");
        frame.CopyTo(new Span<float>(_frame->extended_data[0], frame.Length));
        _frame->pts = _pts;
        _pts += FrameSize;

        Check(Native.avcodec_send_frame(_context, _frame), "A frame can't be encoded");

        // Every frame comes out as a packet of its own, which the caller's granule accounting relies on.
        int received = Native.avcodec_receive_packet(_context, _packet);
        if (received == Native.ErrorAgain || received == Native.ErrorEof)
            throw new InvalidDataException("The Opus encoder gave no packet for a frame.");
        Check(received, "A packet can't be read from the encoder");

        try
        {
            int size = _packet->size;
            if (size <= 0)
                throw new InvalidDataException("An audio frame couldn't be encoded to Opus.");
            if (size > packet.Length)
                throw new ArgumentException($"The packet is {size} bytes, which doesn't fit in {packet.Length}.", nameof(packet));

            new ReadOnlySpan<byte>(_packet->data, size).CopyTo(packet);
            return size;
        }
        finally
        {
            Native.av_packet_unref(_packet);
        }
    }

    public void Dispose()
    {
        // The encoder accounts for its lookahead as frames still queued, and says so on closing: flush them out, and drop
        // the packets, which the caller's own silence flush has already made unnecessary.
        if (_context != null && _packet != null && _pts > 0)
        {
            if (Native.avcodec_send_frame(_context, null) >= 0)
            {
                while (Native.avcodec_receive_packet(_context, _packet) >= 0)
                    Native.av_packet_unref(_packet);
            }
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

        if (_context != null)
        {
            void* context = _context;
            Native.avcodec_free_context(&context);
            _context = null;
        }
    }

    private void SetInt(string name, long value) =>
        Check(Native.av_opt_set_int(_context, name, value, Native.OptSearchChildren), $"The encoder's '{name}' can't be set to {value}");

    private static AVChannelLayout Layout(int channels) => new()
    {
        order = Native.ChannelOrderNative,
        nb_channels = channels,
        u = channels == 1 ? Native.ChannelMaskMono : Native.ChannelMaskStereo,
        opaque = null,
    };

    /// <summary>Throws an <see cref="InvalidDataException"/> naming FFmpeg's own description of a failure.</summary>
    private static void Check(int result, string message)
    {
        if (result < 0)
            throw new InvalidDataException($"{message} ({Describe(result)}).");
    }

    private static string Describe(int error)
    {
        byte* buffer = stackalloc byte[256];
        if (Native.av_strerror(error, buffer, 256) < 0)
            return $"FFmpeg error {error}";
        return $"FFmpeg error {error}: {System.Text.Encoding.UTF8.GetString(buffer, new ReadOnlySpan<byte>(buffer, 256).IndexOf((byte)0))}";
    }
}
