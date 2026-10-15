using System.Runtime.InteropServices;

namespace ChartPacker.Ffmpeg.Interop;

// Written from the public headers of FFmpeg 9.0 (libavformat 63, libavcodec 63, libavutil 61,
// libswscale 10). Only the functions this project calls are bound, and the structs declare only
// their leading fields, up to the last one read; the rest of each struct is FFmpeg's to allocate.
// FfmpegLibrary refuses any other major version, since the layouts can change between them.
// FFmpeg's own names are kept, so each binding reads like its header. Video uses avformat, avcodec
// and swscale; audio only avcodec's decoders, given packets our own code has read.

/// <summary>The leading fields of <c>AVCodec</c> (libavcodec/codec.h).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AVCodec
{
    public byte* name;
    public byte* long_name;
    public int type;
    public int id;
}

/// <summary><c>AVRational</c>: a fraction.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AVRational
{
    public int num;
    public int den;
}

/// <summary>The leading fields of <c>AVFormatContext</c> (libavformat/avformat.h).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AVFormatContext
{
    public void* av_class;
    public void* iformat;
    public void* oformat;
    public void* priv_data;
    public AVIOContext* pb;
    public int ctx_flags;
    public uint nb_streams;
    public AVStream** streams;
    public uint nb_stream_groups;
    public void** stream_groups;
    public uint nb_chapters;
    public void** chapters;
    public byte* url;
    public long start_time;
    public long duration;
}

/// <summary>The leading fields of <c>AVStream</c> (libavformat/avformat.h).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AVStream
{
    public void* av_class;
    public int index;
    public int id;
    public AVCodecParameters* codecpar;
    public void* priv_data;
    public AVRational time_base;
    public long start_time;
    public long duration;
}

/// <summary>The leading fields of <c>AVCodecParameters</c> (libavcodec/codec_par.h).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AVCodecParameters
{
    public int codec_type;
    public int codec_id;
    public uint codec_tag;
    public byte* extradata;
    public int extradata_size;
    public void* coded_side_data;
    public int nb_coded_side_data;
    public int format;
    public long bit_rate;
    public int bits_per_coded_sample;
    public int bits_per_raw_sample;
    public int profile;
    public int level;
    public int width;
    public int height;
}

/// <summary>The leading fields of <c>AVIOContext</c> (libavformat/avio.h).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AVIOContext
{
    public void* av_class;
    public byte* buffer;
}

/// <summary>The leading fields of <c>AVPacket</c> (libavcodec/packet.h).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AVPacket
{
    public void* buf;
    public long pts;
    public long dts;
    public byte* data;
    public int size;
    public int stream_index;
}

/// <summary>The leading fields of <c>AVFrame</c> (libavutil/frame.h).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AVFrame
{
    public fixed ulong data[8];
    public fixed int linesize[8];
    public byte** extended_data;
    public int width;
    public int height;
    public int nb_samples;
    public int format;
    public int pict_type;
    public AVRational sample_aspect_ratio;
    public long pts;
    public long pkt_dts;
    public AVRational time_base;
    public int quality;
    public void* opaque;
    public int repeat_pict;
    public int sample_rate;
    public fixed ulong buf[8];
    public void** extended_buf;
    public int nb_extended_buf;
    public void** side_data;
    public int nb_side_data;
    public int flags;
    public int color_range;
    public int color_primaries;
    public int color_trc;
    public int colorspace;
    public int chroma_location;
    public long best_effort_timestamp;
    public void* metadata;
    public int decode_error_flags;
    public void* hw_frames_ctx;
    public void* opaque_ref;
    public nuint crop_top;
    public nuint crop_bottom;
    public nuint crop_left;
    public nuint crop_right;
    public void* private_ref;
    public AVChannelLayout ch_layout;
}

/// <summary><c>AVChannelLayout</c> (libavutil/channel_layout.h), whose union is a 64-bit mask or a pointer.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AVChannelLayout
{
    public int order;
    public int nb_channels;
    public ulong u;
    public void* opaque;
}

/// <summary>The FFmpeg functions and constants used to decode video and audio.</summary>
internal static unsafe partial class Native
{
    public const string AvFormat = "avformat";
    public const string AvCodec = "avcodec";
    public const string AvUtil = "avutil";
    public const string SwScale = "swscale";
    public const string SwResample = "swresample";

    // The major versions the struct layouts above are written for.
    public const int AvFormatMajor = 63;
    public const int AvCodecMajor = 63;
    public const int AvUtilMajor = 61;
    public const int SwScaleMajor = 10;
    public const int SwResampleMajor = 7;

    public const long NoPtsValue = long.MinValue;
    public const int TimeBase = 1_000_000;
    public const int MediaTypeVideo = 0;
    public const int MediaTypeAudio = 1;

    // libavutil/samplefmt.h: AV_SAMPLE_FMT_FLT and AV_SAMPLE_FMT_FLTP.
    public const int SampleFormatFloat = 3;
    public const int SampleFormatFloatPlanar = 8;

    // libavcodec/defs.h: AV_INPUT_BUFFER_PADDING_SIZE, zeroed bytes every buffer given to a decoder must have after it.
    public const int InputBufferPadding = 64;
    // libavutil/channel_layout.h: AV_CHANNEL_ORDER_NATIVE and the channel masks.
    public const int ChannelOrderNative = 1;
    public const ulong ChannelMaskMono = 0x4;
    public const ulong ChannelMaskStereo = 0x3;
    public const int PixelFormatBgra = 28;
    public const int SwsBilinear = 2;
    public const int SeekFlagBackward = 1;
    public const int SeekSize = 0x10000;
    public const int SeekForce = 0x20000;

    // FFERRTAG('E','O','F',' ').
    public const int ErrorEof = -('E' | ('O' << 8) | ('F' << 16) | (' ' << 24));

    // AVERROR(EIO): EIO is 5 everywhere FFmpeg runs.
    public const int ErrorIo = -5;

    // FFERRTAG('I','N','D','A'): AVERROR_INVALIDDATA.
    public const int ErrorInvalidData = -('I' | ('N' << 8) | ('D' << 16) | ('A' << 24));

    /// <summary>AVERROR(EAGAIN): EAGAIN is 35 on macOS and 11 elsewhere.</summary>
    public static int ErrorAgain { get; } = OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() ? -35 : -11;

    // libavutil
    [LibraryImport(AvUtil, EntryPoint = "avutil_version")]
    public static partial uint avutil_version();

    [LibraryImport(AvUtil, EntryPoint = "av_malloc")]
    public static partial void* av_malloc(nuint size);

    [LibraryImport(AvUtil, EntryPoint = "av_mallocz")]
    public static partial void* av_mallocz(nuint size);

    [LibraryImport(AvUtil, EntryPoint = "av_freep")]
    public static partial void av_freep(void* ptr);

    [LibraryImport(AvUtil, EntryPoint = "av_frame_alloc")]
    public static partial AVFrame* av_frame_alloc();

    [LibraryImport(AvUtil, EntryPoint = "av_frame_free")]
    public static partial void av_frame_free(AVFrame** frame);

    [LibraryImport(AvUtil, EntryPoint = "av_frame_unref")]
    public static partial void av_frame_unref(AVFrame* frame);

    [LibraryImport(AvUtil, EntryPoint = "av_dict_set", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int av_dict_set(void** dictionary, string key, string value, int flags);

    [LibraryImport(AvUtil, EntryPoint = "av_dict_free")]
    public static partial void av_dict_free(void** dictionary);

    // libavformat
    [LibraryImport(AvFormat, EntryPoint = "avformat_version")]
    public static partial uint avformat_version();

    [LibraryImport(AvFormat, EntryPoint = "avformat_alloc_context")]
    public static partial AVFormatContext* avformat_alloc_context();

    [LibraryImport(AvFormat, EntryPoint = "avformat_open_input")]
    public static partial int avformat_open_input(AVFormatContext** context, byte* url, void* format, void** options);

    [LibraryImport(AvFormat, EntryPoint = "avformat_find_stream_info")]
    public static partial int avformat_find_stream_info(AVFormatContext* context, void** options);

    [LibraryImport(AvFormat, EntryPoint = "av_find_best_stream")]
    public static partial int av_find_best_stream(AVFormatContext* context, int type, int wanted, int related, void** decoder, int flags);

    [LibraryImport(AvFormat, EntryPoint = "av_read_frame")]
    public static partial int av_read_frame(AVFormatContext* context, AVPacket* packet);

    [LibraryImport(AvFormat, EntryPoint = "av_seek_frame")]
    public static partial int av_seek_frame(AVFormatContext* context, int streamIndex, long timestamp, int flags);

    [LibraryImport(AvFormat, EntryPoint = "avformat_close_input")]
    public static partial void avformat_close_input(AVFormatContext** context);

    [LibraryImport(AvFormat, EntryPoint = "avio_alloc_context")]
    public static partial AVIOContext* avio_alloc_context(
        byte* buffer, int bufferSize, int writeFlag, void* opaque,
        delegate* unmanaged[Cdecl]<void*, byte*, int, int> readPacket,
        void* writePacket,
        delegate* unmanaged[Cdecl]<void*, long, int, long> seek);

    [LibraryImport(AvFormat, EntryPoint = "avio_context_free")]
    public static partial void avio_context_free(AVIOContext** context);

    // libavcodec
    [LibraryImport(AvCodec, EntryPoint = "avcodec_version")]
    public static partial uint avcodec_version();

    [LibraryImport(AvCodec, EntryPoint = "avcodec_find_decoder_by_name", StringMarshalling = StringMarshalling.Utf8)]
    public static partial AVCodec* avcodec_find_decoder_by_name(string name);

    [LibraryImport(AvCodec, EntryPoint = "avcodec_parameters_alloc")]
    public static partial AVCodecParameters* avcodec_parameters_alloc();

    [LibraryImport(AvCodec, EntryPoint = "avcodec_parameters_free")]
    public static partial void avcodec_parameters_free(AVCodecParameters** parameters);

    [LibraryImport(AvCodec, EntryPoint = "avcodec_alloc_context3")]
    public static partial void* avcodec_alloc_context3(void* codec);

    [LibraryImport(AvCodec, EntryPoint = "avcodec_parameters_to_context")]
    public static partial int avcodec_parameters_to_context(void* codecContext, AVCodecParameters* parameters);

    [LibraryImport(AvCodec, EntryPoint = "avcodec_open2")]
    public static partial int avcodec_open2(void* codecContext, void* codec, void** options);

    [LibraryImport(AvCodec, EntryPoint = "avcodec_send_packet")]
    public static partial int avcodec_send_packet(void* codecContext, AVPacket* packet);

    [LibraryImport(AvCodec, EntryPoint = "avcodec_receive_frame")]
    public static partial int avcodec_receive_frame(void* codecContext, AVFrame* frame);

    [LibraryImport(AvCodec, EntryPoint = "avcodec_flush_buffers")]
    public static partial void avcodec_flush_buffers(void* codecContext);

    [LibraryImport(AvCodec, EntryPoint = "avcodec_free_context")]
    public static partial void avcodec_free_context(void** codecContext);

    [LibraryImport(AvCodec, EntryPoint = "av_packet_alloc")]
    public static partial AVPacket* av_packet_alloc();

    [LibraryImport(AvCodec, EntryPoint = "av_packet_free")]
    public static partial void av_packet_free(AVPacket** packet);

    [LibraryImport(AvCodec, EntryPoint = "av_packet_unref")]
    public static partial void av_packet_unref(AVPacket* packet);

    // libswscale
    [LibraryImport(SwScale, EntryPoint = "swscale_version")]
    public static partial uint swscale_version();
    [LibraryImport(SwScale, EntryPoint = "sws_getContext")]
    public static partial void* sws_getContext(int srcWidth, int srcHeight, int srcFormat, int dstWidth, int dstHeight, int dstFormat, int flags, void* srcFilter, void* dstFilter, double* param);

    [LibraryImport(SwScale, EntryPoint = "sws_scale")]
    public static partial int sws_scale(void* context, byte** srcSlice, int* srcStride, int srcSliceY, int srcSliceHeight, byte** dst, int* dstStride);

    [LibraryImport(SwScale, EntryPoint = "sws_freeContext")]
    public static partial void sws_freeContext(void* context);

    // libswresample: only the resampler used to convert decoded audio to 48 kHz for Opus encoding.
    [LibraryImport(SwResample, EntryPoint = "swresample_version")]
    public static partial uint swresample_version();

    [LibraryImport(SwResample, EntryPoint = "swr_alloc_set_opts2")]
    public static partial int swr_alloc_set_opts2(
        void** ps,
        AVChannelLayout* out_ch_layout,
        int out_sample_fmt,
        int out_sample_rate,
        AVChannelLayout* in_ch_layout,
        int in_sample_fmt,
        int in_sample_rate,
        int log_offset,
        void* log_ctx);

    [LibraryImport(SwResample, EntryPoint = "swr_init")]
    public static partial int swr_init(void* s);

    [LibraryImport(SwResample, EntryPoint = "swr_convert")]
    public static partial int swr_convert(void* s, byte** output, int out_count, byte** input, int in_count);

    [LibraryImport(SwResample, EntryPoint = "swr_free")]
    public static partial void swr_free(void** s);
}
