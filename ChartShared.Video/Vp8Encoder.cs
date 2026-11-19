using ChartShared.Ffmpeg;
using ChartShared.Ffmpeg.Interop;

namespace ChartShared.Video;

/// <summary>Receives each packet an encoder produces. The packet is the encoder's: valid until the handler returns.</summary>
internal unsafe delegate void PacketHandler(AVPacket* packet);

/// <summary>
/// One VP8 encode through libvpx: frames in, packets out. The codec context is opaque, so everything it needs is set
/// through the option API before it's opened. Not thread-safe.
/// </summary>
internal sealed unsafe class Vp8Encoder : IDisposable
{
    /// <summary>FFmpeg's name for the libvpx VP8 encoder (<c>--enable-encoder=libvpx_vp8</c> builds it).</summary>
    public const string EncoderName = "libvpx";

    private void* _context;
    private AVPacket* _packet;

    /// <summary>What an encode is set up with, taken from the source's first frame.</summary>
    /// <param name="Width">The output width; even.</param>
    /// <param name="Height">The output height; even.</param>
    /// <param name="FrameRate">The output frame rate, which is also the reciprocal of <see cref="TimeBase"/>.</param>
    /// <param name="SampleAspectRatio">The shape of a pixel; 0/1 or 1/1 when square or unknown.</param>
    /// <param name="Colour">The colour tags the frames carry, written to the stream so a player decodes them as they were meant.</param>
    public readonly record struct Setup(int Width, int Height, AVRational FrameRate, AVRational SampleAspectRatio, FrameColour Colour);

    /// <summary>A frame's colour tags (<c>AVColorRange</c> and its siblings), as FFmpeg numbers them.</summary>
    public readonly record struct FrameColour(int Range, int Space, int Primaries, int Transfer, int ChromaLocation);

    public Vp8Encoder(Setup setup, VideoReencodeSettings settings)
    {
        FfmpegLibrary.EnsureAvailable();
        AVCodec* codec = Native.avcodec_find_encoder_by_name(EncoderName);
        if (codec == null)
            throw new InvalidOperationException("This build of FFmpeg has no VP8 encoder (libvpx); see build/ffmpeg/README.md.");

        Width = setup.Width;
        Height = setup.Height;
        TimeBase = new AVRational { num = setup.FrameRate.den, den = setup.FrameRate.num };
        try
        {
            _context = Native.avcodec_alloc_context3(codec);
            if (_context == null)
                throw new OutOfMemoryException();

            // The picture's size and format aren't options, so they go in through codec parameters, which also carry the colour
            // tags (advisory: written to the stream so a player decodes the frames as they were meant to be).
            AVCodecParameters* parameters = Native.avcodec_parameters_alloc();
            if (parameters == null)
                throw new OutOfMemoryException();
            try
            {
                parameters->codec_type = Native.MediaTypeVideo;
                parameters->codec_id = codec->id;
                parameters->width = setup.Width;
                parameters->height = setup.Height;
                parameters->format = Native.PixelFormatYuv420p;
                parameters->sample_aspect_ratio = setup.SampleAspectRatio.num > 0 && setup.SampleAspectRatio.den > 0
                    ? setup.SampleAspectRatio
                    : new AVRational { num = 1, den = 1 };
                parameters->color_range = setup.Colour.Range;
                parameters->color_space = setup.Colour.Space;
                parameters->color_primaries = setup.Colour.Primaries;
                parameters->color_trc = setup.Colour.Transfer;
                parameters->chroma_location = setup.Colour.ChromaLocation;
                Check(Native.avcodec_parameters_to_context(_context, parameters), "The encoder's picture can't be set up");
            }
            finally
            {
                Native.avcodec_parameters_free(&parameters);
            }

            // The rest are options of the context itself, then libvpx's own, which av_opt_set_* only reaches with
            // AV_OPT_SEARCH_CHILDREN.
            Check(Native.av_opt_set_q(_context, "time_base", TimeBase, Native.OptSearchChildren), "The encoder's time base can't be set");
            Native.av_opt_set_q(_context, "framerate", setup.FrameRate, Native.OptSearchChildren);

            // Constrained quality: libvpx's CQ level, with the bitrate as the most it may spend. The WebM muxer wants
            // the stream's header written once, not in every packet.
            SetInt("crf", settings.Cq);
            SetInt("b", (long)settings.EffectiveMaxBitrateKbps * 1000);
            SetInt("g", VideoReencodeSettings.GopFrames);
            // Slower is better compression: cpu-used runs from 0 (slowest) to 5 and up. Threads share the machine with the
            // other encodes running at once, which is the optimizer's to say.
            SetInt("cpu-used", settings.EffectiveCpuUsed);
            if (settings.Realtime)
                Check(Native.av_opt_set(_context, "deadline", "realtime", Native.OptSearchChildren), "The encoder's deadline can't be set");
            if (settings.Slices > 0)
                SetInt("slices", settings.Slices);
            SetInt("threads", settings.Threads > 0 ? settings.Threads : Math.Clamp(Environment.ProcessorCount, 1, 8));
            Check(Native.av_opt_set(_context, "flags", "+global_header", Native.OptSearchChildren), "The encoder's flags can't be set");

            Check(Native.avcodec_open2(_context, codec, null), "The VP8 encoder can't be opened");

            _packet = Native.av_packet_alloc();
            if (_packet == null)
                throw new OutOfMemoryException();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>The width of the frames it takes.</summary>
    public int Width { get; }

    /// <summary>The height of the frames it takes.</summary>
    public int Height { get; }

    /// <summary>The unit of the frames' <c>pts</c>: one frame, the reciprocal of the frame rate.</summary>
    public AVRational TimeBase { get; }

    /// <summary>Copies what a muxer needs to know about the stream.</summary>
    public void CopyParametersTo(AVCodecParameters* parameters) =>
        Check(Native.avcodec_parameters_from_context(parameters, _context), "The encoder's parameters can't be copied");

    /// <summary>Sends <paramref name="frame"/> (<see langword="null"/> to finish) and hands every packet that's ready to <paramref name="handler"/>.</summary>
    /// <exception cref="InvalidDataException">The encoder fails.</exception>
    public void Encode(AVFrame* frame, PacketHandler handler)
    {
        ObjectDisposedException.ThrowIf(_context == null, this);
        Check(Native.avcodec_send_frame(_context, frame), "A frame can't be encoded");
        while (true)
        {
            int received = Native.avcodec_receive_packet(_context, _packet);
            if (received == Native.ErrorAgain || received == Native.ErrorEof)
                return;
            Check(received, "A packet can't be read from the encoder");

            try
            {
                handler(_packet);
            }
            finally
            {
                Native.av_packet_unref(_packet);
            }
        }
    }

    public void Dispose()
    {
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

    /// <summary>Throws an <see cref="InvalidDataException"/> naming FFmpeg's own description of a failure.</summary>
    internal static void Check(int result, string message)
    {
        if (result < 0)
            throw new InvalidDataException($"{message} ({Describe(result)}).");
    }

    /// <summary>FFmpeg's words for an error code, with the code.</summary>
    internal static string Describe(int error)
    {
        byte* buffer = stackalloc byte[256];
        if (Native.av_strerror(error, buffer, 256) < 0)
            return $"FFmpeg error {error}";
        return $"FFmpeg error {error}: {System.Text.Encoding.UTF8.GetString(buffer, new ReadOnlySpan<byte>(buffer, 256).IndexOf((byte)0))}";
    }
}
