using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ChartShared.Ffmpeg.Interop;
using Serilog;

namespace ChartShared.Ffmpeg;

/// <summary>
/// Opens a managed stream as an FFmpeg input format, with FFmpeg's own I/O callbacks over it. The stream is owned from
/// here on: disposing this closes the format and the stream.
/// </summary>
/// <remarks>
/// Shared by the video decoder and the video's audio reader, so the custom I/O and the careful error handling around it
/// only exist once. Not thread-safe: one thread uses it at a time.
/// </remarks>
internal sealed unsafe class FfmpegStreamInput : IDisposable
{
    private const int IoBufferSize = 64 * 1024;

    private static readonly ILogger Logger = Log.ForContext<FfmpegStreamInput>();

    private readonly Stream _stream;
    private GCHandle _streamHandle;
    private AVIOContext* _io;
    private AVFormatContext* _format;

    private FfmpegStreamInput(Stream stream)
    {
        _stream = stream;
        try
        {
            _streamHandle = GCHandle.Alloc(stream);
            byte* buffer = (byte*)Native.av_malloc(IoBufferSize);
            if (buffer == null)
                throw new OutOfMemoryException();

            delegate* unmanaged[Cdecl]<void*, byte*, int, int> read = &Read;
            delegate* unmanaged[Cdecl]<void*, long, int, long> seek = stream.CanSeek ? &Seek : null;
            _io = Native.avio_alloc_context(buffer, IoBufferSize, 0, (void*)GCHandle.ToIntPtr(_streamHandle), read, null, seek);
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
            Check(result, "The file isn't a media file FFmpeg can read");
            Check(Native.avformat_find_stream_info(_format, null), "The file's streams couldn't be read");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens <paramref name="stream"/>, which this takes ownership of: it's disposed even when this throws, as when FFmpeg
    /// isn't available.
    /// </summary>
    /// <exception cref="InvalidOperationException">FFmpeg isn't available.</exception>
    /// <exception cref="InvalidDataException">FFmpeg can't read the stream.</exception>
    public static FfmpegStreamInput Open(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            FfmpegLibrary.EnsureAvailable();
        }
        catch
        {
            // It's owned from the moment it's passed here, so it's closed even when FFmpeg isn't available. Once the
            // constructor starts, its own catch closes it.
            stream.Dispose();
            throw;
        }

        return new FfmpegStreamInput(stream);
    }

    public AVFormatContext* Format => _format;

    public void Dispose()
    {
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

    private static void Check(int result, string message)
    {
        if (result < 0)
            throw new InvalidDataException($"{message} (FFmpeg error {result}).");
    }

    // Read and Seek are called by FFmpeg, so no exception may leave them: it can't unwind through FFmpeg's frames, and the
    // runtime ends the process instead, with an ExecutionEngineException and nothing logged. Each catches everything and
    // tells FFmpeg the read failed, which it reports as a damaged or unreadable file. What isn't an expected I/O failure
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
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException or NotSupportedException)
        {
            Logger.Debug(ex, "A media read failed");
            return Native.ErrorIo;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "A media read threw unexpectedly; FFmpeg is told the read failed");
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
            Logger.Debug(ex, "A media seek to {Offset} ({Whence}) failed", offset, whence);
            return Native.ErrorIo;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "A media seek to {Offset} ({Whence}) threw unexpectedly; FFmpeg is told it failed", offset, whence);
            return Native.ErrorIo;
        }
    }
}
