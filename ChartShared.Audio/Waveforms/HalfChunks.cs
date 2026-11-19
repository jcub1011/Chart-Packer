namespace ChartShared.Audio.Waveforms;

/// <summary>
/// A run of <see cref="Half"/> values kept in chunks, where a chunk that's only ever silent takes no memory: a stem's
/// peaks, or its samples for mixing, through stretches where it's quiet are nothing at all.
/// </summary>
/// <remarks>
/// A chunk is made when a value that isn't 0 is first put in it. Anything quieter than about -144 dBFS is 0 as a
/// <see cref="Half"/>, so leaving out chunks of 0 loses nothing. Values may be put in from several threads at once, each
/// putting its own; reads of values that have been put see them, once the putting thread has published how far it got.
/// </remarks>
internal sealed class HalfChunks
{
    /// <summary>How many values a chunk of buckets holds, as a power of two: about a second at 1000 buckets a second.</summary>
    public const int BucketShift = 10;

    /// <summary>How many values a chunk of samples for mixing holds, as a power of two.</summary>
    public const int SignalShift = 13;

    public static readonly HalfChunks Empty = new(0, BucketShift);

    private readonly Half[]?[] _chunks;
    private readonly int _shift;
    private readonly int _mask;

    /// <param name="count">How many values it holds, all 0 to begin with.</param>
    /// <param name="shift">How many values a chunk holds, as a power of two.</param>
    public HalfChunks(int count, int shift)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfNegative(shift);
        Count = count;
        _shift = shift;
        _mask = (1 << shift) - 1;
        _chunks = new Half[]?[(int)(((long)count + _mask) >> shift)];
    }

    // The same chunks as another, as many of them as count needs: see Trimmed.
    private HalfChunks(Half[]?[] chunks, int count, int shift)
    {
        Count = count;
        _shift = shift;
        _mask = (1 << shift) - 1;
        _chunks = chunks;
    }

    public int Count { get; }

    /// <summary>How many values a chunk holds.</summary>
    public int ChunkLength => 1 << _shift;

    /// <summary>How many chunks there are, made or not.</summary>
    public int ChunkCount => _chunks.Length;

    /// <summary>Roughly how much memory it takes: the chunks made, and the table of them.</summary>
    public long SizeBytes
    {
        get
        {
            long bytes = (long)_chunks.Length * IntPtr.Size;
            foreach (var chunk in _chunks)
            {
                if (chunk is not null)
                    bytes += (long)chunk.Length * 2;
            }

            return bytes;
        }
    }

    /// <summary>Value <paramref name="index"/>: 0 in a chunk that hasn't been made.</summary>
    public Half this[int index]
    {
        get
        {
            var chunk = Volatile.Read(ref _chunks[index >> _shift]);
            return chunk is null ? Half.Zero : chunk[index & _mask];
        }
    }

    /// <summary>Puts <paramref name="value"/> at <paramref name="index"/>; a 0 where nothing's been put needs no chunk made.</summary>
    public void Set(int index, Half value)
    {
        ref var slot = ref _chunks[index >> _shift];
        var chunk = Volatile.Read(ref slot);
        if (chunk is null)
        {
            if (value == Half.Zero)
                return;

            // Two threads may each put a first value in one chunk: one chunk is kept, which both put into.
            chunk = Interlocked.CompareExchange(ref slot, new Half[1 << _shift], null) ?? Volatile.Read(ref slot)!;
        }

        chunk[index & _mask] = value;
    }

    /// <summary>Puts <paramref name="value"/> at <paramref name="index"/>, as a <see cref="Half"/>.</summary>
    public void Set(int index, float value) => Set(index, (Half)value);

    /// <summary>Chunk <paramref name="chunk"/>'s values, or <see langword="null"/> when it hasn't been made and they're all 0.</summary>
    public Half[]? ChunkAt(int chunk) => Volatile.Read(ref _chunks[chunk]);

    /// <summary>The first <paramref name="count"/> values, sharing these chunks: for a run read shorter than it was made.</summary>
    public HalfChunks Trimmed(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, Count);
        if (count == Count)
            return this;

        int chunks = (int)(((long)count + _mask) >> _shift);
        var kept = new Half[]?[chunks];
        Array.Copy(_chunks, kept, chunks);

        // What's past the end in the last chunk kept is 0, as it is past the end of anything.
        if (chunks > 0 && (count & _mask) != 0 && kept[chunks - 1] is { } last)
        {
            var copy = (Half[])last.Clone();
            Array.Clear(copy, count & _mask, copy.Length - (count & _mask));
            kept[chunks - 1] = copy;
        }

        return new HalfChunks(kept, count, _shift);
    }

    /// <summary>The first <paramref name="count"/> values, then <paramref name="more"/>: for a run read past the length it was made.</summary>
    public HalfChunks Extended(int count, IReadOnlyList<Half> more)
    {
        var extended = new HalfChunks(count + more.Count, _shift);
        Array.Copy(_chunks, extended._chunks, Math.Min(_chunks.Length, extended._chunks.Length));

        // The last chunk of these may be shared, and is written past its end: it's copied first.
        int last = (count - 1) >> _shift;
        if (count > 0 && extended._chunks[last] is { } shared)
            extended._chunks[last] = (Half[])shared.Clone();
        for (int i = 0; i < more.Count; i++)
            extended.Set(count + i, more[i]);
        return extended;
    }

    /// <summary>All of the values, for comparing.</summary>
    public Half[] ToArray()
    {
        var values = new Half[Count];
        for (int i = 0; i < Count; i++)
            values[i] = this[i];
        return values;
    }

    /// <summary>The values from <paramref name="first"/> up to <paramref name="end"/>, a chunk at a time: each stretch within one chunk, empty for a chunk that's all 0.</summary>
    /// <remarks>For hot loops, which skip the silent chunks whole.</remarks>
    public ChunkEnumerator Spans(int first, int end) => new(this, Math.Max(0, first), Math.Min(Count, end));

    /// <summary>A stretch of values within one chunk: <see cref="Start"/> is where it starts, and <see cref="Values"/> is empty when they're all 0.</summary>
    public readonly ref struct ChunkSpan(int start, int length, ReadOnlySpan<Half> values)
    {
        public int Start { get; } = start;

        public int Length { get; } = length;

        public ReadOnlySpan<Half> Values { get; } = values;

        public bool IsSilent => Values.IsEmpty;
    }

    public ref struct ChunkEnumerator(HalfChunks chunks, int first, int end)
    {
        private int _next = first;

        public ChunkSpan Current { get; private set; }

        public readonly ChunkEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            if (_next >= end)
                return false;

            int chunk = _next >> chunks._shift;
            int offset = _next & chunks._mask;
            int length = Math.Min(end - _next, chunks.ChunkLength - offset);
            var values = chunks.ChunkAt(chunk);
            Current = new ChunkSpan(_next, length, values is null ? default : values.AsSpan(offset, length));
            _next += length;
            return true;
        }
    }
}
