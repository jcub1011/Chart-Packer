using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ChartPacker.Video.Interop;

namespace ChartPacker.Video;

/// <summary>
/// Decodes the video stream of a file read through a <see cref="Stream"/>, one frame at a time,
/// and converts frames to BGRA at a size that fits a width. Audio and every other stream are
/// ignored. Not thread-safe: one thread uses it at a time.
/// </summary>
/// <remarks>
/// Two frames are held: the one just decoded by <see cref="Next"/>, and the one kept by
/// <see cref="Keep"/> to be shown. A player can decode ahead to find the last frame due without
/// converting every frame it passes.
/// </remarks>
public sealed unsafe class VideoDecoder : IDisposable
{
    private const int IoBufferSize = 64 * 1024;

    private readonly Stream _stream;
    private GCHandle _streamHandle;
    private AVIOContext* _io;
    private AVFormatContext* _format;
    private void* _codec;
    private AVPacket* _packet;
    private AVFrame* _next;
    private AVFrame* _kept;
    private void* _scaler;
    private (int Width, int Height, int Format) _scalerSource;
    private readonly int _streamIndex;
    private readonly AVRational _timeBase;
    private readonly long _startTime;
    private bool _draining;
    private bool _hasNext;
    private TimeSpan _lastTime;

    private VideoDecoder(Stream stream, int maxWidth)
    {
        _stream = stream;
        try
        {
            _streamHandle = GCHandle.Alloc(stream);
            byte* buffer = (byte*)Ffmpeg.av_malloc(IoBufferSize);
            if (buffer == null)
                throw new OutOfMemoryException();

            delegate* unmanaged[Cdecl]<void*, long, int, long> seek = stream.CanSeek ? &Seek : null;
            _io = Ffmpeg.avio_alloc_context(buffer, IoBufferSize, 0, (void*)GCHandle.ToIntPtr(_streamHandle), &Read, null, seek);
            if (_io == null)
            {
                Ffmpeg.av_freep(&buffer);
                throw new OutOfMemoryException();
            }

            _format = Ffmpeg.avformat_alloc_context();
            if (_format == null)
                throw new OutOfMemoryException();
            _format->pb = _io;

            AVFormatContext* format = _format;
            int result = Ffmpeg.avformat_open_input(&format, null, null, null);

            // On failure the context is freed and set to null; the custom I/O is still ours.
            _format = format;
            Check(result, "The file isn't a video FFmpeg can read");
            Check(Ffmpeg.avformat_find_stream_info(_format, null), "The video's streams couldn't be read");

            void* decoder = null;
            _streamIndex = Ffmpeg.av_find_best_stream(_format, Ffmpeg.MediaTypeVideo, -1, -1, &decoder, 0);
            Check(_streamIndex, "The file has no video stream that can be decoded");

            AVStream* videoStream = _format->streams[_streamIndex];
            _timeBase = videoStream->time_base;
            _startTime = videoStream->start_time == Ffmpeg.NoPtsValue ? 0 : videoStream->start_time;

            _codec = Ffmpeg.avcodec_alloc_context3(decoder);
            if (_codec == null)
                throw new OutOfMemoryException();
            Check(Ffmpeg.avcodec_parameters_to_context(_codec, videoStream->codecpar), "The video's codec can't be set up");

            void* options = null;
            Ffmpeg.av_dict_set(&options, "threads", "auto", 0);
            result = Ffmpeg.avcodec_open2(_codec, decoder, &options);
            Ffmpeg.av_dict_free(&options);
            Check(result, "The video's codec can't be opened");

            _packet = Ffmpeg.av_packet_alloc();
            _next = Ffmpeg.av_frame_alloc();
            _kept = Ffmpeg.av_frame_alloc();
            if (_packet == null || _next == null || _kept == null)
                throw new OutOfMemoryException();

            var parameters = videoStream->codecpar;
            (Width, Height) = FitWidth(parameters->width, parameters->height, maxWidth);
            if (Width <= 0 || Height <= 0)
                throw new InvalidDataException("The video has no picture size.");

            Duration = DurationOf(videoStream);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>The width frames are converted to.</summary>
    public int Width { get; }

    /// <summary>The height frames are converted to.</summary>
    public int Height { get; }

    /// <summary>The video's length, or zero when the file doesn't say.</summary>
    public TimeSpan Duration { get; }

    /// <summary>Whether a frame has been kept with <see cref="Keep"/> since opening or the last <see cref="Seek"/>.</summary>
    public bool HasKept { get; private set; }

    /// <summary>
    /// Opens <paramref name="stream"/>, which must be readable and should be seekable. The decoder
    /// owns it from here on: disposing the decoder disposes it, and so does a failed open.
    /// </summary>
    /// <param name="maxWidth">Frames wider than this are scaled down to it, keeping their shape.</param>
    /// <exception cref="InvalidOperationException">FFmpeg isn't available; see <see cref="FfmpegLibrary"/>.</exception>
    /// <exception cref="InvalidDataException">The stream isn't a video that can be decoded.</exception>
    public static VideoDecoder Open(Stream stream, int maxWidth)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWidth, 1);
        try
        {
            FfmpegLibrary.EnsureAvailable();
            return new VideoDecoder(stream, maxWidth);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens the file at <paramref name="path"/> and decodes its first frame, to tell whether it's a
    /// video that can be shown.
    /// </summary>
    /// <exception cref="InvalidOperationException">FFmpeg isn't available.</exception>
    /// <exception cref="InvalidDataException">It isn't a video that can be decoded.</exception>
    /// <exception cref="IOException">The file can't be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    public static void Check(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var decoder = Open(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, IoBufferSize), 16);
        if (!decoder.Next(out _))
            throw new InvalidDataException("The video has no frames that can be decoded.");
    }

    /// <summary>
    /// Decodes the next frame, replacing the last one decoded unless it was kept.
    /// </summary>
    /// <param name="time">When the frame is shown, from the start of the video.</param>
    /// <returns><see langword="false"/> at the end of the video.</returns>
    /// <exception cref="InvalidDataException">The video can't be decoded any further.</exception>
    public bool Next(out TimeSpan time)
    {
        ObjectDisposedException.ThrowIf(_codec == null, this);
        Ffmpeg.av_frame_unref(_next);
        _hasNext = false;

        while (true)
        {
            int received = Ffmpeg.avcodec_receive_frame(_codec, _next);
            if (received == 0)
            {
                long timestamp = _next->best_effort_timestamp != Ffmpeg.NoPtsValue ? _next->best_effort_timestamp : _next->pts;
                time = timestamp == Ffmpeg.NoPtsValue ? _lastTime : ToTime(timestamp);
                _lastTime = time;
                _hasNext = true;
                return true;
            }

            if (received == Ffmpeg.ErrorEof)
            {
                time = _lastTime;
                return false;
            }

            if (received != Ffmpeg.ErrorAgain)
                throw new InvalidDataException($"The video can't be decoded (FFmpeg error {received}).");

            if (_draining)
            {
                time = _lastTime;
                return false;
            }

            int read = Ffmpeg.av_read_frame(_format, _packet);
            if (read < 0)
            {
                // The end, or a read error: whatever the decoder still holds is let out.
                _draining = true;
                Ffmpeg.avcodec_send_packet(_codec, null);
                continue;
            }

            if (_packet->stream_index == _streamIndex)
            {
                // A damaged packet is skipped; the decoder carries on from the next one.
                Ffmpeg.avcodec_send_packet(_codec, _packet);
            }

            Ffmpeg.av_packet_unref(_packet);
        }
    }

    /// <summary>Keeps the frame <see cref="Next"/> just decoded, in place of the one kept before.</summary>
    public void Keep()
    {
        if (!_hasNext)
            return;

        AVFrame* kept = _kept;
        _kept = _next;
        _next = kept;
        Ffmpeg.av_frame_unref(_next);
        _hasNext = false;
        HasKept = true;
    }

    /// <summary>Converts the kept frame to BGRA, <see cref="Width"/> × <see cref="Height"/>, rows packed.</summary>
    /// <returns><see langword="false"/> when no frame is kept, or it couldn't be converted.</returns>
    public bool CopyKept(Span<byte> bgra)
    {
        ObjectDisposedException.ThrowIf(_codec == null, this);
        if (bgra.Length < Width * Height * 4)
            throw new ArgumentException("The buffer is too small for a frame.", nameof(bgra));
        if (!HasKept)
            return false;

        (int Width, int Height, int Format) source = (_kept->width, _kept->height, _kept->format);
        if (source.Width <= 0 || source.Height <= 0)
            return false;

        if (_scaler == null || _scalerSource != source)
        {
            Ffmpeg.sws_freeContext(_scaler);
            _scaler = Ffmpeg.sws_getContext(source.Width, source.Height, source.Format, Width, Height, Ffmpeg.PixelFormatBgra, Ffmpeg.SwsBilinear, null, null, null);
            _scalerSource = source;
            if (_scaler == null)
                return false;
        }

        fixed (byte* output = bgra)
        {
            byte** dst = stackalloc byte*[4] { output, null, null, null };
            int* dstStride = stackalloc int[4] { Width * 4, 0, 0, 0 };
            return Ffmpeg.sws_scale(_scaler, (byte**)_kept->data, _kept->linesize, 0, source.Height, dst, dstStride) > 0;
        }
    }

    /// <summary>
    /// Moves to the last keyframe at or before <paramref name="time"/>, forgetting both held frames.
    /// <see cref="Next"/> then decodes from there.
    /// </summary>
    public void Seek(TimeSpan time)
    {
        ObjectDisposedException.ThrowIf(_codec == null, this);

        long target = _startTime + (long)(time.TotalSeconds * _timeBase.den / _timeBase.num);
        if (Ffmpeg.av_seek_frame(_format, _streamIndex, target, Ffmpeg.SeekFlagBackward) < 0)
            Ffmpeg.av_seek_frame(_format, _streamIndex, _startTime, Ffmpeg.SeekFlagBackward);

        Ffmpeg.avcodec_flush_buffers(_codec);
        Ffmpeg.av_frame_unref(_next);
        Ffmpeg.av_frame_unref(_kept);
        _hasNext = false;
        HasKept = false;
        _draining = false;
        _lastTime = time;
    }

    public void Dispose()
    {
        if (_scaler != null)
        {
            Ffmpeg.sws_freeContext(_scaler);
            _scaler = null;
        }

        if (_next != null)
        {
            AVFrame* frame = _next;
            Ffmpeg.av_frame_free(&frame);
            _next = null;
        }

        if (_kept != null)
        {
            AVFrame* frame = _kept;
            Ffmpeg.av_frame_free(&frame);
            _kept = null;
        }

        if (_packet != null)
        {
            AVPacket* packet = _packet;
            Ffmpeg.av_packet_free(&packet);
            _packet = null;
        }

        if (_codec != null)
        {
            void* codec = _codec;
            Ffmpeg.avcodec_free_context(&codec);
            _codec = null;
        }

        if (_format != null)
        {
            AVFormatContext* format = _format;
            Ffmpeg.avformat_close_input(&format);
            _format = null;
        }

        // The custom I/O isn't closed with the input; its buffer may have been replaced since.
        if (_io != null)
        {
            Ffmpeg.av_freep(&_io->buffer);
            AVIOContext* io = _io;
            Ffmpeg.avio_context_free(&io);
            _io = null;
        }

        if (_streamHandle.IsAllocated)
            _streamHandle.Free();
        _stream.Dispose();
    }

    private TimeSpan ToTime(long timestamp)
    {
        if (_timeBase.den == 0)
            return TimeSpan.Zero;

        double seconds = (double)(timestamp - _startTime) * _timeBase.num / _timeBase.den;
        return seconds <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(seconds);
    }

    private TimeSpan DurationOf(AVStream* stream)
    {
        if (stream->duration > 0 && stream->duration != Ffmpeg.NoPtsValue && _timeBase.den != 0)
            return TimeSpan.FromSeconds((double)stream->duration * _timeBase.num / _timeBase.den);

        long duration = _format->duration;
        return duration > 0 && duration != Ffmpeg.NoPtsValue ? TimeSpan.FromSeconds((double)duration / Ffmpeg.TimeBase) : TimeSpan.Zero;
    }

    private static (int Width, int Height) FitWidth(int width, int height, int maxWidth)
    {
        if (width <= maxWidth)
            return (width, height);

        return (maxWidth, Math.Max(1, (int)Math.Round((double)height * maxWidth / width)));
    }

    private static void Check(int result, string message)
    {
        if (result < 0)
            throw new InvalidDataException($"{message} (FFmpeg error {result}).");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Read(void* opaque, byte* buffer, int size)
    {
        try
        {
            var stream = (Stream)GCHandle.FromIntPtr((IntPtr)opaque).Target!;
            int read = stream.Read(new Span<byte>(buffer, size));
            return read == 0 ? Ffmpeg.ErrorEof : read;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            return Ffmpeg.ErrorIo;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static long Seek(void* opaque, long offset, int whence)
    {
        try
        {
            var stream = (Stream)GCHandle.FromIntPtr((IntPtr)opaque).Target!;
            if ((whence & Ffmpeg.SeekSize) != 0)
                return stream.Length;

            var origin = (whence & ~Ffmpeg.SeekForce) switch
            {
                0 => SeekOrigin.Begin,
                1 => SeekOrigin.Current,
                2 => SeekOrigin.End,
                _ => (SeekOrigin?)null,
            };
            return origin is { } o ? stream.Seek(offset, o) : Ffmpeg.ErrorIo;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException or ArgumentException)
        {
            return Ffmpeg.ErrorIo;
        }
    }
}
