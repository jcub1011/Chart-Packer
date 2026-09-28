namespace ChartPacker.Audio.Decoding;

/// <summary>A decoded audio stream: interleaved 32-bit float samples, nominally in [-1, 1].</summary>
/// <remarks>
/// A frame is one sample for each channel. Positions and lengths are in frames at
/// <see cref="SampleRate"/>. Sources aren't thread-safe; one thread reads each at a time.
/// </remarks>
public interface IPcmSource : IDisposable
{
    int SampleRate { get; }

    int Channels { get; }

    /// <summary>How many frames the stream has, or -1 when that isn't known.</summary>
    long LengthFrames { get; }

    /// <summary>Decodes the next frames into <paramref name="buffer"/>.</summary>
    /// <param name="buffer">Room for a whole number of frames.</param>
    /// <returns>How many frames were written; 0 at the end of the stream.</returns>
    /// <exception cref="InvalidDataException">The audio is damaged.</exception>
    int ReadFrames(Span<float> buffer);

    /// <summary>Moves to <paramref name="frame"/>, so that the next read starts with it. Past the end, reads return nothing.</summary>
    /// <exception cref="InvalidDataException">The audio is damaged.</exception>
    void SeekToFrame(long frame);
}
