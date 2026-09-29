using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ChartPacker.Video;

namespace DecodeBench;

// A rough prototype, to time FFmpeg's own audio decoders on the files the managed ones read: FFmpeg
// demuxes as well, so its timings include the container as the managed ones do. Its output isn't
// trimmed or checked against theirs, only counted. Written from the
// public headers of FFmpeg 9.0 (libavformat 63, libavcodec 63, libavutil 61), as
// ChartPacker.Video/Interop/Ffmpeg.cs is; the structs declare only their leading fields.

/// <summary>Decodes whole files with FFmpeg's decoder for their audio stream, one thread each, discarding the samples.</summary>
internal static unsafe partial class FfmpegReference
{
    private const string AvFormat = "avformat";
    private const string AvCodec = "avcodec";
    private const string AvUtil = "avutil";

    private const int IoBufferSize = 64 * 1024;
    private const int MediaTypeAudio = 1;

    // libavutil/samplefmt.h: AV_SAMPLE_FMT_FLT and AV_SAMPLE_FMT_FLTP.
    private const int SampleFormatFloat = 3;
    private const int SampleFormatFloatPlanar = 8;

    // FFERRTAG('E','O','F',' '), and AVERROR(EIO).
    private const int ErrorEof = -('E' | ('O' << 8) | ('F' << 16) | (' ' << 24));
    private const int ErrorIo = -5;
    private const int SeekSize = 0x10000;
    private const int SeekForce = 0x20000;
    private const int SeekFlagBackward = 1;

    // libavutil/log.h: AV_LOG_QUIET.
    private const int LogQuiet = -8;

    private static readonly int ErrorAgain = OperatingSystem.IsMacOS() ? -35 : -11;

    private static readonly Lazy<string?> Problem = new(Load);

    /// <summary>Why FFmpeg can't be used, or <see langword="null"/> when it can.</summary>
    public static string? UnavailableReason => Problem.Value;

    /// <summary>What one decode of a file did.</summary>
    /// <param name="Frames">The samples per channel FFmpeg gave, before any trim a container asks for.</param>
    /// <param name="SampleFormat">The format of the frames it gave, as <c>AVSampleFormat</c>.</param>
    public readonly record struct Result(long Frames, int SampleFormat);

    /// <summary>Opens <paramref name="stream"/>, which the decoder owns from here on, at its best audio stream.</summary>
    /// <exception cref="InvalidOperationException">FFmpeg isn't available.</exception>
    /// <exception cref="InvalidDataException">FFmpeg can't decode it.</exception>
    public static Decoder Open(Stream stream)
    {
        if (Problem.Value is { } problem)
        {
            stream.Dispose();
            throw new InvalidOperationException(problem);
        }

        return new Decoder(stream);
    }

    private static string? Load()
    {
        // The app's own check of the versions; it loads the libraries this binds too.
        if (!FfmpegLibrary.IsAvailable)
            return FfmpegLibrary.UnavailableReason;

        NativeLibrary.SetDllImportResolver(typeof(FfmpegReference).Assembly, Resolve);

        // Its warnings about timestamps after a seek would bury the results.
        av_log_set_level(LogQuiet);
        return null;
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        string? fileName = libraryName switch
        {
            AvUtil => "avutil-61.dll",
            AvCodec => "avcodec-63.dll",
            AvFormat => "avformat-63.dll",
            _ => null,
        };
        if (fileName is null || !OperatingSystem.IsWindows())
            return IntPtr.Zero;

        return NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, fileName), out IntPtr handle) ? handle : IntPtr.Zero;
    }

    /// <summary>One file open in FFmpeg, decoded from its start by each <see cref="Run"/>.</summary>
    internal sealed class Decoder : IDisposable
    {
        private readonly Stream _stream;
        private GCHandle _streamHandle;
        private AVIOContext* _io;
        private AVFormatContext* _format;
        private void* _codec;
        private AVPacket* _packet;
        private AVFrame* _frame;
        private readonly int _streamIndex;
        private readonly long _startTime;
        private float[] _scratch = [];

        public Decoder(Stream stream)
        {
            _stream = stream;
            try
            {
                _streamHandle = GCHandle.Alloc(stream);
                byte* buffer = (byte*)av_malloc(IoBufferSize);
                if (buffer == null)
                    throw new OutOfMemoryException();

                _io = avio_alloc_context(buffer, IoBufferSize, 0, (void*)GCHandle.ToIntPtr(_streamHandle), &Read, null, &Seek);
                if (_io == null)
                {
                    av_freep(&buffer);
                    throw new OutOfMemoryException();
                }

                _format = avformat_alloc_context();
                if (_format == null)
                    throw new OutOfMemoryException();
                _format->pb = _io;

                AVFormatContext* format = _format;
                int result = avformat_open_input(&format, null, null, null);
                _format = format;
                Check(result, "avformat_open_input");
                Check(avformat_find_stream_info(_format, null), "avformat_find_stream_info");

                void* codec = null;
                _streamIndex = av_find_best_stream(_format, MediaTypeAudio, -1, -1, &codec, 0);
                Check(_streamIndex, "av_find_best_stream");

                // AVCodec's first field is its name.
                Name = Marshal.PtrToStringUTF8((IntPtr)(*(byte**)codec)) ?? "?";
                long start = _format->streams[_streamIndex]->start_time;
                _startTime = start == long.MinValue ? 0 : start;

                _codec = avcodec_alloc_context3(codec);
                if (_codec == null)
                    throw new OutOfMemoryException();
                Check(avcodec_parameters_to_context(_codec, _format->streams[_streamIndex]->codecpar), "avcodec_parameters_to_context");

                // The app reads chunks side by side itself, so each decoder gets one thread.
                void* options = null;
                av_dict_set(&options, "threads", "1", 0);
                result = avcodec_open2(_codec, codec, &options);
                av_dict_free(&options);
                Check(result, "avcodec_open2");

                _packet = av_packet_alloc();
                _frame = av_frame_alloc();
                if (_packet == null || _frame == null)
                    throw new OutOfMemoryException();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>FFmpeg's name for the decoder it chose.</summary>
        public string Name { get; } = "?";

        /// <summary>
        /// Decodes from the start to the end, interleaving each frame into a scratch buffer as a source would
        /// hand it out: the work a real source adds.
        /// </summary>
        public Result Run(int channels)
        {
            if (av_seek_frame(_format, _streamIndex, _startTime, SeekFlagBackward) < 0)
                throw new InvalidDataException("av_seek_frame couldn't go back to the start.");
            avcodec_flush_buffers(_codec);

            long frames = 0;
            int sampleFormat = -1;
            bool draining = false;

            while (true)
            {
                int received = avcodec_receive_frame(_codec, _frame);
                if (received == 0)
                {
                    int count = _frame->nb_samples;
                    sampleFormat = _frame->format;
                    if (_scratch.Length < count * channels)
                        _scratch = new float[count * channels];
                    Interleave(_frame, count, channels, _scratch);
                    frames += count;
                    av_frame_unref(_frame);
                    continue;
                }

                if (received == ErrorEof || (received == ErrorAgain && draining))
                    break;
                if (received != ErrorAgain)
                    throw new InvalidDataException($"avcodec_receive_frame failed (FFmpeg error {received}).");

                int read = av_read_frame(_format, _packet);
                if (read < 0)
                {
                    draining = true;
                    avcodec_send_packet(_codec, null);
                    continue;
                }

                if (_packet->stream_index == _streamIndex)
                    avcodec_send_packet(_codec, _packet);
                av_packet_unref(_packet);
            }

            return new Result(frames, sampleFormat);
        }

        public void Dispose()
        {
            if (_frame != null)
            {
                AVFrame* frame = _frame;
                av_frame_free(&frame);
                _frame = null;
            }

            if (_packet != null)
            {
                AVPacket* packet = _packet;
                av_packet_free(&packet);
                _packet = null;
            }

            if (_codec != null)
            {
                void* codec = _codec;
                avcodec_free_context(&codec);
                _codec = null;
            }

            if (_format != null)
            {
                AVFormatContext* format = _format;
                avformat_close_input(&format);
                _format = null;
            }

            if (_io != null)
            {
                av_freep(&_io->buffer);
                AVIOContext* io = _io;
                avio_context_free(&io);
                _io = null;
            }

            if (_streamHandle.IsAllocated)
                _streamHandle.Free();
            _stream.Dispose();
        }

        private static void Interleave(AVFrame* frame, int count, int channels, float[] output)
        {
            if (frame->format == SampleFormatFloatPlanar)
            {
                for (int c = 0; c < channels; c++)
                {
                    float* plane = (float*)frame->extended_data[c];
                    for (int i = 0; i < count; i++)
                        output[(i * channels) + c] = plane[i];
                }
            }
            else if (frame->format == SampleFormatFloat)
            {
                new ReadOnlySpan<float>(frame->extended_data[0], count * channels).CopyTo(output);
            }
        }

        private static void Check(int result, string function)
        {
            if (result < 0)
                throw new InvalidDataException($"{function} failed (FFmpeg error {result}).");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Read(void* opaque, byte* buffer, int size)
    {
        try
        {
            var stream = (Stream)GCHandle.FromIntPtr((IntPtr)opaque).Target!;
            int read = stream.Read(new Span<byte>(buffer, size));
            return read == 0 ? ErrorEof : read;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return ErrorIo;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static long Seek(void* opaque, long offset, int whence)
    {
        try
        {
            var stream = (Stream)GCHandle.FromIntPtr((IntPtr)opaque).Target!;
            if ((whence & SeekSize) != 0)
                return stream.Length;

            var origin = (whence & ~SeekForce) switch
            {
                0 => SeekOrigin.Begin,
                1 => SeekOrigin.Current,
                2 => SeekOrigin.End,
                _ => (SeekOrigin?)null,
            };
            return origin is { } o ? stream.Seek(offset, o) : ErrorIo;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException or ArgumentException)
        {
            return ErrorIo;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AVFormatContext
    {
        public void* av_class;
        public void* iformat;
        public void* oformat;
        public void* priv_data;
        public AVIOContext* pb;
        public int ctx_flags;
        public uint nb_streams;
        public AVStream** streams;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AVStream
    {
        public void* av_class;
        public int index;
        public int id;
        public void* codecpar;
        public void* priv_data;
        public int time_base_num;
        public int time_base_den;
        public long start_time;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AVIOContext
    {
        public void* av_class;
        public byte* buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AVPacket
    {
        public void* buf;
        public long pts;
        public long dts;
        public byte* data;
        public int size;
        public int stream_index;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AVFrame
    {
        public fixed ulong data[8];
        public fixed int linesize[8];
        public byte** extended_data;
        public int width;
        public int height;
        public int nb_samples;
        public int format;
    }

    [LibraryImport(AvUtil, EntryPoint = "av_log_set_level")]
    private static partial void av_log_set_level(int level);

    [LibraryImport(AvUtil, EntryPoint = "av_malloc")]
    private static partial void* av_malloc(nuint size);

    [LibraryImport(AvUtil, EntryPoint = "av_freep")]
    private static partial void av_freep(void* ptr);

    [LibraryImport(AvUtil, EntryPoint = "av_frame_alloc")]
    private static partial AVFrame* av_frame_alloc();

    [LibraryImport(AvUtil, EntryPoint = "av_frame_free")]
    private static partial void av_frame_free(AVFrame** frame);

    [LibraryImport(AvUtil, EntryPoint = "av_frame_unref")]
    private static partial void av_frame_unref(AVFrame* frame);

    [LibraryImport(AvUtil, EntryPoint = "av_dict_set", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int av_dict_set(void** dictionary, string key, string value, int flags);

    [LibraryImport(AvUtil, EntryPoint = "av_dict_free")]
    private static partial void av_dict_free(void** dictionary);

    [LibraryImport(AvFormat, EntryPoint = "avformat_alloc_context")]
    private static partial AVFormatContext* avformat_alloc_context();

    [LibraryImport(AvFormat, EntryPoint = "avformat_open_input")]
    private static partial int avformat_open_input(AVFormatContext** context, byte* url, void* format, void** options);

    [LibraryImport(AvFormat, EntryPoint = "avformat_find_stream_info")]
    private static partial int avformat_find_stream_info(AVFormatContext* context, void** options);

    [LibraryImport(AvFormat, EntryPoint = "av_find_best_stream")]
    private static partial int av_find_best_stream(AVFormatContext* context, int type, int wanted, int related, void** decoder, int flags);

    [LibraryImport(AvFormat, EntryPoint = "av_read_frame")]
    private static partial int av_read_frame(AVFormatContext* context, AVPacket* packet);

    [LibraryImport(AvFormat, EntryPoint = "av_seek_frame")]
    private static partial int av_seek_frame(AVFormatContext* context, int streamIndex, long timestamp, int flags);

    [LibraryImport(AvFormat, EntryPoint = "avformat_close_input")]
    private static partial void avformat_close_input(AVFormatContext** context);

    [LibraryImport(AvFormat, EntryPoint = "avio_alloc_context")]
    private static partial AVIOContext* avio_alloc_context(
        byte* buffer, int bufferSize, int writeFlag, void* opaque,
        delegate* unmanaged[Cdecl]<void*, byte*, int, int> readPacket,
        void* writePacket,
        delegate* unmanaged[Cdecl]<void*, long, int, long> seek);

    [LibraryImport(AvFormat, EntryPoint = "avio_context_free")]
    private static partial void avio_context_free(AVIOContext** context);

    [LibraryImport(AvCodec, EntryPoint = "avcodec_alloc_context3")]
    private static partial void* avcodec_alloc_context3(void* codec);

    [LibraryImport(AvCodec, EntryPoint = "avcodec_parameters_to_context")]
    private static partial int avcodec_parameters_to_context(void* codecContext, void* parameters);

    [LibraryImport(AvCodec, EntryPoint = "avcodec_open2")]
    private static partial int avcodec_open2(void* codecContext, void* codec, void** options);

    [LibraryImport(AvCodec, EntryPoint = "avcodec_send_packet")]
    private static partial int avcodec_send_packet(void* codecContext, AVPacket* packet);

    [LibraryImport(AvCodec, EntryPoint = "avcodec_receive_frame")]
    private static partial int avcodec_receive_frame(void* codecContext, AVFrame* frame);

    [LibraryImport(AvCodec, EntryPoint = "avcodec_flush_buffers")]
    private static partial void avcodec_flush_buffers(void* codecContext);

    [LibraryImport(AvCodec, EntryPoint = "avcodec_free_context")]
    private static partial void avcodec_free_context(void** codecContext);

    [LibraryImport(AvCodec, EntryPoint = "av_packet_alloc")]
    private static partial AVPacket* av_packet_alloc();

    [LibraryImport(AvCodec, EntryPoint = "av_packet_free")]
    private static partial void av_packet_free(AVPacket** packet);

    [LibraryImport(AvCodec, EntryPoint = "av_packet_unref")]
    private static partial void av_packet_unref(AVPacket* packet);
}
