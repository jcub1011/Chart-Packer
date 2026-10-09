using System.Globalization;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Scanning;

namespace ChartPacker.Core.Charting;

/// <summary>
/// What the Editor has open (charter-spec §4.5): a song opened from a folder or <c>.sng</c> (C2), or a new chart that
/// hasn't been saved yet (C1), which has no folder and plays its audio from the files it was given.
/// </summary>
/// <remarks>
/// It belongs to the editing thread, as the chart does. A save moves it on (<see cref="Saved"/>): its <c>song.ini</c>
/// is then the one saved, and a new chart becomes a song opened from its folder. The pending <c>song.ini</c> changes are
/// worked out from it and the chart as they are (<see cref="PendingSongIniChanges"/>), less those the user reverted.
/// </remarks>
public sealed class EditorSong
{
    private readonly HashSet<string> _reverted = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<KeyValuePair<string, string?>> _edits = [];
    private IReadOnlyList<KeyValuePair<string, string>> _values = [];
    private long? _audioLengthMs;
    private long? _delayMs;
    private string _defaultCharter = "";
    private readonly Dictionary<string, int> _ratings = new(StringComparer.OrdinalIgnoreCase);

    // A new chart's tracks as it was given them, which its track changes are made over until it's first saved.
    private readonly IReadOnlyList<SongTrack> _newTracks;
    private readonly Dictionary<MediaSlot, MediaChange> _media = [];

    // The save from which each slot holds what it's stored with now; none, from the first.
    private readonly Dictionary<MediaSlot, int> _storedSince = [];

    // The session's copy of each file a save replaced or removed, and the saves it was the slot's file through.
    private readonly List<KeptMedia> _kept = [];
    private IReadOnlyList<SongTrack> _tracks;

    private EditorSong(OpenedSong? opened, EditableChart chart, IReadOnlyList<SongTrack> tracks, IReadOnlySet<ChartInstrument> loadedParts)
    {
        Opened = opened;
        Chart = chart;
        _newTracks = opened is null ? tracks : [];
        _tracks = tracks;
        LoadedParts = loadedParts;
    }

    /// <summary>
    /// Raised when anything the pending changes, the delay or the title come from changes, other than the chart: a save,
    /// a revert or restore, the audio's length, a value or the default charter.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>Raised after <see cref="Changed"/> when <see cref="Tracks"/> changed: a track added, replaced or removed, or a save.</summary>
    public event EventHandler? TracksChanged;

    /// <summary>The song as it was opened or last saved, or null for a new chart not yet saved.</summary>
    public OpenedSong? Opened { get; private set; }

    /// <summary>Whether it's a new chart, which has no folder until it's first saved.</summary>
    public bool IsNew => Opened is null;

    /// <summary>The chart, in the editable model.</summary>
    public EditableChart Chart { get; }

    /// <summary>
    /// The song's title, as the Editor's title shows it: "Artist – Name" as the next save leaves them, or whichever of
    /// them it has, or else the song's file or folder name; null for a new chart with neither.
    /// </summary>
    public string? Title
    {
        get
        {
            var pairs = EffectivePairs();
            string? Get(string key) => SongIniValues.Last(pairs, key)?.Trim() is { Length: > 0 } value ? value : null;
            return (Get("artist"), Get("name")) switch
            {
                ({ } artist, { } name) => $"{artist} – {name}",
                (null, { } name) => name,
                ({ } artist, null) => artist,
                _ => Opened?.Entry.DisplayName,
            };
        }
    }

    /// <summary>The folder or <c>.sng</c> the song is in; null for a new chart.</summary>
    public SongEntry? Entry => Opened?.Entry;

    /// <summary>The song's <c>song.ini</c> as it was opened or last saved; none for a new chart.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Pairs => Opened?.Pairs ?? [];

    /// <summary>
    /// For a <c>notes.chart</c> not yet saved as a <c>notes.mid</c>, what converting it gave: the <c>Offset</c> and
    /// <c>[Song]</c> values its save carries into <c>song.ini</c>.
    /// </summary>
    public ChartImportResult? Import => Opened?.Import;

    /// <summary>
    /// Whether the song's chart file is a <c>notes.chart</c>, which the first save replaces with a <c>notes.mid</c>
    /// (charter-spec C2 item 3).
    /// </summary>
    public bool IsImport => Opened?.Import is not null;

    /// <summary>
    /// The audio tracks as the next save leaves them: the song's stems, or a new chart's files, with the tracks added,
    /// replaced or removed since; each played from where it is, a staged one from the file it's copied from.
    /// </summary>
    public IReadOnlyList<SongTrack> Tracks => _tracks;

    /// <summary>The parts the chart had when it was opened or last saved, which need no rating added; none for a new chart.</summary>
    public IReadOnlySet<ChartInstrument> LoadedParts { get; private set; }

    /// <summary>How many things in the chart were left out as it was read; none for a new chart.</summary>
    public int LoadIssueCount => Opened?.Load.Issues.Count ?? 0;

    /// <summary>The keys of the pending changes the user has reverted, which saves leave out until they're restored.</summary>
    public IReadOnlySet<string> Reverted => _reverted;

    /// <summary>The longest stem's length in milliseconds, once it's been measured.</summary>
    public long? AudioLengthMs
    {
        get => _audioLengthMs;
        set
        {
            if (_audioLengthMs == value)
                return;
            _audioLengthMs = value;
            OnChanged();
        }
    }

    /// <summary>
    /// The <c>delay</c> Find first downbeat set, in milliseconds, so the chart starts on the downbeat (charter-spec C14
    /// item 5); null for none. It's a pending change, which Revert takes back, and is cleared once saved. Setting it
    /// takes the place of an edit made to <c>delay</c>.
    /// </summary>
    public long? DelayMs
    {
        get => _delayMs;
        set
        {
            bool edited = value is not null && (RemoveEdit("delay") | RemoveEdit("delay_seconds"));
            if (_delayMs == value && !edited)
                return;
            _delayMs = value;
            OnChanged();
        }
    }

    /// <summary>
    /// The <c>song.ini</c> keys the user edited in the Chart Inspector, in the order first edited, each with its new
    /// value or null when it's removed. They're pending changes until saved, and Revert takes each back.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string?>> Edits => _edits;

    /// <summary>The album art the next save puts in place of the song's, or removes; null when it's left as it is.</summary>
    public AlbumArtChange? AlbumArt => _media.TryGetValue(MediaSlot.AlbumArt, out var change) ? new AlbumArtChange(change.SourcePath) : null;

    /// <summary>The files the next save puts in place of the song's, or removes, by slot: art, backgrounds and tracks.</summary>
    public IReadOnlyDictionary<MediaSlot, MediaChange> Media => _media;

    /// <summary>How many times the song has been saved since it was opened, which <see cref="MediaRef.Stored"/> counts by.</summary>
    public int SaveGeneration { get; private set; }

    /// <summary>
    /// Whether the user has changed the song itself, beside the chart, since it was opened or last saved: an edit,
    /// album art, a background, a track, a delay or a rating. Changes Chart Builder works out on its own don't count.
    /// </summary>
    public bool HasUserChanges => _edits.Count > 0 || _media.Count > 0 || _delayMs is not null || _ratings.Count > 0;

    /// <summary>The song's stored files in <paramref name="slot"/>, as it was opened or last saved; none for a new chart.</summary>
    public IReadOnlyList<string> StoredFilesOf(MediaSlot slot) => Opened is { } opened ? [.. opened.StoredNames.Where(slot.Holds)] : [];

    /// <summary>What <paramref name="slot"/> holds as the next save leaves it.</summary>
    public MediaRef EffectiveMedia(MediaSlot slot) => _media.TryGetValue(slot, out var change)
        ? change.SourcePath is { } path ? MediaRef.Source(path) : MediaRef.None
        : BaseMedia(slot);

    /// <summary>
    /// Makes the next save leave <paramref name="slot"/> holding <paramref name="target"/>: no change when that's what the
    /// song holds already, and otherwise the file to copy in, or a removal. A file the song had stored before a save that
    /// has since replaced it is copied back in from the session's copy. For undoable edits (<see cref="Commands.MediaCommand"/>).
    /// </summary>
    internal void SetEffectiveMedia(MediaSlot slot, MediaRef target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var resolved = Resolve(slot, target);
        MediaChange? change = resolved == BaseMedia(slot) ? null : resolved switch
        {
            MediaRef.SourceRef source => new MediaChange(source.Path),
            _ => new MediaChange(null),
        };
        SetMedia(slot, change);
    }

    /// <summary>
    /// Whether <paramref name="key"/> can be edited: not when the chart decides it, as for the drum layout keys, an
    /// import's <c>sustain_cutoff_threshold</c> and <c>multiplier_note</c>, which would save a chart that plays
    /// differently from what the editor shows.
    /// </summary>
    public bool CanEdit(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return !PendingSongIniChanges.IsChartOwned(key, Chart, PendingInputs);
    }

    /// <summary>Sets <paramref name="key"/> to <paramref name="value"/>, a pending change until saved; not an undo step.</summary>
    public void SetValue(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        Edit(key, value);
    }

    /// <summary>Removes <paramref name="key"/> from <c>song.ini</c>, a pending change until saved; not an undo step.</summary>
    public void RemoveValue(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        Edit(key, null);
    }

    /// <summary>
    /// Sets <c>delay</c> to <paramref name="ms"/>, and when that's 0, removes <c>delay_seconds</c>, which the games read
    /// in place of a <c>delay</c> of 0; a pending change until saved, not an undo step.
    /// </summary>
    public void SetDelay(long ms)
    {
        bool changed = SetEdit("delay", ms.ToString(CultureInfo.InvariantCulture));
        if (ms == 0 && !string.IsNullOrWhiteSpace(SongIniValues.Last(Pairs, "delay_seconds")))
            changed |= SetEdit("delay_seconds", null);
        else
            changed |= RemoveEdit("delay_seconds");
        if (changed)
            OnChanged();
    }

    /// <summary>Takes back the edit to <paramref name="key"/>, if there is one; not an undo step.</summary>
    public void DiscardEdit(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (RemoveEdit(key))
            OnChanged();
    }

    /// <summary>Puts the image at <paramref name="sourcePath"/> in place of the song's album art at the next save; not an undo step.</summary>
    public void SetAlbumArt(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        SetMedia(MediaSlot.AlbumArt, new MediaChange(sourcePath));
    }

    /// <summary>Removes the song's album art at the next save; not an undo step.</summary>
    public void RemoveAlbumArt() => SetMedia(MediaSlot.AlbumArt, new MediaChange(null));

    /// <summary>Leaves the song's album art as it is; not an undo step.</summary>
    public void RevertAlbumArt() => SetMedia(MediaSlot.AlbumArt, null);

    /// <summary>Sets each of <paramref name="edits"/> and the file changes <paramref name="media"/>, as a recovered chart had them.</summary>
    public void SetEdits(IEnumerable<KeyValuePair<string, string?>> edits, IEnumerable<KeyValuePair<MediaSlot, MediaChange>> media)
    {
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(media);
        foreach (var (key, value) in edits)
            SetEdit(key, value);
        foreach (var (slot, change) in media)
            _media[slot] = change;
        OnChanged();
        RefreshTracks();
    }

    /// <summary>
    /// The suggested ratings the user chose to use (charter-spec C18 item 3), by <c>diff_*</c> key: pending changes, which
    /// Revert takes back, cleared once saved.
    /// </summary>
    public IReadOnlyDictionary<string, int> Ratings => _ratings;

    /// <summary>
    /// Uses <paramref name="tier"/> as <paramref name="key"/>'s rating, a pending change until saved, in place of an edit
    /// made to it; not an undo step.
    /// </summary>
    public void SetRating(string key, int tier)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentOutOfRangeException.ThrowIfNegative(tier);
        bool restored = _reverted.Remove(key);
        bool edited = RemoveEdit(key);
        if (_ratings.TryGetValue(key, out int was) && was == tier && !restored && !edited)
            return;
        _ratings[key] = tier;
        OnChanged();
    }

    /// <summary>Sets each of <paramref name="ratings"/>, as a recovered chart had them.</summary>
    public void SetRatings(IEnumerable<KeyValuePair<string, int>> ratings)
    {
        ArgumentNullException.ThrowIfNull(ratings);
        foreach (var (key, tier) in ratings)
            _ratings[key] = tier;
        OnChanged();
    }

    /// <summary>The Default charter name setting, which fills an empty <c>charter</c>.</summary>
    public string DefaultCharter
    {
        get => _defaultCharter;
        set
        {
            value ??= "";
            if (_defaultCharter == value)
                return;
            _defaultCharter = value;
            OnChanged();
        }
    }

    /// <summary>Values the song has nowhere else, written where <c>song.ini</c> has none: a new chart's name and artist.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Values
    {
        get => _values;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _values = value;
            OnChanged();
        }
    }

    /// <summary>
    /// How much later than its ticks say the chart plays against the audio, as the song will have it once saved: from
    /// <see cref="EffectivePairs"/>, as the saved <c>notes.mid</c> has no offset of its own.
    /// </summary>
    public TimeSpan Delay => SongChartSettings.DelayOf(EffectivePairs(), (TimeSpan?)null);

    /// <summary>The value of the metadata key <paramref name="key"/>, as <see cref="OpenedSong.Value"/> reads it; null for a new chart.</summary>
    public string? Value(string key) => Opened?.Value(key);

    /// <summary>What the pending changes are worked out from, beside the chart.</summary>
    public PendingSongIniInputs PendingInputs => new(LoadedParts, Pairs)
    {
        AudioLengthMs = _audioLengthMs,
        DefaultCharter = _defaultCharter,
        Import = Import,
        Values = _values,
        DelayMs = _delayMs,
        Ratings = new Dictionary<string, int>(_ratings, StringComparer.OrdinalIgnoreCase),
        Edits = [.. _edits],
    };

    /// <summary>Every change the chart and the song call for, reverted or not, in the order they're listed.</summary>
    public IReadOnlyList<PendingSongIniChange> AllPendingChanges() => PendingSongIniChanges.Of(Chart, PendingInputs);

    /// <summary>The changes the next save makes: every one called for but those the user reverted.</summary>
    public IReadOnlyList<PendingSongIniChange> PendingChanges() =>
        [.. AllPendingChanges().Where(c => !c.CanRevert || !_reverted.Contains(c.Key))];

    /// <summary>The song's <c>song.ini</c> as the next save leaves it: <see cref="Pairs"/> with <see cref="PendingChanges"/> applied.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> EffectivePairs() => PendingSongIniChanges.Apply(Pairs, PendingChanges());

    /// <summary>The value of <paramref name="key"/> as the next save leaves it; null when it has none.</summary>
    public string? EffectiveValue(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return SongIniValues.Last(EffectivePairs(), key);
    }

    /// <summary>
    /// Makes the next save leave each key of <paramref name="values"/> with its value, or without it for null, in place of
    /// whatever else would change it: a rating, the delay Find first downbeat set, or a revert. A key left as Chart Builder
    /// would leave it on its own has no edit. Keys the chart decides are passed over. For undoable edits
    /// (<see cref="Commands.SongValueCommand"/>), which work from what the save would write, so they still hold once the
    /// song has been saved since.
    /// </summary>
    internal void SetEffectiveValues(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var targets = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
        {
            if (!PendingSongIniChanges.IsChartOwned(key, Chart, PendingInputs))
                targets[key] = value;
        }

        if (targets.Count == 0)
            return;

        // The delay Find first downbeat set covers both delay keys, so one set here holds the other where it is.
        if (_delayMs is not null && targets.Keys.Any(IsDelayKey))
        {
            foreach (string key in new[] { "delay", "delay_seconds" })
                targets.TryAdd(key, EffectiveValue(key));
            _delayMs = null;
        }

        foreach (string key in targets.Keys)
        {
            _ratings.Remove(key);
            _reverted.Remove(key);
            RemoveEdit(key);
        }

        var without = EffectivePairs();
        foreach (var (key, value) in targets)
        {
            if (SongIniValues.Last(without, key) != value)
                _edits.Add(KeyValuePair.Create(key, value));
        }

        OnChanged();
    }

    /// <summary>
    /// Leaves the change to <paramref name="key"/> out of saves, until it's restored; or, for a key the user edited,
    /// takes the edit back. Not an undo step.
    /// </summary>
    public void Revert(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (RemoveEdit(key) || _reverted.Add(key))
            OnChanged();
    }

    /// <summary>Takes back <see cref="Revert"/>, so saves make the change to <paramref name="key"/> again.</summary>
    public void Restore(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (_reverted.Remove(key))
            OnChanged();
    }

    /// <summary>Reverts each of <paramref name="keys"/>, as a recovered chart had them.</summary>
    public void RevertAll(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        bool any = false;
        foreach (string key in keys)
            any |= _reverted.Add(key);
        if (any)
            OnChanged();
    }

    /// <summary>
    /// Moves on to the song as it was saved: <paramref name="saved"/>, whose <c>song.ini</c> and files are now the ones
    /// changes are worked out from, with <paramref name="savedParts"/> the parts it was saved with. A new chart becomes a
    /// song opened from its folder. Reverted changes stay reverted.
    /// </summary>
    /// <param name="media">
    /// The slots the save wrote, and the session's copy of each file it replaced or removed, which an undo of an edit made
    /// before the save copies back in; by default, the slots with changes, without copies.
    /// </param>
    public void Saved(OpenedSong saved, IReadOnlySet<ChartInstrument> savedParts, MediaSaveResult? media = null)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(savedParts);
        int generation = SaveGeneration;
        foreach (var slot in media?.Changed ?? [.. _media.Keys])
        {
            if (media is not null && media.Kept.TryGetValue(slot, out string? kept))
                _kept.Add(new KeptMedia(slot, _storedSince.GetValueOrDefault(slot), generation, kept));
            _storedSince[slot] = generation + 1;
        }

        SaveGeneration = generation + 1;
        Opened = saved;
        LoadedParts = savedParts;
        _values = [];
        _delayMs = null;
        _ratings.Clear();
        _edits.Clear();
        _media.Clear();
        OnChanged();
        RefreshTracks();
    }

    /// <summary>A song opened from a folder or <c>.sng</c>, with its stems.</summary>
    /// <param name="loadedParts">
    /// The parts the chart on disk has, which need no rating added; by default the chart's own. A chart recovered from an
    /// autosave passes the parts of the chart on disk.
    /// </param>
    public static EditorSong Of(OpenedSong song, IReadOnlySet<ChartInstrument>? loadedParts = null)
    {
        ArgumentNullException.ThrowIfNull(song);
        loadedParts ??= song.HasChartFile ? [.. song.Chart.Parts.Select(p => p.Instrument)] : new HashSet<ChartInstrument>();
        return new EditorSong(song, song.Chart, SongTracks.Resolve(song.StoredNames, null), loadedParts);
    }

    /// <summary>A new chart, with the audio it was given, played from those files until it's first saved.</summary>
    public static EditorSong New(EditableChart chart, IReadOnlyList<SongTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(tracks);
        if (tracks.Any(t => !t.IsStaged))
            throw new ArgumentException("A new chart's tracks are played from the files it was given.", nameof(tracks));
        return new EditorSong(null, chart, tracks, new HashSet<ChartInstrument>());
    }

    private void Edit(string key, string? value)
    {
        if (SetEdit(key, value))
            OnChanged();
    }

    // An edit takes the place of whatever else changes the key: a rating, the delay Find first downbeat set, or a revert.
    private bool SetEdit(string key, string? value)
    {
        bool changed = _ratings.Remove(key) | _reverted.Remove(key);
        if (_delayMs is not null && (key.Equals("delay", StringComparison.OrdinalIgnoreCase) || key.Equals("delay_seconds", StringComparison.OrdinalIgnoreCase)))
        {
            _delayMs = null;
            changed = true;
        }

        int at = _edits.FindIndex(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (at < 0)
        {
            _edits.Add(KeyValuePair.Create(key, value));
            return true;
        }

        if (_edits[at].Value == value)
            return changed;
        _edits[at] = KeyValuePair.Create(_edits[at].Key, value);
        return true;
    }

    private bool RemoveEdit(string key) => _edits.RemoveAll(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) > 0;

    private static bool IsDelayKey(string key) =>
        key.Equals("delay", StringComparison.OrdinalIgnoreCase) || key.Equals("delay_seconds", StringComparison.OrdinalIgnoreCase);

    private void SetMedia(MediaSlot slot, MediaChange? change)
    {
        bool had = _media.TryGetValue(slot, out var was);
        if (change is { } now ? had && was == now : !had)
            return;
        if (change is { } set)
            _media[slot] = set;
        else
            _media.Remove(slot);
        OnChanged();
        if (slot.Kind == MediaKind.Track)
            RefreshTracks();
    }

    // What the slot holds with no change: a new chart's track as it was given, or the song's stored file.
    private MediaRef BaseMedia(MediaSlot slot)
    {
        if (Opened is null)
        {
            return slot.Kind == MediaKind.Track && _newTracks.FirstOrDefault(t => t.Stem.Equals(slot.Stem, StringComparison.OrdinalIgnoreCase)) is { StagedSourcePath: { } path }
                ? MediaRef.Source(path)
                : MediaRef.None;
        }

        return StoredFilesOf(slot).Count > 0 ? MediaRef.Stored(SaveGeneration) : MediaRef.None;
    }

    // What target means now: a file stored since, as it's stored now; one a save replaced since, as the copy kept of it.
    private MediaRef Resolve(MediaSlot slot, MediaRef target)
    {
        if (target is not MediaRef.StoredRef { Generation: var generation })
            return target;
        if (generation >= _storedSince.GetValueOrDefault(slot))
            return BaseMedia(slot);
        return _kept.LastOrDefault(k => k.Slot == slot && k.From <= generation && generation <= k.To) is { } kept
            ? MediaRef.Source(kept.Path)
            : MediaRef.None;
    }

    /// <summary>Works out the tracks again, raising <see cref="TracksChanged"/> when they differ.</summary>
    private void RefreshTracks()
    {
        var tracks = (Opened is { } opened ? SongTracks.Resolve(opened.StoredNames, null) : _newTracks)
            .Where(t => !_media.ContainsKey(new MediaSlot(MediaKind.Track, t.Stem)))
            .ToList();
        foreach (var (slot, change) in _media)
        {
            if (slot.Kind == MediaKind.Track && change.SourcePath is { } path && slot.StoredNameFor(path) is { } name)
                tracks.Add(new SongTrack(name, slot.Stem!, path));
        }

        IReadOnlyList<SongTrack> sorted = [.. tracks.OrderBy(t => AudioStems.OrderOf(t.Stem)).ThenBy(t => t.StoredName, StringComparer.OrdinalIgnoreCase)];
        if (sorted.SequenceEqual(_tracks))
            return;
        _tracks = sorted;
        TracksChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <param name="From">The first save it was the slot's stored file as of.</param>
    /// <param name="To">The last.</param>
    private sealed record KeptMedia(MediaSlot Slot, int From, int To, string Path);
}
