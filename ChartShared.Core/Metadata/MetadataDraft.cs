using ChartShared.Core.Charts;
using ChartShared.Core.Scanning;

namespace ChartShared.Core.Metadata;

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

/// <summary>A pending change to a song's background image or background video.</summary>
/// <param name="SourcePath">The file to store as the background, or <see langword="null"/> when the background is to be removed.</param>
public readonly record struct BackgroundChange(string? SourcePath)
{
    /// <summary>True when the background is to be removed.</summary>
    public bool IsRemoval => SourcePath is null;
}

/// <summary>A pending change to a song's vocals: its chart's <c>PART VOCALS</c> and harmonies.</summary>
/// <param name="Source">
/// The chart or <c>.sng</c> to take the vocals from, as it was read when it was chosen, or
/// <see langword="null"/> when the vocals are to be removed. Saving writes these vocals, not the file's.
/// </param>
public readonly record struct VocalsChange(Merging.VocalsSource? Source)
{
    /// <summary>Where the vocals come from, or <see langword="null"/> when they're to be removed.</summary>
    public string? SourcePath => Source?.FilePath;

    /// <summary>True when the vocals are to be removed.</summary>
    public bool IsRemoval => Source is null;
}

/// <summary>A file to add to a song.</summary>
/// <param name="StoredName">The name to store it under, relative to the song.</param>
/// <param name="SourcePath">The file to copy in.</param>
/// <param name="Length">Its size when it was chosen, for display. Saving reads the size afresh.</param>
public readonly record struct AddedFile(string StoredName, string SourcePath, long Length);

/// <summary>
/// Unsaved changes to one song's metadata, held per key rather than as a copy of the metadata,
/// plus any change to its album art, its background image and video, its other files, and its chart's vocals and timing.
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

    private readonly Dictionary<ChartTrackKey, int> _chartOffsets = [];

    /// <summary>True when there are no pending changes, to the metadata, the album art, the backgrounds, the files or the chart's vocals or timing.</summary>
    public bool IsEmpty => _changes.Count == 0 && AlbumArt is null && !HasBackgroundChanges && !HasFileChanges && !HasChartEdits;

    /// <summary>True when a metadata key has a pending change. The album art, backgrounds and files don't count.</summary>
    public bool HasMetadataChanges => _changes.Count > 0;

    /// <summary>True when a file is to be removed or added. The album art and backgrounds don't count.</summary>
    public bool HasFileChanges => _removedFiles.Count > 0 || _addedFiles.Count > 0;

    /// <summary>True when some of the chart's notes are to be moved.</summary>
    public bool HasChartOffsets => _chartOffsets.Count > 0;

    /// <summary>True when the chart's vocals are to be moved.</summary>
    public bool HasVocalsOffset => VocalsOffset != 0;

    /// <summary>True when saving rewrites the chart the song ends up with: to move some of its notes, or to change or move its vocals.</summary>
    public bool HasChartEdits => HasChartOffsets || Vocals is not null || HasVocalsOffset;

    /// <summary>
    /// How far to move the chart's vocals, in milliseconds, later when positive: the vocals it has,
    /// or those a <see cref="Vocals"/> change gives it. Saving rewrites the chart; see <see cref="Merging.VocalsEditor"/>.
    /// </summary>
    public int VocalsOffset { get; private set; }

    /// <summary>
    /// The pending change to the chart's vocals, if there is one. Saving applies it to the chart the
    /// song ends up with; see <see cref="Merging.VocalsEditor"/>.
    /// </summary>
    public VocalsChange? Vocals { get; private set; }

    /// <summary>
    /// How far to move each instrument difficulty's notes, in milliseconds, later when positive.
    /// Saving rewrites the chart the song ends up with; see <see cref="ChartShifter"/>.
    /// </summary>
    public IReadOnlyDictionary<ChartTrackKey, int> ChartOffsets => _chartOffsets;

    /// <summary>True when the background image or video has a pending change.</summary>
    public bool HasBackgroundChanges => BackgroundImage is not null || BackgroundVideo is not null;

    /// <summary>The pending change to the album art, if there is one.</summary>
    public AlbumArtChange? AlbumArt { get; private set; }

    /// <summary>The pending change to the background image, if there is one.</summary>
    public BackgroundChange? BackgroundImage { get; private set; }

    /// <summary>The pending change to the background video, if there is one.</summary>
    public BackgroundChange? BackgroundVideo { get; private set; }

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

    /// <summary>The pending change to the background of <paramref name="kind"/>, if there is one.</summary>
    public BackgroundChange? GetBackground(BackgroundKind kind) => kind == BackgroundKind.Image ? BackgroundImage : BackgroundVideo;

    /// <summary>Replaces the background of <paramref name="kind"/> with the file at <paramref name="sourcePath"/>, replacing any earlier change to it.</summary>
    public void SetBackground(BackgroundKind kind, string sourcePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        SetBackgroundChange(kind, new BackgroundChange(sourcePath));
    }

    /// <summary>Marks the background of <paramref name="kind"/> for removal, replacing any earlier change to it.</summary>
    public void RemoveBackground(BackgroundKind kind) => SetBackgroundChange(kind, new BackgroundChange(null));

    /// <summary>Forgets any change to the background of <paramref name="kind"/>.</summary>
    public void RevertBackground(BackgroundKind kind) => SetBackgroundChange(kind, null);

    private void SetBackgroundChange(BackgroundKind kind, BackgroundChange? change)
    {
        if (kind == BackgroundKind.Image)
            BackgroundImage = change;
        else
            BackgroundVideo = change;
    }

    /// <summary>Moves the notes of <paramref name="track"/> by <paramref name="milliseconds"/>, replacing any earlier offset for it; zero forgets it.</summary>
    public void SetChartOffset(ChartTrackKey track, int milliseconds)
    {
        if (milliseconds == 0)
            _chartOffsets.Remove(track);
        else
            _chartOffsets[track] = milliseconds;
    }

    /// <summary>Moves the chart's vocals by <paramref name="milliseconds"/>, replacing any earlier offset for them; zero forgets it.</summary>
    public void SetVocalsOffset(int milliseconds) => VocalsOffset = milliseconds;

    /// <summary>Forgets every chart offset, the vocals' too, as when the chart they were set for is replaced or removed.</summary>
    public void ClearChartOffsets()
    {
        _chartOffsets.Clear();
        VocalsOffset = 0;
    }

    /// <summary>Replaces the chart's vocals with those of <paramref name="source"/>, replacing any earlier vocals change.</summary>
    public void SetVocals(Merging.VocalsSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Vocals = new VocalsChange(source);
    }

    /// <summary>Marks the chart's vocals for removal, replacing any earlier vocals change.</summary>
    public void RemoveVocals() => Vocals = new VocalsChange(null);

    /// <summary>Forgets any change to the chart's vocals.</summary>
    public void RevertVocals() => Vocals = null;

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
        BackgroundImage = null;
        BackgroundVideo = null;
        Vocals = null;
        VocalsOffset = 0;
        _removedFiles.Clear();
        _addedFiles.Clear();
        _replacedBy.Clear();
        _chartOffsets.Clear();
    }

    /// <summary>A copy of every change, which later changes to either draft don't reach.</summary>
    /// <remarks>For work off the UI thread, which a draft the user can still change mustn't be read by.</remarks>
    public MetadataDraft Clone()
    {
        var copy = new MetadataDraft
        {
            AlbumArt = AlbumArt,
            BackgroundImage = BackgroundImage,
            BackgroundVideo = BackgroundVideo,
            Vocals = Vocals,
            VocalsOffset = VocalsOffset,
        };
        copy._order.AddRange(_order);
        foreach (var (key, change) in _changes)
            copy._changes[key] = change;
        copy._removedFiles.UnionWith(_removedFiles);
        copy._addedFiles.AddRange(_addedFiles);
        foreach (var (name, replaced) in _replacedBy)
            copy._replacedBy[name] = [.. replaced];
        foreach (var (track, milliseconds) in _chartOffsets)
            copy._chartOffsets[track] = milliseconds;
        return copy;
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
