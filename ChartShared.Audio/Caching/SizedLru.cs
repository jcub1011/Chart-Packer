using System.Diagnostics.CodeAnalysis;

namespace ChartShared.Audio.Caching;

/// <summary>
/// What a cache keeps within a budget of bytes, in the order it was last used: the bookkeeping the audio caches share
/// (<see cref="Analysis.SpectrogramCache"/>, <see cref="Waveforms.StemPeaksCache"/>, <see cref="Decoding.DecodedAudioCache"/>).
/// Each entry counts at the size it was added with, until it's resized. Not thread-safe: the cache holding it locks.
/// </summary>
internal sealed class SizedLru<TKey, TValue>
    where TKey : notnull
{
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _entries = [];

    // Most recently used first.
    private readonly LinkedList<Entry> _order = new();

    /// <summary>How many bytes every entry takes together.</summary>
    public long Size { get; private set; }

    public int Count => _entries.Count;

    /// <summary>The values, most recently used first.</summary>
    public IEnumerable<TValue> Values => _order.Select(e => e.Value);

    /// <summary>The value kept under <paramref name="key"/>, counted as just used.</summary>
    public bool TryGet(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        if (_entries.TryGetValue(key, out var node))
        {
            if (!ReferenceEquals(_order.First, node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
            }

            value = node.Value.Value;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>The value kept under <paramref name="key"/>, without counting it as used.</summary>
    public bool TryPeek(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        if (_entries.TryGetValue(key, out var node))
        {
            value = node.Value.Value;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Keeps <paramref name="value"/> under <paramref name="key"/> as the most recently used, in place of any there.</summary>
    /// <returns>The value it replaced, if there was one.</returns>
    public TValue? Add(TKey key, TValue value, long size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        Remove(key, out var replaced);
        _entries[key] = _order.AddFirst(new Entry(key, value, size));
        Size += size;
        return replaced;
    }

    /// <summary>Counts the entry under <paramref name="key"/> at <paramref name="size"/> from now on.</summary>
    public void Resize(TKey key, long size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        if (!_entries.TryGetValue(key, out var node))
            return;
        Size += size - node.Value.Size;
        node.Value = node.Value with { Size = size };
    }

    public bool Remove(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        if (_entries.Remove(key, out var node))
        {
            _order.Remove(node);
            Size -= node.Value.Size;
            value = node.Value.Value;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Takes out every entry.</summary>
    /// <returns>Their values, to be let go of outside the lock.</returns>
    public List<TValue> Clear()
    {
        var removed = _order.Select(e => e.Value).ToList();
        _entries.Clear();
        _order.Clear();
        Size = 0;
        return removed;
    }

    /// <summary>
    /// Whether <paramref name="needed"/> more fits within <paramref name="budget"/> once every entry
    /// <paramref name="canRelease"/> allows is released. Nothing is released.
    /// </summary>
    /// <param name="canRelease">Which entries may be released; all of them when not given.</param>
    public bool CanFit(long needed, long budget, Func<TValue, bool>? canRelease = null)
    {
        long size = Size;
        for (var node = _order.Last; node is not null && size + needed > budget; node = node.Previous)
        {
            if (canRelease is null || canRelease(node.Value.Value))
                size -= node.Value.Size;
        }

        return size + needed <= budget;
    }

    /// <summary>
    /// Releases the least recently used that <paramref name="canRelease"/> allows until <paramref name="needed"/> more fits
    /// within <paramref name="budget"/>, or none it allows is left.
    /// </summary>
    /// <returns>What was released, to be let go of outside the lock.</returns>
    public List<TValue> MakeRoom(long needed, long budget, Func<TValue, bool>? canRelease = null)
    {
        var released = new List<TValue>();
        var node = _order.Last;
        while (node is not null && Size + needed > budget)
        {
            var previous = node.Previous;
            if (canRelease is null || canRelease(node.Value.Value))
            {
                _order.Remove(node);
                _entries.Remove(node.Value.Key);
                Size -= node.Value.Size;
                released.Add(node.Value.Value);
            }

            node = previous;
        }

        return released;
    }

    private sealed record Entry(TKey Key, TValue Value, long Size);
}
