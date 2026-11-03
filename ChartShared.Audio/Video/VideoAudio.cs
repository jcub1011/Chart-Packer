using ChartShared.Audio.Decoding;
using ChartShared.Audio.Waveforms;
using ChartShared.Ffmpeg;

namespace ChartShared.Audio.Video;

/// <summary>The audio track of a video, read for lining the video up with a song: whether there is one, and its peaks.</summary>
/// <param name="Peaks">The audio's peaks; <see langword="null"/> when the file has no audio track FFmpeg can read.</param>
public sealed record VideoAudioRead(StemPeaks? Peaks)
{
    /// <summary>The file has an audio track FFmpeg can read.</summary>
    public bool HasAudio => Peaks is not null;
}

/// <summary>Reads a background video's audio for alignment and for drawing its waveform.</summary>
public static class VideoAudio
{
    /// <summary>
    /// Reads the best audio stream of <paramref name="stream"/>, which is owned and disposed by this call, even when it
    /// throws, at <paramref name="resolution"/>. A file without audio gives <see cref="VideoAudioRead.HasAudio"/> false.
    /// </summary>
    /// <exception cref="InvalidOperationException">FFmpeg isn't available.</exception>
    /// <exception cref="InvalidDataException">The video's audio can't be read, or isn't mono or stereo.</exception>
    /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
    public static VideoAudioRead Read(Stream stream, WaveformResolution resolution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = FfmpegAudioTrackReader.TryOpen(stream);
        if (reader is null)
            return new VideoAudioRead(null);

        using var source = new ReaderSource(reader);
        return new VideoAudioRead(WaveformPeaks.Compute("video", source, resolution, cancellationToken));
    }

    /// <summary>The reader as the waveform reader's PCM source. A waveform is read in one pass, so seeking isn't offered.</summary>
    private sealed class ReaderSource(FfmpegAudioTrackReader reader) : IPcmSource
    {
        public int SampleRate => reader.SampleRate;

        public int Channels => reader.Channels;

        public long LengthFrames => reader.LengthFrames;

        public int ReadFrames(Span<float> buffer) => reader.ReadFrames(buffer);

        public void SeekToFrame(long frame) => throw new NotSupportedException("A video's audio is read in one pass.");

        // The reader owns what's left open; the source is only a view over it.
        public void Dispose()
        {
        }
    }
}
