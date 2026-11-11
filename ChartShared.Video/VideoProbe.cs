using System.Runtime.InteropServices;
using ChartShared.Core.Optimization;
using ChartShared.Ffmpeg;
using ChartShared.Ffmpeg.Interop;

namespace ChartShared.Video;

/// <summary>What <see cref="VideoProbe"/> learned about a video's first video stream.</summary>
/// <param name="Codec">FFmpeg's name for the codec: <c>vp8</c>, <c>vp9</c>, <c>av1</c>, <c>hevc</c>, <c>h264</c>, <c>mpeg4</c>.</param>
/// <param name="Width">The coded width in pixels.</param>
/// <param name="Height">The coded height in pixels.</param>
/// <param name="FrameRate">Frames per second as FFmpeg guesses it; 0 when it can't.</param>
/// <param name="Duration">How long it plays; zero when the file doesn't say.</param>
public sealed record VideoInfo(string Codec, int Width, int Height, double FrameRate, TimeSpan Duration);

/// <summary>Reads a video's codec, dimensions, frame rate and duration through avformat, without decoding it.</summary>
public static class VideoProbe
{
    /// <summary>Whether this build can probe a video.</summary>
    public static bool IsAvailable => FfmpegLibrary.IsAvailable;

    /// <summary>Probes <paramref name="stream"/>, a readable, preferably seekable stream. It's owned from here on: disposed when this returns or throws.</summary>
    /// <exception cref="InvalidOperationException">FFmpeg isn't available.</exception>
    /// <exception cref="InvalidDataException">The stream isn't a media file with a video stream.</exception>
    public static unsafe VideoInfo Probe(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var input = FfmpegStreamInput.Open(stream);
        var format = input.Format;
        int index = Native.av_find_best_stream(format, Native.MediaTypeVideo, -1, -1, null, 0);
        if (index < 0)
            throw new InvalidDataException($"The file has no video stream (FFmpeg error {index}).");

        AVStream* video = format->streams[index];
        var parameters = video->codecpar;
        string codec = Marshal.PtrToStringUTF8((IntPtr)Native.avcodec_get_name(parameters->codec_id)) ?? "unknown";

        var rate = Native.av_guess_frame_rate(format, video, null);
        double frameRate = rate.num > 0 && rate.den > 0 ? (double)rate.num / rate.den : 0;

        TimeSpan duration = TimeSpan.Zero;
        if (video->duration > 0 && video->duration != Native.NoPtsValue && video->time_base.den != 0)
            duration = TimeSpan.FromSeconds((double)video->duration * video->time_base.num / video->time_base.den);
        else if (format->duration > 0 && format->duration != Native.NoPtsValue)
            duration = TimeSpan.FromSeconds((double)format->duration / Native.TimeBase);

        return new VideoInfo(codec, parameters->width, parameters->height, frameRate, duration);
    }

    /// <summary>Probes the file at <paramref name="path"/>.</summary>
    /// <exception cref="InvalidOperationException">FFmpeg isn't available.</exception>
    /// <exception cref="InvalidDataException">It isn't a media file with a video stream.</exception>
    /// <exception cref="IOException">The file can't be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    public static VideoInfo Probe(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Probe(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete));
    }
}

/// <summary>The Library Optimization Tool's video probe: <see cref="VideoProbe"/> as an <see cref="IVideoProbe"/>.</summary>
public sealed class FfmpegVideoProbe : IVideoProbe
{
    /// <inheritdoc />
    public Task<ProbeResult?> ProbeAsync(string name, Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!VideoProbe.IsAvailable)
            return Task.FromResult<ProbeResult?>(null);

        return Task.Run(() =>
        {
            try
            {
                var info = VideoProbe.Probe(stream);
                return (ProbeResult?)new ProbeResult("video/" + info.Codec, info.Width, info.Height, info.Duration > TimeSpan.Zero ? info.Duration : null);
            }
            catch (InvalidDataException)
            {
                // Not a video FFmpeg can read; it stays unprobed, and is grouped by its extension.
                return null;
            }
        }, cancellationToken);
    }
}
