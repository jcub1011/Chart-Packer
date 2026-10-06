using ChartPacker.Audio.Playback;

namespace ChartPacker.Audio.Synthesis;

/// <summary>The drum pad or instrument a hit sound stands for (charter-spec C10 item 4).</summary>
public enum SynthHit
{
    /// <summary>A 5-fret or GHL note.</summary>
    Fret,
    Kick,
    Snare,
    Tom,
    Cymbal,
}

/// <summary>
/// Makes the short sounds Chart Builder lays over playback: the metronome's clicks, the hit sounds and the calibration
/// beep (charter-spec §4.4). Each is made anew on each call, the same every time, so a caller makes it once and keeps it.
/// </summary>
public static class Synth
{
    /// <summary>The loudest sample of a full-level sound: half of full scale, so a sound over a loud mix seldom clips.</summary>
    public const float Peak = 0.5f;

    /// <summary>A sine tone, faded in and out over <paramref name="fadeMs"/> each, at <see cref="Peak"/>.</summary>
    /// <remarks>The calibration beep is 1 kHz for 30 ms with 2 ms fades, which has a sharp start; 2 kHz marks a bar's first beat (C11).</remarks>
    public static SynthSound Beep(double hz, double ms, double fadeMs, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(hz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ms);
        ArgumentOutOfRangeException.ThrowIfNegative(fadeMs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        int frames = Math.Max(1, (int)Math.Round(ms * sampleRate / 1000));
        int fade = Math.Min(frames / 2, (int)Math.Round(fadeMs * sampleRate / 1000));
        var samples = new float[frames];
        for (int n = 0; n < frames; n++)
        {
            double envelope = Fade(n, fade) * Fade(frames - 1 - n, fade);
            samples[n] = (float)(Peak * envelope * Math.Sin(2 * Math.PI * hz * n / sampleRate));
        }

        return new SynthSound(samples, sampleRate);
    }

    /// <summary>
    /// A metronome click: a high sine that dies away within 30 ms. The click on a measure's first beat is higher and at
    /// <see cref="Peak"/>; the others are softer.
    /// </summary>
    public static SynthSound Click(bool accented, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        double hz = accented ? 2500 : 1600;
        var samples = Render(0.030, sampleRate, (t, _) => Math.Sin(2 * Math.PI * hz * t) * Math.Exp(-t / 0.006));
        return new SynthSound(Normalized(samples, accented ? Peak : Peak * 0.6f), sampleRate);
    }

    /// <summary>A hit sound for <paramref name="kind"/>, at <see cref="Peak"/>.</summary>
    public static SynthSound Hit(SynthHit kind, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        // Noise from a fixed seed, so a sound is the same every time it's made.
        var random = new Random(17 + (int)kind);
        double previous = 0;
        double Noise() => (random.NextDouble() * 2) - 1;

        float[] samples = kind switch
        {
            // A plucked tone: a fundamental and two overtones, the higher ones dying away sooner.
            SynthHit.Fret => Render(0.120, sampleRate, (t, _) =>
                (Math.Sin(2 * Math.PI * 660 * t) * Math.Exp(-t / 0.050))
                + (0.5 * Math.Sin(2 * Math.PI * 1320 * t) * Math.Exp(-t / 0.025))
                + (0.25 * Math.Sin(2 * Math.PI * 1980 * t) * Math.Exp(-t / 0.012))),

            // A sine whose pitch falls quickly, from 150 to 50 Hz.
            SynthHit.Kick => Sweep(0.180, sampleRate, 150, 50, 0.030, 0.060),
            SynthHit.Tom => Sweep(0.200, sampleRate, 260, 160, 0.050, 0.080),

            // Noise over a short tone.
            SynthHit.Snare => Render(0.150, sampleRate, (t, _) =>
                (0.7 * Noise() * Math.Exp(-t / 0.040)) + (0.5 * Math.Sin(2 * Math.PI * 190 * t) * Math.Exp(-t / 0.030))),

            // Noise with its lows taken out (each sample less the one before), dying away slowly.
            SynthHit.Cymbal => Render(0.350, sampleRate, (t, _) =>
            {
                double noise = Noise();
                double high = noise - previous;
                previous = noise;
                return high * Math.Exp(-t / 0.120);
            }),

            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        return new SynthSound(Normalized(samples, Peak), sampleRate);
    }

    /// <summary>A sine falling exponentially from <paramref name="fromHz"/> towards <paramref name="toHz"/>, its pitch over <paramref name="pitchTime"/>, its level over <paramref name="decay"/>.</summary>
    private static float[] Sweep(double seconds, int sampleRate, double fromHz, double toHz, double pitchTime, double decay)
    {
        double phase = 0;
        return Render(seconds, sampleRate, (t, step) =>
        {
            double hz = toHz + ((fromHz - toHz) * Math.Exp(-t / pitchTime));
            double sample = Math.Sin(phase) * Math.Exp(-t / decay);
            phase += 2 * Math.PI * hz * step;
            return sample;
        });
    }

    /// <summary><paramref name="seconds"/> of <paramref name="sample"/>, given each sample's time and the time between samples.</summary>
    private static float[] Render(double seconds, int sampleRate, Func<double, double, double> sample)
    {
        var samples = new float[Math.Max(1, (int)Math.Round(seconds * sampleRate))];
        double step = 1.0 / sampleRate;
        for (int n = 0; n < samples.Length; n++)
            samples[n] = (float)sample(n * step, step);
        return samples;
    }

    /// <summary>Scales <paramref name="samples"/> in place so the loudest is <paramref name="peak"/>.</summary>
    private static float[] Normalized(float[] samples, float peak)
    {
        float loudest = 0;
        foreach (float sample in samples)
            loudest = Math.Max(loudest, Math.Abs(sample));
        if (loudest > 0)
        {
            for (int n = 0; n < samples.Length; n++)
                samples[n] *= peak / loudest;
        }

        return samples;
    }

    /// <summary>A raised-cosine fade over <paramref name="length"/> frames: 0 at the edge, 1 from <paramref name="length"/> frames in.</summary>
    private static double Fade(int fromEdge, int length) =>
        length <= 0 || fromEdge >= length ? 1 : 0.5 - (0.5 * Math.Cos(Math.PI * fromEdge / length));
}
