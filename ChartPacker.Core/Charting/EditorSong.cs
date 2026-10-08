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

    private EditorSong(OpenedSong? opened, EditableChart chart, IReadOnlyList<SongTrack> tracks, IReadOnlySet<ChartInstrument> loadedParts)
    {
        Opened = opened;
        Chart = chart;
        Tracks = tracks;
        LoadedParts = loadedParts;
    }

    /// <summary>
    /// Raised when anything the pending changes, the delay or the title come from changes, other than the chart: a save,
    /// a revert or restore, the audio's length, a value or the default charter.
    /// </summary>
    public event EventHandler? Changed;

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

    /// <summary>The audio tracks: the song's stems, or a new chart's files, each played from where it is.</summary>
    public IReadOnlyList<SongTrack> Tracks { get; }

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
    public AlbumArtChange? AlbumArt { get; private set; }

    /// <summary>
    /// Whether the user has changed the song itself, beside the chart, since it was opened or last saved: an edit,
    /// album art, a delay or a rating. Changes Chart Builder works out on its own don't count.
    /// </summary>
    public bool HasUserChanges => _edits.Count > 0 || AlbumArt is not null || _delayMs is not null || _ratings.Count > 0;

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

    /// <summary>Puts the image at <paramref name="sourcePath"/> in place of the song's album art at the next save.</summary>
    public void SetAlbumArt(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        SetArt(new AlbumArtChange(sourcePath));
    }

    /// <summary>Removes the song's album art at the next save.</summary>
    public void RemoveAlbumArt() => SetArt(new AlbumArtChange(null));

    /// <summary>Leaves the song's album art as it is.</summary>
    public void RevertAlbumArt() => SetArt(null);

    /// <summary>Sets each of <paramref name="edits"/> and the album art, as a recovered chart had them.</summary>
    public void SetEdits(IEnumerable<KeyValuePair<string, string?>> edits, AlbumArtChange? albumArt)
    {
        ArgumentNullException.ThrowIfNull(edits);
        foreach (var (key, value) in edits)
            SetEdit(key, value);
        AlbumArt = albumArt;
        OnChanged();
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
    /// Moves on to the song as it was saved: <paramref name="saved"/>, whose <c>song.ini</c> is now the one changes are
    /// worked out from, with <paramref name="savedParts"/> the parts it was saved with. A new chart becomes a song
    /// opened from its folder. Reverted changes stay reverted.
    /// </summary>
    public void Saved(OpenedSong saved, IReadOnlySet<ChartInstrument> savedParts)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(savedParts);
        Opened = saved;
        LoadedParts = savedParts;
        _values = [];
        _delayMs = null;
        _ratings.Clear();
        _edits.Clear();
        AlbumArt = null;
        OnChanged();
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

    private void SetArt(AlbumArtChange? art)
    {
        if (AlbumArt == art)
            return;
        AlbumArt = art;
        OnChanged();
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
