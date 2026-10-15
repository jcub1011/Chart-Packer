using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ChartPacker.Ffmpeg;
using ChartPacker.Ffmpeg.Interop;
using Serilog;

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

    private static ILogger Logger => Log.ForContext<VideoDecoder>();

    private readonly Stream _stream;
    private GCHandle _streamHandle;
    private AVIOContext* _io;
    private AVFormatContext* _format;
    private void* _codec;
    private AVPacket* _packet;
    private AVFrame* _next;
    private AVFrame* _kept;
    // swscale's frame API: the scaler takes the kept frame's size, format and colour (BT.709 or 601, limited or full range)
    // from the frame itself, and fills _converted, a frame whose buffer it allocates. That buffer has room past each row,
    // which its vectorised converters need: they write whole blocks of pixels, past the end of a row whose width isn't a
    // multiple of the block (a 1176-wide video, 32 bytes past every row). CopyKept copies the rows out from there, packed.
    // Converted straight into the caller's managed array, as frames once were, the last row's overrun landed on the .NET
    // heap past it, which surfaced later as an ExecutionEngineException in whatever allocated or collected next.
    private void* _scaler;
    private AVFrame* _converted;
    private bool _scaleFailureLogged;
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
            byte* buffer = (byte*)Native.av_malloc(IoBufferSize);
            if (buffer == null)
                throw new OutOfMemoryException();

            delegate* unmanaged[Cdecl]<void*, long, int, long> seek = stream.CanSeek ? &Seek : null;
            _io = Native.avio_alloc_context(buffer, IoBufferSize, 0, (void*)GCHandle.ToIntPtr(_streamHandle), &Read, null, seek);
            if (_io == null)
            {
                Native.av_freep(&buffer);
                throw new OutOfMemoryException();
            }

            _format = Native.avformat_alloc_context();
            if (_format == null)
                throw new OutOfMemoryException();
            _format->pb = _io;

            AVFormatContext* format = _format;
            int result = Native.avformat_open_input(&format, null, null, null);

            // On failure the context is freed and set to null; the custom I/O is still ours.
            _format = format;
            Check(result, "The file isn't a video FFmpeg can read");
            Check(Native.avformat_find_stream_info(_format, null), "The video's streams couldn't be read");

            void* decoder = null;
            _streamIndex = Native.av_find_best_stream(_format, Native.MediaTypeVideo, -1, -1, &decoder, 0);
            Check(_streamIndex, "The file has no video stream that can be decoded");

            AVStream* videoStream = _format->streams[_streamIndex];
            _timeBase = videoStream->time_base;
            _startTime = videoStream->start_time == Native.NoPtsValue ? 0 : videoStream->start_time;

            _codec = Native.avcodec_alloc_context3(decoder);
            if (_codec == null)
                throw new OutOfMemoryException();
            Check(Native.avcodec_parameters_to_context(_codec, videoStream->codecpar), "The video's codec can't be set up");

            void* options = null;
            Native.av_dict_set(&options, "threads", "auto", 0);
            result = Native.avcodec_open2(_codec, decoder, &options);
            Native.av_dict_free(&options);
            Check(result, "The video's codec can't be opened");

            _packet = Native.av_packet_alloc();
            _next = Native.av_frame_alloc();
            _kept = Native.av_frame_alloc();
            _converted = Native.av_frame_alloc();
            _scaler = Native.sws_alloc_context();
            if (_packet == null || _next == null || _kept == null || _converted == null || _scaler == null)
                throw new OutOfMemoryException();
            Check(Native.av_opt_set_int(_scaler, "sws_flags", Native.SwsBilinear, 0), "The video's scaler can't be set up");

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
        Native.av_frame_unref(_next);
        _hasNext = false;

        while (true)
        {
            int received = Native.avcodec_receive_frame(_codec, _next);
            if (received == 0)
            {
                long timestamp = _next->best_effort_timestamp != Native.NoPtsValue ? _next->best_effort_timestamp : _next->pts;
                time = timestamp == Native.NoPtsValue ? _lastTime : ToTime(timestamp);
                _lastTime = time;
                _hasNext = true;
                return true;
            }

            if (received == Native.ErrorEof)
            {
                time = _lastTime;
                return false;
            }

            if (received != Native.ErrorAgain)
                throw new InvalidDataException($"The video can't be decoded (FFmpeg error {received}).");

            if (_draining)
            {
                time = _lastTime;
                return false;
            }

            int read = Native.av_read_frame(_format, _packet);
            if (read < 0)
            {
                // The end, or a read error: whatever the decoder still holds is let out.
                _draining = true;
                Native.avcodec_send_packet(_codec, null);
                continue;
            }

            if (_packet->stream_index == _streamIndex)
            {
                // A damaged packet is skipped; the decoder carries on from the next one.
                Native.avcodec_send_packet(_codec, _packet);
            }

            Native.av_packet_unref(_packet);
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
        Native.av_frame_unref(_next);
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

        if (_kept->width <= 0 || _kept->height <= 0)
            return false;

        // Emptied, so the scaler allocates the buffer (from a pool of its own), sized and padded as it needs.
        Native.av_frame_unref(_converted);
        _converted->width = Width;
        _converted->height = Height;
        _converted->format = Native.PixelFormatBgra;
        int result = Native.sws_scale_frame(_scaler, _converted, _kept);
        if (result < 0)
        {
            // Once per video: it would fail the same way on every frame.
            if (!_scaleFailureLogged)
            {
                _scaleFailureLogged = true;
                Logger.Warning(
                    "A {SourceWidth}×{SourceHeight} frame (pixel format {SourceFormat}) couldn't be converted to {Width}×{Height} (FFmpeg error {Error})",
                    _kept->width, _kept->height, _kept->format, Width, Height, result);
            }

            return false;
        }

        int rowBytes = Width * 4;
        byte* rows = (byte*)_converted->data[0];
        int stride = _converted->linesize[0];
        for (int y = 0; y < Height; y++)
            new ReadOnlySpan<byte>(rows + ((long)y * stride), rowBytes).CopyTo(bgra.Slice(y * rowBytes, rowBytes));
        return true;
    }

    /// <summary>
    /// Moves to the last keyframe at or before <paramref name="time"/>, forgetting both held frames.
    /// <see cref="Next"/> then decodes from there.
    /// </summary>
    public void Seek(TimeSpan time)
    {
        ObjectDisposedException.ThrowIf(_codec == null, this);

        long target = _startTime + (long)(time.TotalSeconds * _timeBase.den / _timeBase.num);
        if (Native.av_seek_frame(_format, _streamIndex, target, Native.SeekFlagBackward) < 0)
            Native.av_seek_frame(_format, _streamIndex, _startTime, Native.SeekFlagBackward);

        Native.avcodec_flush_buffers(_codec);
        Native.av_frame_unref(_next);
        Native.av_frame_unref(_kept);
        _hasNext = false;
        HasKept = false;
        _draining = false;
        _lastTime = time;
    }

    public void Dispose()
    {
        if (_scaler != null)
        {
            void* scaler = _scaler;
            Native.sws_free_context(&scaler);
            _scaler = null;
        }

        if (_converted != null)
        {
            AVFrame* converted = _converted;
            Native.av_frame_free(&converted);
            _converted = null;
        }

        if (_next != null)
        {
            AVFrame* frame = _next;
            Native.av_frame_free(&frame);
            _next = null;
        }

        if (_kept != null)
        {
            AVFrame* frame = _kept;
            Native.av_frame_free(&frame);
            _kept = null;
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

        if (_format != null)
        {
            AVFormatContext* format = _format;
            Native.avformat_close_input(&format);
            _format = null;
        }

        // The custom I/O isn't closed with the input; its buffer may have been replaced since.
        if (_io != null)
        {
            Native.av_freep(&_io->buffer);
            AVIOContext* io = _io;
            Native.avio_context_free(&io);
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
        if (stream->duration > 0 && stream->duration != Native.NoPtsValue && _timeBase.den != 0)
            return TimeSpan.FromSeconds((double)stream->duration * _timeBase.num / _timeBase.den);

        long duration = _format->duration;
        return duration > 0 && duration != Native.NoPtsValue ? TimeSpan.FromSeconds((double)duration / Native.TimeBase) : TimeSpan.Zero;
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

    // Read and Seek are called by FFmpeg, so no exception may leave them: it can't unwind through FFmpeg's frames, and the
    // runtime ends the process instead, with an ExecutionEngineException and nothing logged. Each catches everything and
    // tells FFmpeg the read failed, which it reports as a damaged or unreadable video. What isn't an expected I/O failure
    // is logged, as it's a bug in the stream.

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Read(void* opaque, byte* buffer, int size)
    {
        try
        {
            var stream = (Stream)GCHandle.FromIntPtr((IntPtr)opaque).Target!;
            int read = stream.Read(new Span<byte>(buffer, size));
            return read == 0 ? Native.ErrorEof : read;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            Logger.Debug(ex, "A video read failed");
            return Native.ErrorIo;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "A video read threw unexpectedly; FFmpeg is told the read failed");
            return Native.ErrorIo;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static long Seek(void* opaque, long offset, int whence)
    {
        try
        {
            var stream = (Stream)GCHandle.FromIntPtr((IntPtr)opaque).Target!;
            if ((whence & Native.SeekSize) != 0)
                return stream.Length;

            var origin = (whence & ~Native.SeekForce) switch
            {
                0 => SeekOrigin.Begin,
                1 => SeekOrigin.Current,
                2 => SeekOrigin.End,
                _ => (SeekOrigin?)null,
            };
            return origin is { } o ? stream.Seek(offset, o) : Native.ErrorIo;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException or ArgumentException)
        {
            Logger.Debug(ex, "A video seek to {Offset} ({Whence}) failed", offset, whence);
            return Native.ErrorIo;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "A video seek to {Offset} ({Whence}) threw unexpectedly; FFmpeg is told it failed", offset, whence);
            return Native.ErrorIo;
        }
    }
}
