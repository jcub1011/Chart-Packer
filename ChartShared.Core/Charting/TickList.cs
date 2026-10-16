using System.Collections;
using System.Runtime.InteropServices;

namespace ChartShared.Core.Charting;

/// <summary>
/// A list of a chart's items kept in tick order, for the editable model. Anyone can read it; only the model and its
/// commands, in this assembly, can change it, and every change keeps the order.
/// </summary>
/// <remarks>
/// Items are ordered by an order whose first key is the tick. Items the order ranks equal keep the order they were
/// added in, unless the list is unique, when it holds at most one item at each place in the order (one note per tick,
/// lane and kick, for example). Finding, adding and removing are binary searches.
/// </remarks>
/// <typeparam name="T">The items: immutable values, so a copy of the list shares nothing that can change.</typeparam>
public sealed class TickList<T> : IReadOnlyList<T>
{
    private readonly List<T> _items;
    private readonly Func<T, long> _tickOf;
    private readonly Comparison<T> _order;
    private readonly bool _unique;
    private readonly Action<T>? _check;

    /// <param name="tickOf">An item's tick.</param>
    /// <param name="order">The order, whose first key must be the tick; the tick alone when null.</param>
    /// <param name="unique">Whether the list holds at most one item at each place in <paramref name="order"/>.</param>
    /// <param name="check">Throws when an item can't be added, such as a negative tick.</param>
    internal TickList(Func<T, long> tickOf, Comparison<T>? order = null, bool unique = false, Action<T>? check = null)
    {
        _items = [];
        _tickOf = tickOf;
        _order = order ?? ((a, b) => tickOf(a).CompareTo(tickOf(b)));
        _unique = unique;
        _check = check;
    }

    private TickList(TickList<T> source)
    {
        _items = [.. source._items];
        _tickOf = source._tickOf;
        _order = source._order;
        _unique = source._unique;
        _check = source._check;
    }

    public int Count => _items.Count;

    public T this[int index] => _items[index];

    /// <summary>
    /// Goes up by one on every change, so a cache built from the list can tell it's out of date. A copy starts again
    /// at 0.
    /// </summary>
    internal int Version { get; private set; }

    /// <summary>The items, in order. Valid only until the list next changes.</summary>
    public ReadOnlySpan<T> AsSpan() => CollectionsMarshal.AsSpan(_items);

    /// <summary>
    /// The items from <paramref name="fromTick"/> up to and including <paramref name="toTick"/>, in order. Valid only
    /// until the list next changes.
    /// </summary>
    public ReadOnlySpan<T> Between(long fromTick, long toTick)
    {
        int start = FirstAtOrAfter(fromTick);
        int end = FirstAfter(toTick);
        return end > start ? AsSpan()[start..end] : [];
    }

    /// <summary>The items at <paramref name="tick"/>, in order. Valid only until the list next changes.</summary>
    public ReadOnlySpan<T> At(long tick) => Between(tick, tick);

    /// <summary>The index of the first item at or after <paramref name="tick"/>, or <see cref="Count"/> when there's none.</summary>
    public int FirstAtOrAfter(long tick)
    {
        int lo = 0, hi = _items.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_tickOf(_items[mid]) < tick)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    /// <summary>The index of the first item after <paramref name="tick"/>, or <see cref="Count"/> when there's none.</summary>
    public int FirstAfter(long tick)
    {
        int lo = 0, hi = _items.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_tickOf(_items[mid]) <= tick)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    public List<T>.Enumerator GetEnumerator() => _items.GetEnumerator();

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>The tick of <paramref name="item"/>.</summary>
    internal long TickOf(T item) => _tickOf(item);

    /// <summary>Where <paramref name="a"/> comes against <paramref name="b"/> in the list's order.</summary>
    internal int Compare(T a, T b) => _order(a, b);

    /// <summary>
    /// The index of the first item at <paramref name="probe"/>'s place in the order, or, when there's none, the bitwise
    /// complement of the index it would go at.
    /// </summary>
    internal int IndexOfPlace(T probe)
    {
        int lo = LowerBound(probe);
        return lo < _items.Count && _order(_items[lo], probe) == 0 ? lo : ~lo;
    }

    /// <summary>
    /// Adds <paramref name="item"/> after the items it ranks equal with. A unique list that already has an item at its
    /// place is left as it is.
    /// </summary>
    /// <returns>Whether it was added.</returns>
    /// <exception cref="ArgumentException">The item can't be in this list.</exception>
    internal bool Add(T item) => Place(item) >= 0;

    /// <summary>Adds <paramref name="item"/> as <see cref="Add"/> does.</summary>
    /// <returns>The index it went at, for undoing with <see cref="RemoveAt"/>; -1 when it wasn't added.</returns>
    /// <exception cref="ArgumentException">The item can't be in this list.</exception>
    internal int Place(T item)
    {
        _check?.Invoke(item);
        int index;
        if (_unique)
        {
            index = LowerBound(item);
            if (index < _items.Count && _order(_items[index], item) == 0)
                return -1;
        }
        else
        {
            index = UpperBound(item);
        }

        _items.Insert(index, item);
        Version++;
        return index;
    }

    /// <summary>
    /// Puts <paramref name="item"/> at <paramref name="index"/>, where it was before <see cref="RemoveAt"/> took it out,
    /// so that undoing a removal keeps its place among the items it ranks equal with.
    /// </summary>
    /// <exception cref="ArgumentException">The item can't be in this list.</exception>
    /// <exception cref="InvalidOperationException">The item doesn't belong at <paramref name="index"/>.</exception>
    internal void Insert(int index, T item)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, _items.Count);
        _check?.Invoke(item);
        int before = index > 0 ? _order(_items[index - 1], item) : -1;
        int after = index < _items.Count ? _order(item, _items[index]) : -1;
        if (before > 0 || after > 0 || (_unique && (before == 0 || after == 0)))
            throw new InvalidOperationException($"{item} doesn't belong at index {index} of the list.");

        _items.Insert(index, item);
        Version++;
    }

    /// <summary>Checks that <paramref name="item"/> can be in this list, without adding it.</summary>
    /// <exception cref="ArgumentException">It can't.</exception>
    internal void Check(T item) => _check?.Invoke(item);

    /// <summary>The index of the first item equal to <paramref name="item"/>, or -1 when there's none.</summary>
    internal int IndexOf(T item)
    {
        var comparer = EqualityComparer<T>.Default;
        for (int i = LowerBound(item); i < _items.Count && _order(_items[i], item) == 0; i++)
        {
            if (comparer.Equals(_items[i], item))
                return i;
        }

        return -1;
    }

    /// <summary>Removes the first item equal to <paramref name="item"/>.</summary>
    /// <returns>Whether there was one.</returns>
    internal bool Remove(T item)
    {
        int index = IndexOf(item);
        if (index < 0)
            return false;
        RemoveAt(index);
        return true;
    }

    internal void RemoveAt(int index)
    {
        _items.RemoveAt(index);
        Version++;
    }

    internal void Clear()
    {
        _items.Clear();
        Version++;
    }

    /// <summary>A copy that changes independently of this list. The items themselves are shared, being values.</summary>
    internal TickList<T> Clone() => new(this);

    // The first item not before item in the order.
    private int LowerBound(T item)
    {
        int lo = 0, hi = _items.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_order(_items[mid], item) < 0)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    // The first item after item in the order.
    private int UpperBound(T item)
    {
        int lo = 0, hi = _items.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_order(_items[mid], item) <= 0)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }
}
