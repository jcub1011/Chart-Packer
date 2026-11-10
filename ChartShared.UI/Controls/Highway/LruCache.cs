using System;
using System.Collections.Generic;

namespace ChartShared.UI.Controls.Highway
{
    /// <summary>
    /// What a highway makes once and keeps to draw again, up to a number of them: past that, the one used longest ago goes, disposed,
    /// to make room for each new one. Not all of them at once, which would have every one in view made again on the next frame, together.
    /// </summary>
    /// <remarks>A hit only moves the entry to the front of the list, so a frame that draws what's kept makes nothing to collect.</remarks>
    public sealed class LruCache<TKey, TValue>
        where TKey : notnull
        where TValue : class
    {
        private readonly int _capacity;
        private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _entries = [];
        private readonly LinkedList<(TKey Key, TValue Value)> _order = new();

        /// <param name="capacity">How many are kept at most.</param>
        public LruCache(int capacity)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
            _capacity = capacity;
        }

        /// <summary>How many are kept now.</summary>
        public int Count => _entries.Count;

        /// <summary>The value kept for <paramref name="key"/>, now the most recently used; false when there is none.</summary>
        public bool TryGetValue(TKey key, out TValue value)
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

            value = null!;
            return false;
        }

        /// <summary>Keeps <paramref name="value"/> for <paramref name="key"/>, which has none yet, letting the least recently used go if there's no room.</summary>
        public TValue Add(TKey key, TValue value)
        {
            while (_entries.Count >= _capacity && _order.Last is { } oldest)
            {
                _order.RemoveLast();
                _entries.Remove(oldest.Value.Key);
                (oldest.Value.Value as IDisposable)?.Dispose();
            }

            _entries[key] = _order.AddFirst((key, value));
            return value;
        }
    }
}
