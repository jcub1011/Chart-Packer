using System.Collections;
using ChartPacker.Audio.Decoding;

namespace ChartPacker.Audio.Waveforms;

/// <summary>
/// One stem's loudness over time: the lowest and highest sample in each <see cref="WaveformPeaks.BucketsPerSecond"/>th
/// of a second and how loud it is on average, and the stem itself sampled <see cref="WaveformPeaks.SignalRate"/>
/// times a second for mixing.
/// </summary>
public sealed class StemPeaks
{
    // The arrays may be longer than what has been read, and a progress snapshot's are still being filled past
    // it: only the first readBuckets and readTicks are ever looked at, and the reader never writes those again.
    internal StemPeaks(string name, float[] min, float[] max, float[] rms, Half[] signal, int count, int readBuckets, int readTicks)
    {
        Name = name;
        MinBuckets = min;
        MaxBuckets = max;
        RmsBuckets = rms;
        Signal = signal;
        Count = count;
        ReadBuckets = readBuckets;
        ReadTicks = readTicks;
        Min = ReadSoFar.Of(min, readBuckets, count);
        Max = ReadSoFar.Of(max, readBuckets, count);
        Rms = ReadSoFar.Of(rms, readBuckets, count);
        Peak = PeakOf(min, max, readBuckets);
    }

    public string Name { get; }

    /// <summary>The lowest sample of each bucket, at most 0. Channels are averaged.</summary>
    public IReadOnlyList<float> Min { get; }

    /// <summary>The highest sample of each bucket, at least 0.</summary>
    public IReadOnlyList<float> Max { get; }

    /// <summary>
    /// The root mean square of each bucket's samples, from 0 to 1: how loud it sounds, where <see cref="Max"/>
    /// is how far its loudest sample reaches.
    /// </summary>
    public IReadOnlyList<float> Rms { get; }

    public int Count { get; }

    /// <summary>
    /// How many of the buckets have been read: <see cref="Count"/> once the read is done, and fewer in a
    /// progress snapshot, where the rest are silent because they haven't been read yet.
    /// </summary>
    public int ReadCount => ReadBuckets;

    /// <summary>How far the loudest sample read so far reaches, from 0 to 1, either side of silence.</summary>
    public float Peak { get; }

    /// <summary>Roughly how much memory the peaks take.</summary>
    public long SizeBytes => ((long)(MinBuckets.Length + MaxBuckets.Length + RmsBuckets.Length) * sizeof(float)) + ((long)Signal.Length * 2);

    // The same buckets as arrays, so that mixing indexes them directly; those from ReadBuckets on are silent.
    internal float[] MinBuckets { get; }

    internal float[] MaxBuckets { get; }

    internal float[] RmsBuckets { get; }

    internal int ReadBuckets { get; }

    // The stem's samples, channels averaged, at each tick of the SignalRate grid: tick k is the first frame
    // at or after k / SignalRate seconds, so every stem is sampled at the same instants whatever its rate.
    // Those from ReadTicks on are silent.
    internal Half[] Signal { get; }

    internal int ReadTicks { get; }

    /// <summary>How far the first <paramref name="read"/> buckets of <paramref name="min"/> and <paramref name="max"/> reach.</summary>
    internal static float PeakOf(float[] min, float[] max, int read)
    {
        float peak = 0;
        for (int i = 0; i < read; i++)
            peak = Math.Max(peak, Math.Max(-min[i], max[i]));
        return peak;
    }

    /// <summary>The first <c>read</c> values of an array being filled, then silence up to <c>count</c>.</summary>
    private sealed class ReadSoFar(float[] values, int read, int count) : IReadOnlyList<float>
    {
        /// <summary><paramref name="values"/> itself when it's all read and as long as it should be, else a view of what's read.</summary>
        public static IReadOnlyList<float> Of(float[] values, int read, int count) =>
            read == count && values.Length == count ? values : new ReadSoFar(values, read, count);

        public int Count => count;

        public float this[int index]
        {
            get
            {
                ArgumentOutOfRangeException.ThrowIfNegative(index);
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count);
                return index < read ? values[index] : 0f;
            }
        }

        public IEnumerator<float> GetEnumerator()
        {
            for (int i = 0; i < count; i++)
                yield return i < read ? values[i] : 0f;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

/// <summary>A stem's peaks with the switches that decide whether it's heard, and how loudly.</summary>
/// <param name="Volume">The stem's level in the mix, as <see cref="Mixing.MixerStem.Volume"/>.</param>
public readonly record struct AudibleStem(StemPeaks Peaks, bool IsMuted, bool IsSolo, float Volume = 1f);

/// <summary>The peaks of everything that is heard, as drawn; see <see cref="WaveformPeaks.Mix"/>.</summary>
public sealed class Waveform
{
    public static readonly Waveform Empty = new([], [], [], 0, 0);

    internal Waveform(IReadOnlyList<float> min, IReadOnlyList<float> max, IReadOnlyList<float> rms, int readCount, float peak)
    {
        Min = min;
        Max = max;
        Rms = rms;
        ReadCount = readCount;
        Peak = peak;
    }

    /// <summary>The lowest level of each bucket, from -1 to 0.</summary>
    public IReadOnlyList<float> Min { get; }

    /// <summary>The highest level of each bucket, from 0 to 1.</summary>
    public IReadOnlyList<float> Max { get; }

    /// <summary>How loud each bucket is, as the root mean square of its level, from 0 to 1.</summary>
    public IReadOnlyList<float> Rms { get; }

    public int Count => Min.Count;

    /// <summary>
    /// How many buckets, from the start, are known: all of them once every stem in it has been read, and
    /// until then no further than the stem read least far. The rest are silent for now.
    /// </summary>
    public int ReadCount { get; }

    /// <summary>How far the loudest of the known buckets reaches, from 0 to 1, either side of silence.</summary>
    public float Peak { get; }

    /// <summary>How much time the buckets cover.</summary>
    public double Seconds => (double)Count / WaveformPeaks.BucketsPerSecond;

    /// <summary>How much time the known buckets cover; see <see cref="ReadCount"/>.</summary>
    public double ReadSeconds => (double)ReadCount / WaveformPeaks.BucketsPerSecond;
}

/// <summary>Summarises stems into peaks for drawing a waveform, and combines them the way the mixer does.</summary>
public static class WaveformPeaks
{
    /// <summary>The waveform's resolution: 20 ms per bucket, however the stems are sampled.</summary>
    public const int BucketsPerSecond = 50;

    /// <summary>How often each stem is sampled for mixing: 40 times a bucket.</summary>
    public const int SignalRate = 2000;

    private const int TicksPerBucket = SignalRate / BucketsPerSecond;

    // How many frames are decoded at a time.
    internal const int ChunkFrames = 4096;

    /// <summary>Reads <paramref name="source"/> from where it is to its end.</summary>
    /// <remarks>
    /// A stem whose audio turns out to be damaged keeps the peaks read before that point. The source
    /// isn't disposed, and mustn't be in use elsewhere: sources aren't thread-safe. It's read on one
    /// thread; <see cref="StemPeaksReader"/> reads a stem in chunks on several.
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static StemPeaks Compute(string name, IPcmSource source, CancellationToken cancellationToken = default) =>
        Compute(name, source, null, TimeSpan.Zero, cancellationToken);

    /// <summary>
    /// Reads <paramref name="source"/> from where it is to its end, as <see cref="Compute(string, IPcmSource, CancellationToken)"/>,
    /// handing <paramref name="progress"/> the peaks read so far at most once every <paramref name="progressInterval"/>.
    /// </summary>
    /// <remarks>
    /// Progress is only reported when the source knows its length: each snapshot is that long, silent past
    /// what has been read, so a waveform drawn from it fills in rather than stretching. It's called on the
    /// reading thread. A snapshot isn't copied: it shares the arrays still being filled, but only ever shows
    /// what had been read when it was taken. The result is the same as without progress.
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static StemPeaks Compute(string name, IPcmSource source, Action<StemPeaks>? progress, TimeSpan progressInterval, CancellationToken cancellationToken = default)
    {
        // One chunk, as long as the stem.
        var reader = new StemPeaksReader(name, source, progress, progressInterval, int.MaxValue);
        reader.ReadChunk(0, source, cancellationToken);
        return reader.Finish();
    }

    /// <summary>
    /// The peaks of the stems that are heard, mixed as the mixer does: if any stem is soloed only the soloed
    /// ones are heard, muted or not; otherwise the unmuted ones are. The stems' samples are summed and
    /// clipped to [-1, 1], and each bucket holds the lowest and highest of the sum, and its root mean square.
    /// </summary>
    /// <remarks>
    /// It covers the longest stem, muted or not, so muting doesn't change how much time is shown. One stem
    /// heard alone gives its own peaks. Several are summed from their <see cref="SignalRate"/> samples, which
    /// fall at the same instants in every stem, so the sum is the mix's at those instants: it can miss the
    /// sharpest peaks between them, which is the safe way round for a picture, where adding the stems' peaks
    /// would overstate every bucket. It is known as far as the heard stem read least far, or with nothing
    /// heard, as far as any stem has been read; see <see cref="Waveform.ReadCount"/>.
    /// </remarks>
    public static Waveform Mix(IReadOnlyList<AudibleStem> stems)
    {
        ArgumentNullException.ThrowIfNull(stems);

        int count = stems.Select(s => s.Peaks.Count).DefaultIfEmpty(0).Max();
        var min = new float[count];
        var max = new float[count];
        var rms = new float[count];

        bool anySolo = stems.Any(s => s.IsSolo);
        var heard = stems.Where(s => (anySolo ? s.IsSolo : !s.IsMuted) && s.Volume > 0).ToList();

        // A stem read to its end is known all the way, even when it's shorter than the others.
        int readCount = (heard.Count > 0 ? heard : stems)
            .Select(s => s.Peaks.ReadBuckets == s.Peaks.Count ? count : s.Peaks.ReadBuckets)
            .DefaultIfEmpty(0)
            .Min();

        if (heard.Count == 1)
        {
            float volume = heard[0].Volume;
            float[] stemMin = heard[0].Peaks.MinBuckets;
            float[] stemMax = heard[0].Peaks.MaxBuckets;
            float[] stemRms = heard[0].Peaks.RmsBuckets;
            for (int i = 0; i < heard[0].Peaks.ReadBuckets; i++)
            {
                min[i] = Math.Clamp(stemMin[i] * volume, -1f, 0f);
                max[i] = Math.Clamp(stemMax[i] * volume, 0f, 1f);
                rms[i] = Math.Clamp(stemRms[i] * volume, 0f, 1f);
            }
        }
        else if (heard.Count > 1)
        {
            Span<float> sum = stackalloc float[TicksPerBucket];
            for (int b = 0; b < count; b++)
            {
                sum.Clear();
                int first = b * TicksPerBucket;

                // The loudness is of the ticks some stem reaches: a bucket the stems end part way through isn't quieter for it.
                int covered = 0;
                foreach (var stem in heard)
                {
                    Half[] stemSignal = stem.Peaks.Signal;
                    int end = Math.Min(stem.Peaks.ReadTicks, first + TicksPerBucket);
                    for (int k = first; k < end; k++)
                        sum[k - first] += (float)stemSignal[k] * stem.Volume;
                    covered = Math.Max(covered, end - first);
                }

                float low = 0, high = 0;
                double squares = 0;
                foreach (float value in sum)
                {
                    float clipped = Math.Clamp(value, -1f, 1f);
                    if (clipped < low)
                        low = clipped;
                    if (clipped > high)
                        high = clipped;
                    squares += (double)clipped * clipped;
                }

                min[b] = low;
                max[b] = high;
                rms[b] = RootMeanSquare(squares, covered);
            }
        }

        readCount = Math.Min(readCount, count);
        return new Waveform(min, max, rms, readCount, StemPeaks.PeakOf(min, max, readCount));
    }

    /// <summary>
    /// <paramref name="peaks"/> on their own, at full level, as <see cref="Mix"/> would give them heard alone. They
    /// aren't copied, so this is cheap enough to do for each progress snapshot.
    /// </summary>
    public static Waveform Of(StemPeaks peaks)
    {
        ArgumentNullException.ThrowIfNull(peaks);
        return new Waveform(peaks.Min, peaks.Max, peaks.Rms, peaks.ReadBuckets, peaks.Peak);
    }

    internal static float RootMeanSquare(double squares, int count) => count <= 0 ? 0f : (float)Math.Sqrt(squares / count);

    /// <summary>How many of a grid's <paramref name="perSecond"/> steps a second have a frame of <paramref name="frames"/> at <paramref name="rate"/>.</summary>
    internal static int Count(long frames, int perSecond, int rate) =>
        frames <= 0 ? 0 : (int)(((frames - 1) * perSecond / rate) + 1);

    /// <summary>The first frame of bucket <paramref name="bucket"/> at <paramref name="rate"/>.</summary>
    internal static long Boundary(int bucket, int rate) => (((long)bucket * rate) + BucketsPerSecond - 1) / BucketsPerSecond;

    /// <summary>The frame sampled for tick <paramref name="tick"/> at <paramref name="rate"/>: the first at or after it.</summary>
    internal static long TickFrame(long tick, int rate) => ((tick * rate) + SignalRate - 1) / SignalRate;
}
