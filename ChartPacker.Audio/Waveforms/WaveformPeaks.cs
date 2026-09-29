using ChartPacker.Audio.Decoding;

namespace ChartPacker.Audio.Waveforms;

/// <summary>One stem's loudness over time: the lowest and highest sample in each <see cref="WaveformPeaks.BucketsPerSecond"/>th of a second.</summary>
public sealed class StemPeaks
{
    internal StemPeaks(string name, float[] min, float[] max)
    {
        Name = name;
        Min = min;
        Max = max;
    }

    public string Name { get; }

    /// <summary>The lowest sample of each bucket, at most 0. Channels are averaged.</summary>
    public IReadOnlyList<float> Min { get; }

    /// <summary>The highest sample of each bucket, at least 0.</summary>
    public IReadOnlyList<float> Max { get; }

    public int Count => Min.Count;
}

/// <summary>A stem's peaks with the switches that decide whether it's heard.</summary>
public readonly record struct AudibleStem(StemPeaks Peaks, bool IsMuted, bool IsSolo);

/// <summary>The peaks of everything that is heard, as drawn; see <see cref="WaveformPeaks.Mix"/>.</summary>
public sealed class Waveform
{
    public static readonly Waveform Empty = new([], []);

    internal Waveform(float[] min, float[] max)
    {
        Min = min;
        Max = max;
    }

    /// <summary>The lowest level of each bucket, from -1 to 0.</summary>
    public IReadOnlyList<float> Min { get; }

    /// <summary>The highest level of each bucket, from 0 to 1.</summary>
    public IReadOnlyList<float> Max { get; }

    public int Count => Min.Count;

    /// <summary>How much time the buckets cover.</summary>
    public double Seconds => (double)Count / WaveformPeaks.BucketsPerSecond;
}

/// <summary>Summarises stems into peaks for drawing a waveform, and combines them the way the mixer does.</summary>
public static class WaveformPeaks
{
    /// <summary>The waveform's resolution: 20 ms per bucket, however the stems are sampled.</summary>
    public const int BucketsPerSecond = 50;

    private const int ChunkFrames = 4096;

    /// <summary>Reads <paramref name="source"/> from where it is to its end.</summary>
    /// <remarks>
    /// A stem whose audio turns out to be damaged keeps the peaks read before that point. The source
    /// isn't disposed, and mustn't be in use elsewhere: sources aren't thread-safe.
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static StemPeaks Compute(string name, IPcmSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(source);

        int rate = source.SampleRate;
        int channels = source.Channels;
        var min = new List<float>();
        var max = new List<float>();
        var buffer = new float[ChunkFrames * channels];

        long frame = 0;
        int bucket = 0;
        long nextBoundary = Boundary(1, rate);
        float low = 0, high = 0;
        bool started = false;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int frames = source.ReadFrames(buffer);
                if (frames <= 0)
                    break;

                for (int n = 0; n < frames; n++)
                {
                    while (frame >= nextBoundary)
                    {
                        min.Add(low);
                        max.Add(high);
                        low = high = 0;
                        bucket++;
                        nextBoundary = Boundary(bucket + 1, rate);
                    }

                    float sum = 0;
                    for (int c = 0; c < channels; c++)
                        sum += buffer[(n * channels) + c];
                    float sample = sum / channels;

                    if (sample < low)
                        low = sample;
                    if (sample > high)
                        high = sample;
                    started = true;
                    frame++;
                }
            }
        }
        catch (InvalidDataException)
        {
            // Damaged audio: what was read stands.
        }

        if (started)
        {
            min.Add(low);
            max.Add(high);
        }

        return new StemPeaks(name, [.. min], [.. max]);
    }

    /// <summary>
    /// The peaks of the stems that are heard, summed and clipped to [-1, 1]: as the mixer does, if any
    /// stem is soloed only the soloed ones are heard, muted or not; otherwise the unmuted ones are.
    /// </summary>
    /// <remarks>
    /// It covers the longest stem, muted or not, so muting doesn't change how much time is shown. Adding
    /// the stems' peaks overstates the peak of their sum (the peaks needn't coincide), which is the safe
    /// way round for a picture.
    /// </remarks>
    public static Waveform Mix(IReadOnlyList<AudibleStem> stems)
    {
        ArgumentNullException.ThrowIfNull(stems);

        int count = stems.Select(s => s.Peaks.Count).DefaultIfEmpty(0).Max();
        var min = new float[count];
        var max = new float[count];

        bool anySolo = stems.Any(s => s.IsSolo);
        foreach (var stem in stems)
        {
            if (!(anySolo ? stem.IsSolo : !stem.IsMuted))
                continue;

            for (int i = 0; i < stem.Peaks.Count; i++)
            {
                min[i] += stem.Peaks.Min[i];
                max[i] += stem.Peaks.Max[i];
            }
        }

        for (int i = 0; i < count; i++)
        {
            min[i] = Math.Clamp(min[i], -1f, 0f);
            max[i] = Math.Clamp(max[i], 0f, 1f);
        }

        return new Waveform(min, max);
    }

    /// <summary>The first frame of bucket <paramref name="bucket"/> at <paramref name="rate"/>.</summary>
    private static long Boundary(int bucket, int rate) => (((long)bucket * rate) + BucketsPerSecond - 1) / BucketsPerSecond;
}
