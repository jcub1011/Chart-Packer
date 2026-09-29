using System.Diagnostics;
using ChartPacker.Audio.Decoding;

namespace ChartPacker.Audio.Waveforms;

/// <summary>
/// Reads a stem's peaks in chunks of time that can be read side by side, each from a source of its own, so
/// that a long stem isn't decoded on one thread. Read each chunk once, then <see cref="Finish"/>.
/// </summary>
/// <remarks>
/// <para>Every chunk starts on a bucket boundary and takes the ticks of the <see cref="WaveformPeaks.SignalRate"/> grid
/// that fall on its frames, so each has buckets and ticks of its own: the chunks fill the same arrays side by side, with nothing to
/// join where they meet. A chunk's source seeks to its start, and every decoder lands a seek on the sample a
/// straight read gives there.</para>
/// <para>A stem whose length isn't known is one chunk. Chunks may be read on any threads at once, each from its
/// own source: sources aren't thread-safe.</para>
/// </remarks>
public sealed class StemPeaksReader
{
    /// <summary>How long a chunk is unless another length is given, in buckets: 15 seconds.</summary>
    public const int DefaultChunkBuckets = WaveformPeaks.BucketsPerSecond * 15;

    private readonly string _name;
    private readonly int _rate;
    private readonly int _channels;
    private readonly int _count;
    private readonly Chunk[] _chunks;

    // As long as the whole stem when that's known, so they're filled rather than grown: other threads write
    // them, and snapshots share them. What the last chunk finds past the length goes to the overflow instead.
    private readonly float[] _min;
    private readonly float[] _max;
    private readonly float[] _rms;
    private readonly Half[] _signal;

    // Only the last chunk writes these, and only Finish reads them.
    private readonly List<float> _minOverflow = [];
    private readonly List<float> _maxOverflow = [];
    private readonly List<float> _rmsOverflow = [];
    private readonly List<Half> _signalOverflow = [];

    private readonly Action<StemPeaks>? _progress;
    private readonly long _progressTicks;
    private readonly Lock _progressLock = new();
    private long _nextProgress;

    /// <summary>
    /// Sets up the read of a stem shaped as <paramref name="source"/> is: its rate, channels and length. Nothing is
    /// read from it yet.
    /// </summary>
    /// <param name="progress">
    /// Handed the peaks read so far at most once every <paramref name="progressInterval"/>, as
    /// <see cref="WaveformPeaks.Compute(string, IPcmSource, Action{StemPeaks}?, TimeSpan, CancellationToken)"/> does:
    /// as far as the chunks have been read without a gap from the start. It's called on a reading thread, never
    /// on two at once. Not at all when the length isn't known.
    /// </param>
    /// <param name="chunkBuckets">How long each chunk is, in buckets.</param>
    public StemPeaksReader(string name, IPcmSource source, Action<StemPeaks>? progress = null, TimeSpan progressInterval = default, int chunkBuckets = DefaultChunkBuckets)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkBuckets);

        _name = name;
        _rate = source.SampleRate;
        _channels = source.Channels;
        long length = source.LengthFrames;

        _count = WaveformPeaks.Count(length, WaveformPeaks.BucketsPerSecond, _rate);
        _min = new float[_count];
        _max = new float[_count];
        _rms = new float[_count];
        _signal = new Half[WaveformPeaks.Count(length, WaveformPeaks.SignalRate, _rate)];

        int chunks = length < 0 || _count == 0 ? 1 : (int)(((long)_count + chunkBuckets - 1) / chunkBuckets);
        _chunks = new Chunk[chunks];
        for (int i = 0; i < chunks; i++)
        {
            int first = (int)((long)i * chunkBuckets);
            int end = (int)Math.Min((long)(i + 1) * chunkBuckets, _count);
            _chunks[i] = new Chunk(first, FirstTickOf(first), end, FirstTickOf(end), i == chunks - 1);
        }

        if (progress is not null && length >= 0)
        {
            _progress = progress;
            _progressTicks = (long)(progressInterval.TotalSeconds * Stopwatch.Frequency);
            _nextProgress = Stopwatch.GetTimestamp() + _progressTicks;
        }
    }

    /// <summary>How many chunks there are to read, numbered from 0 at the start.</summary>
    public int ChunkCount => _chunks.Length;

    /// <summary>Reads chunk <paramref name="index"/> from <paramref name="source"/>, a source of this stem that nothing else is reading.</summary>
    /// <remarks>
    /// The source is moved to the chunk's start; chunk 0 is read from where the source is, so a source just opened
    /// needn't seek. The last chunk reads on to the source's end, however long it said it was. Damaged audio ends
    /// the chunk: what it read before stands, and the rest of it is silent. The source isn't disposed.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The chunk has already been read.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public void ReadChunk(int index, IPcmSource source, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _chunks.Length);
        ArgumentNullException.ThrowIfNull(source);

        var chunk = _chunks[index];
        if (Interlocked.Exchange(ref chunk.IsClaimed, 1) != 0)
            throw new InvalidOperationException("The chunk has already been read.");

        int bucket = chunk.FirstBucket;
        int tick = chunk.FirstTick;
        long frame = WaveformPeaks.Boundary(bucket, _rate);
        long end = chunk.IsLast ? long.MaxValue : WaveformPeaks.Boundary(chunk.EndBucket, _rate);
        long nextBoundary = WaveformPeaks.Boundary(bucket + 1, _rate);
        long nextTick = WaveformPeaks.TickFrame(tick, _rate);
        float low = 0, high = 0;
        double squares = 0;
        int bucketFrames = 0;
        bool started = false;
        var buffer = new float[WaveformPeaks.ChunkFrames * _channels];

        try
        {
            if (index > 0)
                source.SeekToFrame(frame);

            while (frame < end)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int wanted = (int)Math.Min(WaveformPeaks.ChunkFrames, end - frame);
                int frames = source.ReadFrames(buffer.AsSpan(0, wanted * _channels));
                if (frames <= 0)
                    break;

                for (int n = 0; n < frames; n++)
                {
                    while (frame >= nextBoundary)
                    {
                        PutBucket(bucket, low, high, WaveformPeaks.RootMeanSquare(squares, bucketFrames));
                        low = high = 0;
                        squares = 0;
                        bucketFrames = 0;
                        bucket++;
                        nextBoundary = WaveformPeaks.Boundary(bucket + 1, _rate);
                    }

                    float sum = 0;
                    for (int c = 0; c < _channels; c++)
                        sum += buffer[(n * _channels) + c];
                    float sample = sum / _channels;

                    // Below SignalRate, one frame is the first at or after several ticks.
                    while (frame >= nextTick)
                    {
                        PutTick(tick, (Half)sample);
                        tick++;
                        nextTick = WaveformPeaks.TickFrame(tick, _rate);
                    }

                    if (sample < low)
                        low = sample;
                    if (sample > high)
                        high = sample;
                    squares += (double)sample * sample;
                    bucketFrames++;
                    started = true;
                    frame++;
                }

                chunk.Publish(bucket, tick);
                ReportProgress();
            }
        }
        catch (InvalidDataException)
        {
            // Damaged audio: what was read stands.
        }

        if (started)
        {
            PutBucket(bucket, low, high, WaveformPeaks.RootMeanSquare(squares, bucketFrames));
            bucket++;
        }

        chunk.Publish(bucket, tick);
        Volatile.Write(ref chunk.IsStarted, started);
        Volatile.Write(ref chunk.IsDone, true);
    }

    /// <summary>The peaks, once every chunk has been read; a chunk that wasn't is silent.</summary>
    /// <remarks>
    /// They end where the last chunk that read anything did, so a stem shorter than it said, or damaged near its end,
    /// is as long as what was read of it.
    /// </remarks>
    public StemPeaks Finish()
    {
        int buckets = 0, ticks = 0;
        for (int i = _chunks.Length - 1; i >= 0; i--)
        {
            var chunk = _chunks[i];
            if (Volatile.Read(ref chunk.IsStarted))
            {
                buckets = Volatile.Read(ref chunk.ReadBuckets);
                ticks = Volatile.Read(ref chunk.ReadTicks);
                break;
            }
        }

        return new StemPeaks(
            _name,
            Trim(_min, _minOverflow, buckets),
            Trim(_max, _maxOverflow, buckets),
            Trim(_rms, _rmsOverflow, buckets),
            Trim(_signal, _signalOverflow, ticks),
            buckets,
            buckets,
            ticks);
    }

    /// <summary>The peaks read so far without a gap from the start, over the arrays still being filled.</summary>
    private StemPeaks Snapshot()
    {
        int buckets = 0, ticks = 0;
        foreach (var chunk in _chunks)
        {
            // A chunk that's done is known all the way: what it didn't read is silent in the result too.
            if (Volatile.Read(ref chunk.IsDone) && !chunk.IsLast)
            {
                buckets = chunk.EndBucket;
                ticks = chunk.EndTick;
                continue;
            }

            // Buckets first: its ticks, published before them, then cover at least as far.
            buckets = Math.Max(buckets, Volatile.Read(ref chunk.ReadBuckets));
            ticks = Math.Max(ticks, Volatile.Read(ref chunk.ReadTicks));
            break;
        }

        return new StemPeaks(_name, _min, _max, _rms, _signal, _count, Math.Min(buckets, _count), Math.Min(ticks, _signal.Length));
    }

    private void ReportProgress()
    {
        if (_progress is null)
            return;

        long now = Stopwatch.GetTimestamp();
        if (now < Volatile.Read(ref _nextProgress) || !_progressLock.TryEnter())
            return;

        try
        {
            // Another thread may have reported while this one waited.
            if (now < _nextProgress)
                return;

            _progress(Snapshot());
            Volatile.Write(ref _nextProgress, Stopwatch.GetTimestamp() + _progressTicks);
        }
        finally
        {
            _progressLock.Exit();
        }
    }

    private void PutBucket(int index, float low, float high, float rms)
    {
        if (index < _count)
        {
            _min[index] = low;
            _max[index] = high;
            _rms[index] = rms;
        }
        else
        {
            // Only the last chunk reaches past the length, one bucket after another.
            _minOverflow.Add(low);
            _maxOverflow.Add(high);
            _rmsOverflow.Add(rms);
        }
    }

    private void PutTick(int index, Half value)
    {
        if (index < _signal.Length)
            _signal[index] = value;
        else
            _signalOverflow.Add(value);
    }

    /// <summary>
    /// The first tick on or after bucket <paramref name="bucket"/>'s first frame. It's the bucket's first tick at
    /// <see cref="WaveformPeaks.SignalRate"/> and above; below it, one frame can be the first at or after the last
    /// tick of one bucket and the first of the next.
    /// </summary>
    private int FirstTickOf(int bucket) => WaveformPeaks.Count(WaveformPeaks.Boundary(bucket, _rate), WaveformPeaks.SignalRate, _rate);

    /// <summary>The first <paramref name="count"/> of <paramref name="array"/> then <paramref name="overflow"/>: the array itself when that's all of it.</summary>
    private static T[] Trim<T>(T[] array, List<T> overflow, int count)
    {
        if (array.Length == count && overflow.Count == 0)
            return array;

        var trimmed = new T[count];
        int fromArray = Math.Min(count, array.Length);
        array.AsSpan(0, fromArray).CopyTo(trimmed);
        for (int i = fromArray; i < count; i++)
            trimmed[i] = overflow[i - array.Length];
        return trimmed;
    }

    /// <summary>One chunk's buckets, and how far it has been read, published after the arrays are written.</summary>
    private sealed class Chunk(int firstBucket, int firstTick, int endBucket, int endTick, bool isLast)
    {
        public int FirstBucket { get; } = firstBucket;

        public int FirstTick { get; } = firstTick;

        /// <summary>Where the stem's buckets would end if it were read to its length: the last chunk may read on past it.</summary>
        public int EndBucket { get; } = endBucket;

        public int EndTick { get; } = endTick;

        public bool IsLast { get; } = isLast;

        // Where it has read to: the buckets done and the ticks written, counted from the stem's start.
        public int ReadBuckets = firstBucket;
        public int ReadTicks = firstTick;

        public int IsClaimed;
        public bool IsStarted;
        public bool IsDone;

        /// <summary>Says how far it has been read, ticks first, so that a snapshot reading the buckets first has the ticks for them.</summary>
        public void Publish(int buckets, int ticks)
        {
            Volatile.Write(ref ReadTicks, ticks);
            Volatile.Write(ref ReadBuckets, buckets);
        }
    }
}
