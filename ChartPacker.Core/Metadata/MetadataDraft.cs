namespace ChartPacker.Core.Metadata;

/// <summary>A pending change to one metadata key.</summary>
/// <param name="Key">The key, as the user typed it.</param>
/// <param name="Value">The new value, or <see langword="null"/> when the key is to be removed.</param>
public readonly record struct MetadataChange(string Key, string? Value)
{
    /// <summary>True when the key is to be removed.</summary>
    public bool IsRemoval => Value is null;
}

/// <summary>A pending change to a song's album art.</summary>
/// <param name="SourcePath">The image to store as the album art, or <see langword="null"/> when the art is to be removed.</param>
public readonly record struct AlbumArtChange(string? SourcePath)
{
    /// <summary>True when the album art is to be removed.</summary>
    public bool IsRemoval => SourcePath is null;
}

/// <summary>A file to add to a song.</summary>
/// <param name="StoredName">The name to store it under, relative to the song.</param>
/// <param name="SourcePath">The file to copy in.</param>
/// <param name="Length">Its size when it was chosen, for display. Saving reads the size afresh.</param>
public readonly record struct AddedFile(string StoredName, string SourcePath, long Length);

/// <summary>
/// Unsaved changes to one song's metadata, held per key rather than as a copy of the metadata,
/// plus any change to its album art and to its other files.
/// </summary>
/// <remarks>
/// Keys and file names are matched ignoring case (sng-spec A7). Because only the changed keys are
/// recorded, <see cref="ApplyTo"/> can be run against whatever is on disk at save time: every key
/// the user didn't touch is carried over exactly as stored. Likewise only the removed and added
/// files are recorded; an added file whose name is already in the song replaces it.
/// </remarks>
public sealed class MetadataDraft
{
    // Insertion order matters: keys added by the draft are appended in the order they were added.
    private readonly List<string> _order = [];
    private readonly Dictionary<string, MetadataChange> _changes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _removedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AddedFile> _addedFiles = [];

    // The removals an add made, by the added name, so that dropping the add drops them too.
    private readonly Dictionary<string, List<string>> _replacedBy = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when there are no pending changes, to the metadata, the album art or the files.</summary>
    public bool IsEmpty => _changes.Count == 0 && AlbumArt is null && !HasFileChanges;

    /// <summary>True when a metadata key has a pending change. The album art and files don't count.</summary>
    public bool HasMetadataChanges => _changes.Count > 0;

    /// <summary>True when a file is to be removed or added. The album art doesn't count.</summary>
    public bool HasFileChanges => _removedFiles.Count > 0 || _addedFiles.Count > 0;

    /// <summary>The pending change to the album art, if there is one.</summary>
    public AlbumArtChange? AlbumArt { get; private set; }

    /// <summary>The names of the files to remove, relative to the song.</summary>
    public IReadOnlyCollection<string> RemovedFiles => _removedFiles;

    /// <summary>The files to add, in the order they were added.</summary>
    public IReadOnlyList<AddedFile> AddedFiles => _addedFiles;

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

    /// <summary>Replaces the album art with the image at <paramref name="sourcePath"/>, replacing any earlier art change.</summary>
    public void SetAlbumArt(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        AlbumArt = new AlbumArtChange(sourcePath);
    }

    /// <summary>Marks the album art for removal, replacing any earlier art change.</summary>
    public void RemoveAlbumArt() => AlbumArt = new AlbumArtChange(null);

    /// <summary>Forgets any change to the album art.</summary>
    public void RevertAlbumArt() => AlbumArt = null;

    /// <summary>Stages <paramref name="file"/> to be added, replacing any earlier add under the same name.</summary>
    public void AddFile(AddedFile file)
    {
        ArgumentException.ThrowIfNullOrEmpty(file.StoredName);
        ArgumentException.ThrowIfNullOrEmpty(file.SourcePath);

        int index = FindAdded(file.StoredName);
        if (index >= 0)
            _addedFiles[index] = file;
        else
            _addedFiles.Add(file);
    }

    /// <summary>
    /// Stages <paramref name="file"/> to be added in place of the files <paramref name="replaces"/>,
    /// which are marked for removal. Reverting the add unmarks them again, except those that were
    /// already marked before it.
    /// </summary>
    public void AddFile(AddedFile file, IEnumerable<string> replaces)
    {
        ArgumentNullException.ThrowIfNull(replaces);
        AddFile(file);

        foreach (string name in replaces)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);
            if (!_removedFiles.Add(name))
                continue;

            if (!_replacedBy.TryGetValue(file.StoredName, out var replaced))
                _replacedBy[file.StoredName] = replaced = [];
            replaced.Add(name);
        }
    }

    /// <summary>Marks the file <paramref name="name"/> for removal.</summary>
    public void RemoveFile(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _removedFiles.Add(name);

        // Asked for in its own right, so it outlasts any add that also removed it.
        ForgetReplaced(name);
    }

    /// <summary>
    /// Forgets any removal or add of the file <paramref name="name"/>. Forgetting an add also
    /// forgets the removals it made; see <see cref="AddFile(AddedFile, IEnumerable{string})"/>.
    /// </summary>
    public void RevertFile(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (_removedFiles.Remove(name))
            ForgetReplaced(name);

        int index = FindAdded(name);
        if (index >= 0)
        {
            _addedFiles.RemoveAt(index);
            if (_replacedBy.Remove(name, out var replaced))
            {
                foreach (string removed in replaced)
                    _removedFiles.Remove(removed);
            }
        }
    }

    /// <summary>Whether the file <paramref name="name"/> is marked for removal.</summary>
    public bool IsFileRemoved(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _removedFiles.Contains(name);
    }

    /// <summary>Whether a file is staged to be added as <paramref name="name"/>.</summary>
    public bool IsFileAdded(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return FindAdded(name) >= 0;
    }

    /// <summary>Forgets every change.</summary>
    public void Clear()
    {
        _changes.Clear();
        _order.Clear();
        AlbumArt = null;
        _removedFiles.Clear();
        _addedFiles.Clear();
        _replacedBy.Clear();
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

    private int FindAdded(string name) =>
        _addedFiles.FindIndex(f => f.StoredName.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Stops any add counting <paramref name="name"/> among the removals it made.</summary>
    private void ForgetReplaced(string name)
    {
        foreach (var replaced in _replacedBy.Values)
            replaced.RemoveAll(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private void Record(MetadataChange change)
    {
        if (!_changes.ContainsKey(change.Key))
            _order.Add(change.Key);
        _changes[change.Key] = change;
    }
}
