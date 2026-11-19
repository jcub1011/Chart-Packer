using System.Collections;
using ChartShared.Audio.Decoding;

namespace ChartShared.Audio.Waveforms;

/// <summary>How finely a waveform is read: each a <see cref="WaveformResolution"/>, from coarsest to finest.</summary>
public enum WaveformFidelity
{
    /// <summary>8 ms a bucket.</summary>
    Low,

    /// <summary>4 ms a bucket.</summary>
    Medium,

    /// <summary>2 ms a bucket.</summary>
    High,

    /// <summary>1 ms a bucket.</summary>
    Ultra,
}

/// <summary>
/// How many buckets a second a waveform holds, and how often each stem is sampled for mixing: a whole number
/// of times a bucket, and never less often than 2000 times a second.
/// </summary>
public readonly record struct WaveformResolution
{
    /// <summary>8 ms a bucket, sampled 16 times a bucket.</summary>
    public static readonly WaveformResolution Low = new(125);

    /// <summary>4 ms a bucket, sampled 10 times a bucket.</summary>
    public static readonly WaveformResolution Medium = new(250);

    /// <summary>2 ms a bucket, sampled 10 times a bucket.</summary>
    public static readonly WaveformResolution High = new(500);

    /// <summary>1 ms a bucket, sampled 10 times a bucket.</summary>
    public static readonly WaveformResolution Ultra = new(1000);

    /// <summary>20 ms a bucket, sampled 40 times a bucket: what a read that doesn't say how finely is read at.</summary>
    public static readonly WaveformResolution Default = new(50);

    // Sampled fewer times a bucket than this, a mix's peaks would be guesswork.
    private const int MinTicksPerBucket = 10;

    private const int MinSignalRate = 2000;

    /// <exception cref="ArgumentOutOfRangeException">The samples for mixing wouldn't fall a whole number of times a bucket.</exception>
    public WaveformResolution(int bucketsPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bucketsPerSecond);
        BucketsPerSecond = bucketsPerSecond;
        SignalRate = Math.Max(MinSignalRate, bucketsPerSecond * MinTicksPerBucket);
        if (SignalRate % bucketsPerSecond != 0)
            throw new ArgumentOutOfRangeException(nameof(bucketsPerSecond), bucketsPerSecond, $"Must divide {MinSignalRate} evenly, or be over {MinSignalRate / MinTicksPerBucket}.");
    }

    /// <summary>How many buckets a second holds.</summary>
    public int BucketsPerSecond { get; }

    /// <summary>How often each stem is sampled for mixing.</summary>
    public int SignalRate { get; }

    /// <summary>How many of the samples for mixing fall in a bucket.</summary>
    public int TicksPerBucket => SignalRate / BucketsPerSecond;

    /// <summary>The resolution <paramref name="fidelity"/> reads at; <see cref="High"/> for one not known.</summary>
    public static WaveformResolution Of(WaveformFidelity fidelity) => fidelity switch
    {
        WaveformFidelity.Low => Low,
        WaveformFidelity.Medium => Medium,
        WaveformFidelity.Ultra => Ultra,
        _ => High,
    };
}

/// <summary>
/// One stem's loudness over time: the lowest and highest sample in each of <see cref="WaveformResolution.BucketsPerSecond"/>
/// buckets a second and how loud it is on average, and the stem itself sampled <see cref="WaveformResolution.SignalRate"/>
/// times a second for mixing.
/// </summary>
public sealed class StemPeaks
{
    private StemPeaks? _left;
    private StemPeaks? _right;

    // The series may be longer than what has been read, and a progress snapshot's are still being filled past
    // it: only the first readBuckets and readTicks are ever looked at, and the reader never writes those again.
    internal StemPeaks(string name, WaveformResolution resolution, HalfChunks min, HalfChunks max, HalfChunks rms, HalfChunks signal, int count, int readBuckets, int readTicks)
    {
        Name = name;
        Resolution = resolution;
        MinBuckets = min;
        MaxBuckets = max;
        RmsBuckets = rms;
        Signal = signal;
        Count = count;
        ReadBuckets = readBuckets;
        ReadTicks = readTicks;
        Min = new ReadSoFar(min, readBuckets, count);
        Max = new ReadSoFar(max, readBuckets, count);
        Rms = new ReadSoFar(rms, readBuckets, count);
        Peak = PeakOf(min, max, readBuckets);
    }

    public string Name { get; }

    /// <summary>
    /// The stem was read by channel (<see cref="StemPeaksReader"/>'s <c>channels</c>), so <see cref="Left"/> and
    /// <see cref="Right"/> are its channels' own; a mono stem's are its own peaks.
    /// </summary>
    public bool HasChannels { get; private set; }

    /// <summary>The left channel's peaks when <see cref="HasChannels"/>; otherwise, and for a mono stem, these.</summary>
    public StemPeaks Left => _left ?? this;

    /// <summary>The right channel's peaks when <see cref="HasChannels"/>; otherwise, and for a mono stem, these.</summary>
    public StemPeaks Right => _right ?? this;

    /// <summary>How long a bucket is, and how often the stem is sampled for mixing.</summary>
    public WaveformResolution Resolution { get; }

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

    /// <summary>Roughly how much memory the peaks take, each channel's included.</summary>
    /// <remarks>Stretches where the stem is silent take none: see <see cref="HalfChunks"/>.</remarks>
    public long SizeBytes => OwnSizeBytes + (_left?.OwnSizeBytes ?? 0) + (_right?.OwnSizeBytes ?? 0);

    private long OwnSizeBytes => MinBuckets.SizeBytes + MaxBuckets.SizeBytes + RmsBuckets.SizeBytes + Signal.SizeBytes;

    // The same buckets, as half-precision floats, so that mixing reads them directly; those from ReadBuckets on are silent.
    internal HalfChunks MinBuckets { get; }

    internal HalfChunks MaxBuckets { get; }

    internal HalfChunks RmsBuckets { get; }

    internal int ReadBuckets { get; }

    // The stem's samples, channels averaged, at each tick of the Resolution.SignalRate grid: tick k is the first frame
    // at or after k / SignalRate seconds, so every stem is sampled at the same instants whatever its rate.
    // Those from ReadTicks on are silent.
    internal HalfChunks Signal { get; }

    internal int ReadTicks { get; }

    /// <summary>These peaks, read by channel: with <paramref name="left"/> and <paramref name="right"/>, or both null for a mono stem.</summary>
    internal StemPeaks WithChannels(StemPeaks? left, StemPeaks? right)
    {
        _left = left;
        _right = right;
        HasChannels = true;
        return this;
    }

    /// <summary>How far the first <paramref name="read"/> buckets of <paramref name="min"/> and <paramref name="max"/> reach.</summary>
    internal static float PeakOf(HalfChunks min, HalfChunks max, int read)
    {
        float peak = 0;
        foreach (var span in min.Spans(0, read))
        {
            foreach (var value in span.Values)
                peak = Math.Max(peak, -(float)value);
        }

        foreach (var span in max.Spans(0, read))
        {
            foreach (var value in span.Values)
                peak = Math.Max(peak, (float)value);
        }

        return peak;
    }
}

/// <summary>The first <c>read</c> values of a series being filled, then silence up to <c>count</c>.</summary>
internal sealed class ReadSoFar(HalfChunks values, int read, int count) : IReadOnlyList<float>
{
    public int Count => count;

    public float this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count);
            return index < read ? (float)values[index] : 0f;
        }
    }

    public IEnumerator<float> GetEnumerator()
    {
        for (int i = 0; i < count; i++)
            yield return i < read ? (float)values[i] : 0f;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A stem's peaks with the switches that decide whether it's heard, and how loudly.</summary>
/// <param name="Volume">The stem's level in the mix, as <see cref="Mixing.MixerStem.Volume"/>.</param>
public readonly record struct AudibleStem(StemPeaks Peaks, bool IsMuted, bool IsSolo, float Volume = 1f);

/// <summary>The peaks of everything that is heard, as drawn; see <see cref="WaveformPeaks.Mix"/>.</summary>
public sealed class Waveform
{
    public static readonly Waveform Empty = new(WaveformResolution.Default, HalfChunks.Empty, HalfChunks.Empty, HalfChunks.Empty, 0, 0, 0, 0);

    // The buckets, as half-precision floats in chunks, so that drawing reads them directly and skips silent stretches whole.
    private readonly HalfChunks _min;
    private readonly HalfChunks _max;
    private readonly HalfChunks _rms;

    /// <param name="listed">How many of the values <see cref="Min"/>, <see cref="Max"/> and <see cref="Rms"/> show; the rest are silent.</param>
    internal Waveform(WaveformResolution resolution, HalfChunks min, HalfChunks max, HalfChunks rms, int count, int listed, int readCount, float peak)
    {
        Resolution = resolution;
        Min = new ReadSoFar(min, listed, count);
        Max = new ReadSoFar(max, listed, count);
        Rms = new ReadSoFar(rms, listed, count);
        _min = min;
        _max = max;
        _rms = rms;
        ReadCount = readCount;
        Peak = peak;
    }

    /// <summary>How long a bucket is.</summary>
    public WaveformResolution Resolution { get; }

    /// <summary>How many buckets a second holds; see <see cref="Resolution"/>.</summary>
    public int BucketsPerSecond => Resolution.BucketsPerSecond;

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
    public double Seconds => (double)Count / BucketsPerSecond;

    /// <summary>How much time the known buckets cover; see <see cref="ReadCount"/>.</summary>
    public double ReadSeconds => (double)ReadCount / BucketsPerSecond;

    /// <summary>
    /// The lowest and highest level of the known buckets from <paramref name="first"/> up to <paramref name="last"/>,
    /// and how loud they are together: the root mean square of them all, so a stretch isn't only as quiet as its loudest moment.
    /// </summary>
    /// <remarks>Only what's been read counts: the buckets past <see cref="ReadCount"/> are silent for now, not quiet.</remarks>
    public (double Low, double High, double Rms) LevelsOf(int first, int last)
    {
        first = Math.Max(0, first);
        last = Math.Min(ReadCount, last);

        // A chunk at a time, skipping silent ones whole.
        float low = 0, high = 0;
        double squares = 0;
        foreach (var span in _min.Spans(first, last))
        {
            foreach (var value in span.Values)
                low = Math.Min(low, (float)value);
        }

        foreach (var span in _max.Spans(first, last))
        {
            foreach (var value in span.Values)
                high = Math.Max(high, (float)value);
        }

        foreach (var span in _rms.Spans(first, last))
        {
            foreach (var value in span.Values)
                squares += (double)(float)value * (float)value;
        }

        return (low, high, last > first ? Math.Sqrt(squares / (last - first)) : 0);
    }
}

/// <summary>Summarises stems into peaks for drawing a waveform, and combines them the way the mixer does.</summary>
public static class WaveformPeaks
{
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

    /// <summary>Reads <paramref name="source"/> from where it is to its end, at <paramref name="resolution"/>.</summary>
    /// <inheritdoc cref="Compute(string, IPcmSource, CancellationToken)"/>
    public static StemPeaks Compute(string name, IPcmSource source, WaveformResolution resolution, CancellationToken cancellationToken = default) =>
        Compute(name, source, null, TimeSpan.Zero, cancellationToken, resolution);

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
    /// <param name="resolution">How finely to read it; <see cref="WaveformResolution.Default"/> unless given.</param>
    /// <param name="channels">Read the left and right channels' peaks too (<see cref="StemPeaks.HasChannels"/>).</param>
    public static StemPeaks Compute(string name, IPcmSource source, Action<StemPeaks>? progress, TimeSpan progressInterval, CancellationToken cancellationToken = default, WaveformResolution? resolution = null, bool channels = false)
    {
        // One chunk, as long as the stem.
        var reader = new StemPeaksReader(name, source, progress, progressInterval, int.MaxValue, resolution, channels);
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
    /// heard alone gives its own peaks. Several are summed from their <see cref="WaveformResolution.SignalRate"/> samples, which
    /// fall at the same instants in every stem, so the sum is the mix's at those instants: it can miss the
    /// sharpest peaks between them, which is the safe way round for a picture, where adding the stems' peaks
    /// would overstate every bucket. It is known as far as the heard stem read least far, or with nothing
    /// heard, as far as any stem has been read; see <see cref="Waveform.ReadCount"/>. The stems must all have
    /// been read at the same <see cref="WaveformResolution"/>.
    /// </remarks>
    /// <exception cref="ArgumentException">The stems weren't all read at the same resolution.</exception>
    public static Waveform Mix(IReadOnlyList<AudibleStem> stems)
    {
        ArgumentNullException.ThrowIfNull(stems);

        var resolution = stems.Count > 0 ? stems[0].Peaks.Resolution : WaveformResolution.Default;
        if (stems.Any(s => s.Peaks.Resolution != resolution))
            throw new ArgumentException("The stems were read at different resolutions.", nameof(stems));

        int count = stems.Select(s => s.Peaks.Count).DefaultIfEmpty(0).Max();
        var min = new HalfChunks(count, HalfChunks.BucketShift);
        var max = new HalfChunks(count, HalfChunks.BucketShift);
        var rms = new HalfChunks(count, HalfChunks.BucketShift);

        bool anySolo = stems.Any(s => s.IsSolo);
        var heard = stems.Where(s => (anySolo ? s.IsSolo : !s.IsMuted) && s.Volume > 0).ToList();

        // A stem read to its end is known all the way, even when it's shorter than the others.
        int readCount = (heard.Count > 0 ? heard : stems)
            .Select(s => s.Peaks.ReadBuckets == s.Peaks.Count ? count : s.Peaks.ReadBuckets)
            .DefaultIfEmpty(0)
            .Min();

        if (heard.Count == 1)
        {
            // Its silent stretches stay silent, and take nothing.
            float volume = heard[0].Volume;
            int read = heard[0].Peaks.ReadBuckets;
            Scale(heard[0].Peaks.MinBuckets, min, -1f, 0f);
            Scale(heard[0].Peaks.MaxBuckets, max, 0f, 1f);
            Scale(heard[0].Peaks.RmsBuckets, rms, 0f, 1f);

            void Scale(HalfChunks from, HalfChunks into, float lowest, float highest)
            {
                foreach (var span in from.Spans(0, read))
                {
                    for (int i = 0; i < span.Values.Length; i++)
                        into.Set(span.Start + i, Math.Clamp((float)span.Values[i] * volume, lowest, highest));
                }
            }
        }
        else if (heard.Count > 1)
        {
            int ticksPerBucket = resolution.TicksPerBucket;
            Span<float> sum = stackalloc float[ticksPerBucket];
            for (int b = 0; b < count; b++)
            {
                sum.Clear();
                int first = b * ticksPerBucket;

                // The loudness is of the ticks some stem reaches: a bucket the stems end part way through isn't quieter for it.
                int covered = 0;
                foreach (var stem in heard)
                {
                    int end = Math.Min(stem.Peaks.ReadTicks, first + ticksPerBucket);
                    foreach (var span in stem.Peaks.Signal.Spans(first, end))
                    {
                        for (int k = 0; k < span.Values.Length; k++)
                            sum[span.Start - first + k] += (float)span.Values[k] * stem.Volume;
                    }

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

                min.Set(b, low);
                max.Set(b, high);
                rms.Set(b, RootMeanSquare(squares, covered));
            }
        }

        readCount = Math.Min(readCount, count);
        return new Waveform(resolution, min, max, rms, count, count, readCount, StemPeaks.PeakOf(min, max, readCount));
    }

    /// <summary>
    /// <see cref="Mix"/> of each channel: the stems' left channels mixed, and their right. A mono stem is in both.
    /// </summary>
    /// <exception cref="ArgumentException">A stem wasn't read by channel, or they weren't all read at the same resolution.</exception>
    public static (Waveform Left, Waveform Right) MixChannels(IReadOnlyList<AudibleStem> stems)
    {
        ArgumentNullException.ThrowIfNull(stems);
        if (stems.Any(s => !s.Peaks.HasChannels))
            throw new ArgumentException("Every stem must be read by channel.", nameof(stems));

        return (Mix([.. stems.Select(s => s with { Peaks = s.Peaks.Left })]), Mix([.. stems.Select(s => s with { Peaks = s.Peaks.Right })]));
    }

    /// <summary>
    /// <paramref name="peaks"/> on their own, at full level, as <see cref="Mix"/> would give them heard alone. They
    /// aren't copied, so this is cheap enough to do for each progress snapshot.
    /// </summary>
    public static Waveform Of(StemPeaks peaks)
    {
        ArgumentNullException.ThrowIfNull(peaks);
        return new Waveform(peaks.Resolution, peaks.MinBuckets, peaks.MaxBuckets, peaks.RmsBuckets, peaks.Count, peaks.ReadBuckets, peaks.ReadBuckets, peaks.Peak);
    }

    internal static float RootMeanSquare(double squares, int count) => count <= 0 ? 0f : (float)Math.Sqrt(squares / count);

    /// <summary>How many of a grid's <paramref name="perSecond"/> steps a second have a frame of <paramref name="frames"/> at <paramref name="rate"/>.</summary>
    internal static int Count(long frames, int perSecond, int rate) =>
        frames <= 0 ? 0 : (int)(((frames - 1) * perSecond / rate) + 1);

    /// <summary>The first frame at <paramref name="rate"/> of bucket <paramref name="bucket"/>, of <paramref name="perSecond"/> a second.</summary>
    internal static long Boundary(int bucket, int perSecond, int rate) => (((long)bucket * rate) + perSecond - 1) / perSecond;

    /// <summary>The frame at <paramref name="rate"/> sampled for tick <paramref name="tick"/>, of <paramref name="signalRate"/> a second: the first at or after it.</summary>
    internal static long TickFrame(long tick, int signalRate, int rate) => ((tick * rate) + signalRate - 1) / signalRate;
}
