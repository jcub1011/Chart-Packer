using ChartShared.Audio.Waveforms;

namespace ChartShared.Audio.Decoding;

/// <summary>
/// A stem decoded once (<see cref="StemDecoder"/>), its channels averaged, at the rate it was decoded at: filled chunk by chunk,
/// on several threads at once, and read by any number of <see cref="CachedMonoSource"/>s, while it's filled as well as after.
/// </summary>
/// <remarks>
/// <para>It's held in blocks allocated as the decode reaches them, so what's taken grows with what's been decoded, and no
/// one array is as long as the stem.</para>
/// <para>Each chunk is a stretch of its frames, decoded from a source of its own sought to its start. The last reads on to
/// the stem's end, however long the decoder said the stem was: the stem is as long as was decoded, as a straight read of
/// it would be. Damaged audio ends the chunk it's in, and the rest of that chunk is silent.</para>
/// </remarks>
public sealed class DecodedStem
{
    /// <summary>How long a chunk is unless another length is given, in seconds.</summary>
    public const int DefaultChunkSeconds = 15;

    private const int BlockShift = 16;
    private const int BlockFrames = 1 << BlockShift;

    // As long as the decoder said the stem was; allocated block by block as they're written.
    private readonly float[]?[] _blocks;
    private readonly Chunk[] _chunks;
    private readonly long _chunkFrames;
    private readonly CancellationToken _filling;
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // What the last chunk read past the length the decoder said: only it writes this, and it's read once the stem is done.
    private float[] _overflow = [];
    private int _overflowCount;
    private long _length;
    private int _isFinished;

    /// <param name="filling">Stops the decode filling it: once it has, the stem won't be finished.</param>
    internal DecodedStem(StemPeaksKey key, int sampleRate, long frames, long chunkFrames, CancellationToken filling)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkFrames);

        Key = key;
        SampleRate = sampleRate;
        ReportedFrames = frames;
        _length = frames;
        _chunkFrames = chunkFrames;
        _filling = filling;
        _blocks = new float[]?[(int)((frames + BlockFrames - 1) >> BlockShift)];
        _chunks = new Chunk[(int)((frames + chunkFrames - 1) / chunkFrames)];
        for (int i = 0; i < _chunks.Length; i++)
            _chunks[i] = new Chunk(i * chunkFrames, Math.Min((i + 1) * chunkFrames, frames));
    }

    /// <summary>A stem decoded already, of <paramref name="samples"/>.</summary>
    public static DecodedStem Of(StemPeaksKey key, ReadOnlySpan<float> samples, int sampleRate)
    {
        var stem = new DecodedStem(key, sampleRate, Math.Max(1, samples.Length), Math.Max(1, samples.Length), CancellationToken.None);
        stem.Write(0, 0, samples);
        stem.EndChunk(0, samples.Length);
        stem.Finish(whole: true);
        return stem;
    }

    /// <summary>The file it was decoded from, as it was then.</summary>
    public StemPeaksKey Key { get; }

    public int SampleRate { get; }

    /// <summary>How long the decoder said it was.</summary>
    public long ReportedFrames { get; }

    /// <summary>How many frames it has: <see cref="ReportedFrames"/> until it's finished, then how many were decoded.</summary>
    public long LengthFrames => Interlocked.Read(ref _length);

    /// <summary>
    /// Finishes with whether the whole stem was decoded: false when the decode failed or was stopped, after which nothing
    /// more is written and it isn't to be read.
    /// </summary>
    public Task<bool> Completion => _completion.Task;

    /// <summary>Whether the whole stem has been decoded.</summary>
    public bool IsComplete => Completion is { IsCompletedSuccessfully: true, Result: true };

    /// <summary>Whether the decode filling it was stopped before it finished, so it never will.</summary>
    public bool IsAbandoned => _filling.IsCancellationRequested && !Completion.IsCompleted;

    /// <summary>How much memory it takes once all of it has been decoded.</summary>
    public long SizeBytes => (ReportedFrames + Volatile.Read(ref _overflowCount)) * sizeof(float);

    internal int ChunkCount => _chunks.Length;

    /// <summary>The frames chunk <paramref name="index"/> decodes; the last decodes on past <c>To</c>, to the stem's end.</summary>
    internal (long From, long To) ChunkRange(int index) => (_chunks[index].From, _chunks[index].To);

    /// <summary>
    /// Finishes once frames <paramref name="from"/> to <paramref name="to"/> read as they will when the stem is finished,
    /// or the decode has failed (<see cref="Completion"/>). A stretch reaching past <see cref="ReportedFrames"/>, or over
    /// a chunk that ended early, waits for the whole stem, as only then is its end known.
    /// </summary>
    public async Task WhenReadable(long from, long to, CancellationToken cancellationToken = default)
    {
        if (to > ReportedFrames)
        {
            await Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        int first = (int)Math.Clamp(from / _chunkFrames, 0, _chunks.Length - 1);
        int last = (int)Math.Clamp((to - 1) / _chunkFrames, first, _chunks.Length - 1);
        bool endedEarly = false;
        for (int i = first; i <= last; i++)
        {
            await _chunks[i].Done.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            endedEarly |= Volatile.Read(ref _chunks[i].End) < _chunks[i].To;
        }

        if (endedEarly)
            await Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Copies the frames from <paramref name="frame"/> on into <paramref name="destination"/>; those not decoded are silent.</summary>
    /// <returns>How many were copied: all of them, unless the stem ends first.</returns>
    internal int Read(long frame, Span<float> destination)
    {
        long length = LengthFrames;
        int count = (int)Math.Clamp(length - frame, 0, destination.Length);
        int done = 0;
        while (done < count)
        {
            long at = frame + done;
            int take;
            if (at >= ReportedFrames)
            {
                take = count - done;
                _overflow.AsSpan((int)(at - ReportedFrames), take).CopyTo(destination[done..]);
            }
            else
            {
                int block = (int)(at >> BlockShift);
                int offset = (int)(at & (BlockFrames - 1));
                take = (int)Math.Min(Math.Min(count - done, BlockFrames - offset), ReportedFrames - at);
                if (Volatile.Read(ref _blocks[block]) is { } samples)
                    samples.AsSpan(offset, take).CopyTo(destination[done..]);
                else
                    destination.Slice(done, take).Clear();
            }

            done += take;
        }

        return count;
    }

    /// <summary>Writes <paramref name="samples"/> from <paramref name="frame"/>, read by chunk <paramref name="chunk"/>, which alone writes those frames.</summary>
    internal void Write(int chunk, long frame, ReadOnlySpan<float> samples)
    {
        int done = 0;
        while (done < samples.Length)
        {
            long at = frame + done;
            if (at >= ReportedFrames)
            {
                // Only the last chunk reads on past the length, one read after another.
                if (chunk != _chunks.Length - 1)
                    throw new InvalidOperationException("Only the last chunk reads past the stem's length.");
                var rest = samples[done..];
                if (_overflowCount + rest.Length > _overflow.Length)
                    Array.Resize(ref _overflow, Math.Max(_overflowCount + rest.Length, Math.Max(4096, _overflow.Length * 2)));
                rest.CopyTo(_overflow.AsSpan(_overflowCount));
                _overflowCount += rest.Length;
                return;
            }

            int block = (int)(at >> BlockShift);
            int offset = (int)(at & (BlockFrames - 1));
            int take = (int)Math.Min(Math.Min(samples.Length - done, BlockFrames - offset), ReportedFrames - at);
            var target = Volatile.Read(ref _blocks[block]);
            if (target is null)
            {
                // Two chunks can meet in one block.
                var made = new float[(int)Math.Min(BlockFrames, ReportedFrames - ((long)block << BlockShift))];
                target = Interlocked.CompareExchange(ref _blocks[block], made, null) ?? made;
            }

            samples.Slice(done, take).CopyTo(target.AsSpan(offset));
            done += take;
        }
    }

    /// <summary>
    /// Says chunk <paramref name="chunk"/> is done, having read up to frame <paramref name="end"/>: past its range, for the
    /// last, when the stem was longer than the decoder said.
    /// </summary>
    internal void EndChunk(int chunk, long end)
    {
        Volatile.Write(ref _chunks[chunk].End, end);
        _chunks[chunk].Done.TrySetResult();
    }

    /// <summary>
    /// Finishes the stem: <paramref name="whole"/> once every chunk has been read, when it ends where the last chunk that
    /// read anything did; otherwise it's given up. Only the first call counts.
    /// </summary>
    /// <returns>Whether this call finished it.</returns>
    internal bool Finish(bool whole)
    {
        if (Interlocked.Exchange(ref _isFinished, 1) != 0)
            return false;

        if (whole)
        {
            long end = 0;
            for (int i = _chunks.Length - 1; i >= 0; i--)
            {
                long chunkEnd = Volatile.Read(ref _chunks[i].End);
                if (chunkEnd > _chunks[i].From)
                {
                    end = chunkEnd;
                    break;
                }
            }

            if (_overflow.Length > _overflowCount)
                Array.Resize(ref _overflow, _overflowCount);
            Interlocked.Exchange(ref _length, end);
        }

        // Nothing waits on a chunk that will never be read.
        foreach (var chunk in _chunks)
            chunk.Done.TrySetResult();
        _completion.TrySetResult(whole);
        return true;
    }

    private sealed class Chunk(long from, long to)
    {
        public long From { get; } = from;

        public long To { get; } = to;

        // The frame it read up to.
        public long End = from;

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
