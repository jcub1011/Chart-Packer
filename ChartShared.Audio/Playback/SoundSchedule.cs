namespace ChartShared.Audio.Playback;

/// <summary>A short sound to lay over playback, such as a metronome click: mono samples at a rate, never changed once made.</summary>
public sealed class SynthSound
{
    private readonly float[] _samples;

    /// <param name="samples">The sound, from -1 to 1. Kept as given: nothing else may change it.</param>
    /// <param name="sampleRate">The rate it was made at.</param>
    public SynthSound(float[] samples, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        _samples = samples;
        SampleRate = sampleRate;
    }

    public int SampleRate { get; }

    public int Frames => _samples.Length;

    public ReadOnlySpan<float> Samples => _samples;

    /// <summary>How long it lasts at its own rate.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds((double)_samples.Length / SampleRate);
}

/// <summary>A <see cref="SynthSound"/> to start at <paramref name="SourceFrame"/> of the mix, at <paramref name="Gain"/>.</summary>
public readonly record struct ScheduledSound(long SourceFrame, SynthSound Sound, float Gain = 1);

/// <summary>
/// The sounds a <see cref="StemPlayer"/> lays over the mix after the time-stretcher, so they're never stretched
/// (charter-spec §4.4.1). The player asks for them a chunk at a time, on its playback thread.
/// </summary>
/// <remarks>
/// A schedule is never changed once a player has it: to change what plays, give the player another. It may be asked
/// from any thread, and from several players at once.
/// </remarks>
public interface ISoundSchedule
{
    /// <summary>
    /// Adds to <paramref name="into"/> each sound that starts from <paramref name="fromSourceFrame"/> up to, not
    /// including, <paramref name="toSourceFrame"/>, in order.
    /// </summary>
    void SoundsBetween(long fromSourceFrame, long toSourceFrame, List<ScheduledSound> into);
}
