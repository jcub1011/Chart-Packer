using ChartShared.Audio.Decoding;

namespace ChartShared.Audio.Mixing;

/// <summary>A <see cref="StemMixer"/>'s mix as a stereo <see cref="IPcmSource"/>, for encoding it.</summary>
/// <remarks>
/// The mixer mutes a stem whose audio turns out to be damaged and plays the rest, as playback should. A mix that's
/// written to a file mustn't: a stem missing from it would stay missing, so a read that finds one fails instead.
/// The mixer and the sources it mixes are read, not owned.
/// </remarks>
internal sealed class MixedPcmSource(StemMixer mixer) : IPcmSource
{
    public int SampleRate => mixer.OutputRate;

    public int Channels => 2;

    public long LengthFrames => mixer.LengthFrames;

    /// <exception cref="InvalidDataException">A stem's audio is damaged.</exception>
    public int ReadFrames(Span<float> buffer)
    {
        int frames = mixer.Mix(buffer[..(buffer.Length / 2 * 2)]);
        ThrowIfDamaged();
        return frames;
    }

    /// <exception cref="InvalidDataException">A stem's audio is damaged.</exception>
    public void SeekToFrame(long frame)
    {
        mixer.Seek(frame);
        ThrowIfDamaged();
    }

    public void Dispose()
    {
    }

    private void ThrowIfDamaged()
    {
        foreach (var stem in mixer.Stems)
        {
            if (stem.Error is { } error)
                throw new InvalidDataException($"The audio of '{stem.Name}' is damaged: {error}");
        }
    }
}
