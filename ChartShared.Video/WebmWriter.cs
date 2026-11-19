using System.Text;
using ChartShared.Ffmpeg.Interop;

namespace ChartShared.Video;

/// <summary>Writes one VP8 stream, from a <see cref="Vp8Encoder"/>, to a WebM file with no audio track. Not thread-safe.</summary>
internal sealed unsafe class WebmWriter : IDisposable
{
    private AVFormatContext* _output;
    private AVStream* _stream;
    private readonly AVRational _encoderTimeBase;

    /// <summary>Creates the file at <paramref name="path"/> and writes its header.</summary>
    /// <exception cref="InvalidDataException">The container can't be set up.</exception>
    /// <exception cref="IOException">The file can't be opened for writing.</exception>
    public WebmWriter(string path, Vp8Encoder encoder)
    {
        _encoderTimeBase = encoder.TimeBase;
        try
        {
            AVFormatContext* output = null;
            fixed (byte* format = Utf8("webm"))
            fixed (byte* name = Utf8(path))
            {
                int allocated = Native.avformat_alloc_output_context2(&output, null, format, name);
                if (allocated < 0 || output == null)
                    throw new InvalidDataException($"A WebM file can't be set up ({Vp8Encoder.Describe(allocated)}).");
            }

            _output = output;
            _stream = Native.avformat_new_stream(_output, null);
            if (_stream == null)
                throw new OutOfMemoryException();

            encoder.CopyParametersTo(_stream->codecpar);
            _stream->time_base = _encoderTimeBase;

            fixed (byte* name = Utf8(path))
            {
                int opened = Native.avio_open(&_output->pb, name, Native.AvioFlagWrite);
                if (opened < 0)
                    throw new IOException($"The new video can't be opened for writing ({Vp8Encoder.Describe(opened)}).");
            }

            Vp8Encoder.Check(Native.avformat_write_header(_output, null), "The new video's header can't be written");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Writes <paramref name="packet"/>, in the encoder's time base, to the file.</summary>
    /// <exception cref="IOException">It can't be written.</exception>
    public void Write(AVPacket* packet)
    {
        ObjectDisposedException.ThrowIf(_output == null, this);
        Native.av_packet_rescale_ts(packet, _encoderTimeBase, _stream->time_base);
        packet->stream_index = _stream->index;

        // It takes ownership of the packet's data whether it succeeds or not.
        int result = Native.av_interleaved_write_frame(_output, packet);
        if (result < 0)
            throw new IOException($"The new video couldn't be written ({Vp8Encoder.Describe(result)}).");
    }

    /// <summary>Writes the trailer, which makes the file seekable; without it the file is incomplete.</summary>
    public void Finish()
    {
        ObjectDisposedException.ThrowIf(_output == null, this);
        Vp8Encoder.Check(Native.av_write_trailer(_output), "The new video's trailer can't be written");
    }

    public void Dispose()
    {
        if (_output == null)
            return;

        if (_output->pb != null)
            Native.avio_closep(&_output->pb);
        Native.avformat_free_context(_output);
        _output = null;
        _stream = null;
    }

    private static byte[] Utf8(string value)
    {
        byte[] bytes = new byte[Encoding.UTF8.GetByteCount(value) + 1];
        Encoding.UTF8.GetBytes(value, bytes);
        return bytes;
    }
}
