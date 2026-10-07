using ChartPacker.Core.Charts;
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
    private IReadOnlyList<KeyValuePair<string, string>> _values = [];
    private long? _audioLengthMs;
    private string _defaultCharter = "";

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

    /// <summary>The song's title, as the Editor's title shows it; null for a new chart.</summary>
    public string? Title => Opened?.Title;

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
    };

    /// <summary>Every change the chart and the song call for, reverted or not, in the order they're listed.</summary>
    public IReadOnlyList<PendingSongIniChange> AllPendingChanges() => PendingSongIniChanges.Of(Chart, PendingInputs);

    /// <summary>The changes the next save makes: every one called for but those the user reverted.</summary>
    public IReadOnlyList<PendingSongIniChange> PendingChanges() =>
        [.. AllPendingChanges().Where(c => !c.CanRevert || !_reverted.Contains(c.Key))];

    /// <summary>The song's <c>song.ini</c> as the next save leaves it: <see cref="Pairs"/> with <see cref="PendingChanges"/> applied.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> EffectivePairs() => PendingSongIniChanges.Apply(Pairs, PendingChanges());

    /// <summary>Leaves the change to <paramref name="key"/> out of saves, until it's restored. Not an undo step.</summary>
    public void Revert(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (_reverted.Add(key))
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
        loadedParts ??= song.HasChartFile ? song.Chart.Parts.Select(p => p.Instrument).ToHashSet() : new HashSet<ChartInstrument>();
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

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
