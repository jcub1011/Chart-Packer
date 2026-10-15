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
    // from the frame itself. Both are set up by the first CopyKept, so a decoder only opened to be checked or probed never
    // pays for them.
    private void* _scaler;
    private bool _scalerFailed;
    private bool _scaleFailureLogged;

    // What swscale converts into: _converted, a frame over _convertedBuffer, FFmpeg memory of our own that's allocated once,
    // rows RowAlignment apart, with TailPadding after the last. swscale's vectorised converters write whole blocks of
    // pixels, so a row whose width isn't a multiple of the block runs past its end: a 1176-wide video was written 32 bytes
    // past every row, the last included. Converted straight into the caller's managed array, as frames once were, the last
    // row's overrun landed on the .NET heap past it, which surfaced later as an ExecutionEngineException in whatever
    // allocated or collected next. Such writes now land here, on Canary bytes, and are logged; CopyKept copies the rows out
    // packed. The frame has no buffer reference, so freeing it leaves the buffer, which Dispose frees itself.
    private const int RowAlignment = 128;
    private const int TailPadding = 4096;
    private const byte Canary = 0xA5;
    private AVFrame* _converted;
    private byte* _convertedBuffer;
    private int _convertedStride;
    private bool _overrunLogged;

    // The colour the first converted frame was tagged with, which every later one is converted as. swscale rebuilds its
    // whole conversion when a frame's tags differ from the last one's, and some streams flip them between frames; a stream
    // that really does change colour partway keeps the first frame's.
    private (int Range, int Space, int Primaries, int Transfer, int ChromaLocation)? _colour;
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

        // A frame without a picture of its own: swscale returns as if it had converted one, leaving the buffer as it was.
        if (_kept->width <= 0 || _kept->height <= 0 || _kept->data[0] == 0)
            return false;

        if (!EnsureScaler())
            return false;

        Normalise(_kept);
        int rowBytes = Width * 4;
        if (!_overrunLogged)
            FillPadding(rowBytes);

        // Given a picture, swscale converts into it rather than allocating one of its own.
        _converted->data[0] = (ulong)_convertedBuffer;
        _converted->linesize[0] = _convertedStride;
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

        if (!_overrunLogged)
            CheckPadding(rowBytes);
        for (int y = 0; y < Height; y++)
            new ReadOnlySpan<byte>(_convertedBuffer + ((long)y * _convertedStride), rowBytes).CopyTo(bgra.Slice(y * rowBytes, rowBytes));
        return true;
    }

    // Sets up the scaler and what it converts into, the first time a frame is converted. False when the scaler can't be
    // set up, which is logged once; no frame is converted after, but the video still decodes, as nothing else needs it.
    private bool EnsureScaler()
    {
        if (_scaler != null)
            return true;
        if (_scalerFailed)
            return false;

        if (_converted == null)
        {
            _converted = Native.av_frame_alloc();
            if (_converted == null)
                throw new OutOfMemoryException();
        }

        if (_convertedBuffer == null)
        {
            _convertedStride = ((Width * 4) + RowAlignment - 1) / RowAlignment * RowAlignment;
            _convertedBuffer = (byte*)Native.av_malloc((nuint)(((long)_convertedStride * Height) + TailPadding));
            if (_convertedBuffer == null)
                throw new OutOfMemoryException();
        }

        void* scaler = Native.sws_alloc_context();
        if (scaler == null)
            throw new OutOfMemoryException();

        int result = Native.av_opt_set_int(scaler, "sws_flags", Native.SwsBilinear, 0);
        if (result < 0)
        {
            Native.sws_free_context(&scaler);
            _scalerFailed = true;
            Logger.Warning("The video's scaler couldn't be set up (FFmpeg error {Error}), so its frames can't be shown", result);
            return false;
        }

        _scaler = scaler;
        return true;
    }

    // Readies the kept frame, used for nothing but converting, to be converted the way the frames before it were, so
    // swscale keeps the conversion it set up rather than rebuilding it.
    private void Normalise(AVFrame* frame)
    {
        // Converted whole, as one picture, as frames were before the frame API: it won't convert an interlaced frame into
        // a progressive one, and fields scaled apart look no better at the sizes shown.
        frame->flags &= ~Native.FrameFlagInterlaced;

        // HDR10+ metadata is part of what swscale compares, and it changes from frame to frame.
        Native.av_frame_remove_side_data(frame, Native.FrameDataDynamicHdrPlus);

        _colour ??= (frame->color_range, frame->colorspace, frame->color_primaries, frame->color_trc, frame->chroma_location);
        (frame->color_range, frame->colorspace, frame->color_primaries, frame->color_trc, frame->chroma_location) = _colour.Value;
    }

    // Marks the bytes past each row and past the last row, so CheckPadding can tell whether swscale wrote there.
    private void FillPadding(int rowBytes)
    {
        int slack = _convertedStride - rowBytes;
        if (slack > 0)
        {
            for (int y = 0; y < Height; y++)
                new Span<byte>(_convertedBuffer + ((long)y * _convertedStride) + rowBytes, slack).Fill(Canary);
        }

        new Span<byte>(_convertedBuffer + ((long)_convertedStride * Height), TailPadding).Fill(Canary);
    }

    private void CheckPadding(int rowBytes)
    {
        // How far past its end each row was written: into the slack before the next row, and for the last row on into the
        // tail too, as it's all one run of padding.
        int slack = _convertedStride - rowBytes;
        int rowsOverrun = 0, furthest = 0, lastRow = 0;
        for (int y = 0; y < Height; y++)
        {
            int run = y == Height - 1 ? slack + TailPadding : slack;
            int past = new ReadOnlySpan<byte>(_convertedBuffer + ((long)y * _convertedStride) + rowBytes, run).LastIndexOfAnyExcept(Canary) + 1;
            if (past == 0)
                continue;
            rowsOverrun++;
            furthest = Math.Max(furthest, past);
            if (y == Height - 1)
                lastRow = past;
        }

        if (rowsOverrun == 0)
            return;

        // Once per video: it happens on every frame, so neither the marking nor the check is done again.
        _overrunLogged = true;
        if (lastRow == slack + TailPadding)
        {
            // Written up to the buffer's last byte, so perhaps past it too, onto FFmpeg's heap.
            Logger.Warning(
                "swscale wrote to the end of the padding past the rows it was given, and perhaps beyond, converting " +
                "{SourceWidth}×{SourceHeight} (pixel format {SourceFormat}) to {Width}×{Height}",
                _kept->width, _kept->height, _kept->format, Width, Height);
            return;
        }

        Logger.Debug(
            "swscale wrote past the rows it was given, converting {SourceWidth}×{SourceHeight} (pixel format {SourceFormat}) " +
            "to {Width}×{Height}: {Rows} row(s), up to {Bytes} byte(s) past the end, the last row {LastRowBytes}. " +
            "It landed in padding, so nothing was harmed",
            _kept->width, _kept->height, _kept->format, Width, Height, rowsOverrun, furthest, lastRow);
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

        if (_convertedBuffer != null)
        {
            byte* buffer = _convertedBuffer;
            Native.av_freep(&buffer);
            _convertedBuffer = null;
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
