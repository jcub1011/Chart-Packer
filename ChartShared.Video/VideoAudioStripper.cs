using System.Text;
using ChartShared.Ffmpeg;
using ChartShared.Ffmpeg.Interop;

namespace ChartShared.Video;

/// <summary>Rewrites a background video without its audio track, keeping its video streams as they are.</summary>
public static class VideoAudioStripper
{
    /// <summary>Whether this build can strip a video's audio.</summary>
    public static bool IsAvailable => FfmpegLibrary.IsAvailable;

    /// <summary>
    /// Removes every audio stream from the video at <paramref name="path"/>, replacing it only once the rewrite succeeded.
    /// </summary>
    /// <returns>Whether it had audio to remove; <see langword="false"/> leaves the file untouched.</returns>
    /// <exception cref="InvalidOperationException">FFmpeg isn't available.</exception>
    /// <exception cref="InvalidDataException">The video can't be read, or its container can't be written without audio.</exception>
    /// <exception cref="IOException">The file can't be read or written.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">The rewrite was cancelled.</exception>
    public static unsafe bool StripInPlace(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string folder = Path.GetDirectoryName(path) ?? throw new IOException($"'{path}' has no folder.");
        string target = Path.Combine(folder, $".{Path.GetFileNameWithoutExtension(path)}.{Guid.NewGuid():N}{Path.GetExtension(path)}");
        bool written = false;
        try
        {
            using (var input = FfmpegStreamInput.Open(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)))
            {
                if (Native.av_find_best_stream(input.Format, Native.MediaTypeAudio, -1, -1, null, 0) < 0)
                    return false;
                Remux(input.Format, target, cancellationToken);
            }

            File.Move(target, path, overwrite: true);
            written = true;
            return true;
        }
        finally
        {
            if (!written)
            {
                try
                {
                    File.Delete(target);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static unsafe void Remux(AVFormatContext* source, string target, CancellationToken cancellationToken)
    {
        AVFormatContext* output = null;
        byte[] fileName = Utf8(target);
        fixed (byte* name = fileName)
        {
            int allocated = Native.avformat_alloc_output_context2(&output, null, null, name);
            if (allocated < 0 || output == null)
                throw new InvalidDataException($"The video's container can't be written without audio (FFmpeg error {allocated}).");
        }

        AVPacket* packet = null;
        try
        {
            int streamCount = (int)source->nb_streams;
            int[] streamMap = new int[streamCount];
            Array.Fill(streamMap, -1);
            int videoStreams = 0;
            for (int i = 0; i < streamCount; i++)
            {
                AVStream* from = source->streams[i];
                if (from->codecpar->codec_type != Native.MediaTypeVideo)
                    continue;

                AVStream* to = Native.avformat_new_stream(output, null);
                if (to == null)
                    throw new OutOfMemoryException();
                Check(Native.avcodec_parameters_copy(to->codecpar, from->codecpar), "The video's stream couldn't be copied");
                to->time_base = from->time_base;
                to->codecpar->codec_tag = 0;
                streamMap[i] = to->index;
                videoStreams++;
            }

            if (videoStreams == 0)
                throw new InvalidDataException("The file has no video stream to keep.");

            byte[] targetName = Utf8(target);
            fixed (byte* name = targetName)
                Check(Native.avio_open(&output->pb, name, Native.AvioFlagWrite), "The new video can't be opened for writing");

            Check(Native.avformat_write_header(output, null), "The new video's header can't be written");

            packet = Native.av_packet_alloc();
            if (packet == null)
                throw new OutOfMemoryException();

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = Native.av_read_frame(source, packet);
                if (read == Native.ErrorEof)
                    break;
                if (read < 0)
                    throw new InvalidDataException($"The video couldn't be read (FFmpeg error {read}).");

                int index = packet->stream_index;
                if (index < 0 || index >= streamMap.Length || streamMap[index] < 0)
                {
                    Native.av_packet_unref(packet);
                    continue;
                }

                AVStream* from = source->streams[index];
                AVStream* to = output->streams[streamMap[index]];
                Native.av_packet_rescale_ts(packet, from->time_base, to->time_base);
                packet->stream_index = streamMap[index];
                int result = Native.av_interleaved_write_frame(output, packet);

                // It takes ownership of the packet whether it succeeds or not.
                if (result < 0)
                    throw new IOException($"The new video couldn't be written (FFmpeg error {result}).");
            }

            Check(Native.av_write_trailer(output), "The new video's trailer can't be written");
        }
        finally
        {
            if (packet != null)
            {
                AVPacket* free = packet;
                Native.av_packet_free(&free);
            }

            if (output != null)
            {
                if (output->pb != null)
                    Native.avio_closep(&output->pb);
                Native.avformat_free_context(output);
            }
        }
    }

    private static byte[] Utf8(string value)
    {
        byte[] bytes = new byte[Encoding.UTF8.GetByteCount(value) + 1];
        Encoding.UTF8.GetBytes(value, bytes);
        return bytes;
    }

    private static void Check(int result, string message)
    {
        if (result < 0)
            throw new InvalidDataException($"{message} (FFmpeg error {result}).");
    }
}
