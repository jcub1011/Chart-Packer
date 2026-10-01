using System;
using System.IO;
using ChartPacker.Core.Metadata;
using ChartPacker.Video;

namespace ChartPacker.Services
{
    /// <summary>Reads a background video's length through FFmpeg, from the container without decoding frames.</summary>
    public sealed class FfmpegVideoDurationProbe : IVideoDurationProbe
    {
        public TimeSpan? Duration(Stream stream, string name)
        {
            try
            {
                // The decoder owns the stream and disposes it; the caller's own dispose then does nothing.
                using var decoder = VideoDecoder.Open(stream, 16);
                return decoder.Duration > TimeSpan.Zero ? decoder.Duration : null;
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException)
            {
                // No FFmpeg, or not a video it can open: the video doesn't count.
                return null;
            }
        }
    }
}
