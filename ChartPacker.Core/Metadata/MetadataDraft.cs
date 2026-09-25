namespace ChartPacker.Core.Metadata;

/// <summary>A pending change to one metadata key.</summary>
/// <param name="Key">The key, as the user typed it.</param>
/// <param name="Value">The new value, or <see langword="null"/> when the key is to be removed.</param>
public readonly record struct MetadataChange(string Key, string? Value)
{
    /// <summary>True when the key is to be removed.</summary>
    public bool IsRemoval => Value is null;
}

/// <summary>
/// Unsaved changes to one song's metadata, held per key rather than as a copy of the metadata.
/// </summary>
/// <remarks>
/// Keys are matched ignoring case (sng-spec A7). Because only the changed keys are recorded,
/// <see cref="ApplyTo"/> can be run against whatever is on disk at save time: every key the user
/// didn't touch is carried over exactly as stored.
/// </remarks>
public sealed class MetadataDraft
{
    // Insertion order matters: keys added by the draft are appended in the order they were added.
    private readonly List<string> _order = [];
    private readonly Dictionary<string, MetadataChange> _changes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when there are no pending changes.</summary>
    public bool IsEmpty => _changes.Count == 0;

    /// <summary>The changed keys, in the order they were first changed.</summary>
    public IReadOnlyList<string> Keys => _order;

    /// <summary>The pending changes, in the order their keys were first changed.</summary>
    public IEnumerable<MetadataChange> Changes => _order.Select(k => _changes[k]);

    /// <summary>Sets <paramref name="key"/> to <paramref name="value"/>, replacing any earlier change to it.</summary>
    public void Set(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        Record(new MetadataChange(key, value));
    }

    /// <summary>Marks <paramref name="key"/> for removal, replacing any earlier change to it.</summary>
    public void Remove(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        Record(new MetadataChange(key, null));
    }

    /// <summary>Forgets any change to <paramref name="key"/>.</summary>
    public void Revert(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_changes.Remove(key))
            _order.RemoveAt(_order.FindIndex(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Forgets every change.</summary>
    public void Clear()
    {
        _changes.Clear();
        _order.Clear();
    }

    /// <summary>Gets the pending change to <paramref name="key"/>, if there is one.</summary>
    public bool TryGetChange(string key, out MetadataChange change)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _changes.TryGetValue(key, out change);
    }

    /// <summary>Returns <paramref name="pairs"/> with the changes applied.</summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>A set key replaces the value of its first occurrence, keeping the stored key's case and
    /// position. Later duplicates of it are dropped, since they would contradict the new value.</item>
    /// <item>A removed key is dropped, along with all its duplicates.</item>
    /// <item>A set key that isn't present is appended, in the order it was added to the draft.</item>
    /// <item>Every other pair, duplicates included, is returned unchanged and in order.</item>
    /// </list>
    /// </remarks>
    public List<KeyValuePair<string, string>> ApplyTo(IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);

        var result = new List<KeyValuePair<string, string>>(pairs.Count + _changes.Count);
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in pairs)
        {
            if (!_changes.TryGetValue(pair.Key, out var change))
            {
                result.Add(pair);
                continue;
            }

            if (change.Value is { } value && written.Add(pair.Key))
                result.Add(new KeyValuePair<string, string>(pair.Key, value));
        }

        foreach (string key in _order)
        {
            if (_changes[key].Value is { } value && !written.Contains(key))
                result.Add(new KeyValuePair<string, string>(key, value));
        }

        return result;
    }

    private void Record(MetadataChange change)
    {
        if (!_changes.ContainsKey(change.Key))
            _order.Add(change.Key);
        _changes[change.Key] = change;
    }
}
