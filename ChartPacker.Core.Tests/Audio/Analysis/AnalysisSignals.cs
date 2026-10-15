using ChartPacker.Audio.Analysis;
using ChartPacker.Audio.Decoding;
using ChartPacker.Audio.Synthesis;
using ChartPacker.Core.Charting.Analysis;

namespace ChartPacker.Core.Tests.Audio.Analysis;

/// <summary>Audio built in code for the analysis tests: click tracks, drum hits in separate bands, sines.</summary>
internal static class AnalysisSignals
{
    /// <summary>A mono source of <paramref name="samples"/>.</summary>
    public static TestAudio.FakeSource Source(float[] samples, int sampleRate) =>
        new(sampleRate, 1, samples.Length, (n, _) => samples[n]);

    /// <summary><paramref name="samples"/> to analyse.</summary>
    public static AnalysisInput Input(float[] samples, int sampleRate) =>
        AnalysisInput.Of([() => Source(samples, sampleRate)]);

    public static AnalysisInput Input(Func<IPcmSource> open) => AnalysisInput.Of([open]);

    /// <summary>Silence <paramref name="seconds"/> long, with noise <paramref name="noise"/> (peak) over it.</summary>
    public static float[] Silence(double seconds, int sampleRate, float noise = 0, int seed = 1)
    {
        var samples = new float[(int)Math.Round(seconds * sampleRate)];
        if (noise > 0)
        {
            var random = new Random(seed);
            for (int n = 0; n < samples.Length; n++)
                samples[n] = (float)(((random.NextDouble() * 2) - 1) * noise);
        }

        return samples;
    }

    /// <summary>Adds <paramref name="sound"/> into <paramref name="into"/> starting at <paramref name="seconds"/>.</summary>
    public static void Add(float[] into, ReadOnlySpan<float> sound, double seconds, int sampleRate, float gain = 1)
    {
        int start = (int)Math.Round(seconds * sampleRate);
        for (int n = 0; n < sound.Length && start + n < into.Length; n++)
        {
            if (start + n >= 0)
                into[start + n] += sound[n] * gain;
        }
    }

    /// <summary>A metronome click at each of <paramref name="times"/>, every <paramref name="accentEvery"/>th accented.</summary>
    public static float[] ClickTrack(IReadOnlyList<double> times, double seconds, int sampleRate, int accentEvery = 4, float noise = 0.001f)
    {
        var samples = Silence(seconds, sampleRate, noise);
        var accented = Synth.Click(true, sampleRate).Samples.ToArray();
        var plain = Synth.Click(false, sampleRate).Samples.ToArray();
        for (int i = 0; i < times.Count; i++)
            Add(samples, accentEvery > 0 && i % accentEvery == 0 ? accented : plain, times[i], sampleRate);
        return samples;
    }

    /// <summary><paramref name="count"/> beats at <paramref name="bpm"/> from <paramref name="start"/>.</summary>
    public static List<double> Beats(double bpm, int count, double start)
    {
        var times = new List<double>(count);
        for (int i = 0; i < count; i++)
            times.Add(start + (i * 60 / bpm));
        return times;
    }

    /// <summary>Beats whose tempo steps: each step's beats at its tempo, the next step starting a beat after its last.</summary>
    public static List<double> SteppedBeats(double start, params (double Bpm, int Beats)[] steps)
    {
        var times = new List<double>();
        double at = start;
        foreach (var (bpm, beats) in steps)
        {
            for (int i = 0; i < beats; i++)
            {
                times.Add(at);
                at += 60 / bpm;
            }
        }

        return times;
    }

    /// <summary>A sine of <paramref name="hz"/> at <paramref name="amplitude"/>.</summary>
    public static float[] Sine(double hz, double seconds, int sampleRate, double amplitude = 1)
    {
        var samples = new float[(int)Math.Round(seconds * sampleRate)];
        for (int n = 0; n < samples.Length; n++)
            samples[n] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * n / sampleRate));
        return samples;
    }

    /// <summary>
    /// A drum hit kept within one onset band: a 55 Hz thump for the kick, 200 Hz with sines between 2 and 5 kHz for the
    /// snare, sines between 6 and 16 kHz for cymbals. Each starts over 2 ms and dies away.
    /// </summary>
    public static float[] BandHit(OnsetBand band, int sampleRate, float peak = 0.3f)
    {
        var random = new Random(31 + (int)band);
        (double Hz, double Gain)[] partials = band switch
        {
            OnsetBand.Kick => [(55, 1)],
            OnsetBand.Snare => [(200, 1), .. Enumerable.Range(0, 40).Select(_ => (2000 + (random.NextDouble() * 3000), 0.15))],
            OnsetBand.Cymbals => [.. Enumerable.Range(0, 40).Select(_ => (6500 + (random.NextDouble() * 9000), 0.15))],
            _ => throw new ArgumentOutOfRangeException(nameof(band)),
        };
        double decay = band == OnsetBand.Cymbals ? 0.15 : 0.06;
        var phases = partials.Select(_ => random.NextDouble() * 2 * Math.PI).ToArray();

        var samples = new float[(int)(0.3 * sampleRate)];
        int fade = (int)(0.002 * sampleRate);
        float loudest = 0;
        for (int n = 0; n < samples.Length; n++)
        {
            double t = (double)n / sampleRate;
            double value = 0;
            for (int p = 0; p < partials.Length; p++)
                value += partials[p].Gain * Math.Sin((2 * Math.PI * partials[p].Hz * t) + phases[p]);
            double envelope = Math.Exp(-t / decay) * (n < fade ? (double)n / fade : 1);
            samples[n] = (float)(value * envelope);
            loudest = Math.Max(loudest, Math.Abs(samples[n]));
        }

        for (int n = 0; n < samples.Length; n++)
            samples[n] *= peak / loudest;
        return samples;
    }

    /// <summary>How far the nearest of <paramref name="times"/> is from <paramref name="seconds"/>.</summary>
    public static double DistanceToNearest(IEnumerable<double> times, double seconds) =>
        times.Select(t => Math.Abs(t - seconds)).DefaultIfEmpty(double.PositiveInfinity).Min();
}
