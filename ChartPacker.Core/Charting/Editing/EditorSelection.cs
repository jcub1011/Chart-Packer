using ChartPacker.Core.Charting.Layout;
using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting.Editing;

/// <summary>Where a note is in its track: its tick, lane and kind of kick, which no two notes of a track share.</summary>
public readonly record struct NotePlace(long Tick, int Lane, bool DoubleKick = false)
{
    public static NotePlace Of(EditableNote note) => new(note.Tick, note.Lane, note.DoubleKick);
}

/// <summary>
/// What's selected in the editor (charter-spec C15 item 2): notes of one track, tempo markers, phrases of one part, disco
/// flip sections of the drums' difficulties, and a span of ticks across the lanes; or the notes and phrases of one vocal
/// part (C3 item 7). Notes are kept by their place, so a note keeps its selection when its length changes, and loses it
/// when it goes. Phrases and disco flip sections are kept as they are, so one changed is selected again as what it became.
/// </summary>
/// <remarks>
/// A time range can also be selected across lanes (<see cref="SelectRange"/>): every note of those tracks inside it, as
/// the chart has them, which delete, cut and copy, moves in time and the transforms act on (<see cref="NotesByTrack"/>).
/// The note set stays one track's, so Properties and the keys that change one note at a time keep to it.
/// </remarks>
public sealed class EditorSelection
{
    private readonly EditableChart _chart;
    private readonly HashSet<NotePlace> _notes = [];
    private readonly SortedSet<long> _tempos = [];
    private readonly HashSet<Phrase> _phrases = [];
    private readonly HashSet<DiscoFlipSection> _discoFlips = [];
    private readonly List<ChartTrackKey> _rangeTracks = [];
    private readonly HashSet<VocalNotePlace> _vocalNotes = [];
    private readonly HashSet<VocalPhraseEvent> _vocalPhrases = [];
    private readonly Func<VocalTrack, VocalTrackContent?> _vocals;

    public EditorSelection(EditableChart chart)
        : this(chart, null)
    {
    }

    /// <param name="chart">The chart.</param>
    /// <param name="vocals">Each vocal part as it's edited; read from the chart when not given.</param>
    internal EditorSelection(EditableChart chart, Func<VocalTrack, VocalTrackContent?>? vocals)
    {
        ArgumentNullException.ThrowIfNull(chart);
        _chart = chart;
        _vocals = vocals ?? (track => VocalTracks.IndexIn(chart, track) is var index and >= 0 ? VocalTrackContent.Of(chart.Passthrough[index]) : null);
    }

    /// <summary>Raised whenever what's selected changes.</summary>
    public event EventHandler? Changed;

    /// <summary>The track the selected notes are in, or null when no notes are selected.</summary>
    public ChartTrackKey? Track { get; private set; }

    /// <summary>The places of the selected notes.</summary>
    public IReadOnlyCollection<NotePlace> Places => _notes;

    /// <summary>The ticks of the selected tempo markers, in order.</summary>
    public IReadOnlyCollection<long> TempoTicks => _tempos;

    /// <summary>The span selected across the lanes, in ticks, both ends included; null for none.</summary>
    public (long From, long To)? Range { get; private set; }

    /// <summary>The tracks a range across lanes covers (<see cref="SelectRange"/>); none when the range is a box's alone.</summary>
    public IReadOnlyList<ChartTrackKey> RangeTracks => _rangeTracks;

    /// <summary>Whether a range across lanes is selected, rather than notes.</summary>
    public bool HasRangeAcrossLanes => _notes.Count == 0 && Range is not null && _rangeTracks.Count > 0;

    /// <summary>The part the selected phrases are of, or null when no phrases are selected.</summary>
    public ChartInstrument? PhrasePart { get; private set; }

    public bool IsEmpty =>
        _notes.Count == 0 && _tempos.Count == 0 && _phrases.Count == 0 && _discoFlips.Count == 0 && Range is null
        && _vocalNotes.Count == 0 && _vocalPhrases.Count == 0;

    /// <summary>The vocal part the selected vocal notes and phrases are of, or null when none are selected.</summary>
    public VocalTrack? Vocal { get; private set; }

    /// <summary>The places of the selected vocal notes.</summary>
    public IReadOnlyCollection<VocalNotePlace> VocalPlaces => _vocalNotes;

    public bool HasVocalNotes => _vocalNotes.Count > 0;

    public bool HasVocalPhrases => _vocalPhrases.Count > 0;

    /// <summary>The selected vocal notes as the part has them now, in tick order.</summary>
    public IReadOnlyList<VocalNoteEvent> VocalNotes =>
        Vocal is { } track && _vocalNotes.Count > 0 && _vocals(track) is { } content ? VocalEdits.NotesAt(content, _vocalNotes) : [];

    /// <summary>The selected vocal phrases, in tick order.</summary>
    public IReadOnlyList<VocalPhraseEvent> VocalPhrases => [.. _vocalPhrases.OrderBy(static p => p.Tick).ThenBy(static p => p.EndTick)];

    public bool HasNotes => _notes.Count > 0;

    public bool HasPhrases => _phrases.Count > 0;

    public bool HasDiscoFlips => _discoFlips.Count > 0;

    /// <summary>The selected disco flip sections, by difficulty, then in tick order.</summary>
    public IReadOnlyList<DiscoFlipSection> DiscoFlips =>
        [.. _discoFlips.OrderBy(static s => s.Difficulty).ThenBy(static s => s.Range.Tick)];

    /// <summary>The selected phrases, in the part's order.</summary>
    public IReadOnlyList<Phrase> Phrases
    {
        get
        {
            var phrases = _phrases.ToList();
            phrases.Sort(Phrase.Order);
            return phrases;
        }
    }

    /// <summary>The selected notes as the chart has them now, in track order.</summary>
    public IReadOnlyList<EditableNote> Notes
    {
        get
        {
            if (Track is not { } key || _chart.PartOf(key.Instrument) is not { } part)
                return [];
            var track = part[key.Difficulty];
            var notes = new List<EditableNote>(_notes.Count);
            foreach (var place in _notes)
            {
                if (track.TryGetNote(place.Tick, place.Lane, place.DoubleKick, out var note))
                    notes.Add(note);
            }

            notes.Sort(EditableNote.Order);
            return notes;
        }
    }

    /// <summary>
    /// The notes selected, by track: the note set's, or with none, every note of the tracks a range across lanes covers
    /// inside it, as the chart has them now. Tracks with none are left out.
    /// </summary>
    public IReadOnlyList<(ChartTrackKey Track, IReadOnlyList<EditableNote> Notes)> NotesByTrack()
    {
        if (Track is { } key)
        {
            var notes = Notes;
            return notes.Count > 0 ? [(key, notes)] : [];
        }

        if (Range is not { } range || _rangeTracks.Count == 0)
            return [];
        var tracks = new List<(ChartTrackKey, IReadOnlyList<EditableNote>)>();
        foreach (var track in _rangeTracks)
        {
            if (_chart.PartOf(track.Instrument)?[track.Difficulty] is not { } notes)
                continue;
            var inside = notes.Notes.Between(range.From, range.To).ToArray();
            if (inside.Length > 0)
                tracks.Add((track, inside));
        }

        return tracks;
    }

    /// <summary>
    /// The span of ticks the selection covers, for playing it, looping it or fitting a tempo to it: the span selected,
    /// or else from the first selected note to where the last is let go, or else from the first selected phrase's or
    /// disco flip section's start to the last one's end (a section without one adding only its start), or else from the
    /// first selected tempo marker to the last. Null when it covers no ticks.
    /// </summary>
    public (long From, long To)? Span
    {
        get
        {
            if (Range is { } range && range.To > range.From)
                return range;
            var notes = Notes;
            if (notes.Count > 0)
            {
                long from = notes[0].Tick;
                long to = notes.Max(static n => n.Tick + n.Length);
                return to > from ? (from, to) : null;
            }

            var vocalNotes = VocalNotes;
            if (vocalNotes.Count > 0)
            {
                long from = vocalNotes[0].Tick;
                long to = vocalNotes.Max(static n => n.EndTick);
                return to > from ? (from, to) : null;
            }

            if (_phrases.Count > 0 || _discoFlips.Count > 0 || _vocalPhrases.Count > 0)
            {
                var spans = _phrases.Select(static p => (From: p.Tick, To: p.Tick + p.Length))
                    .Concat(_discoFlips.Select(static s => (From: s.Range.Tick, To: s.Range.EndTick ?? s.Range.Tick)))
                    .Concat(_vocalPhrases.Select(static p => (From: p.Tick, To: p.EndTick)))
                    .ToList();
                long from = spans.Min(static s => s.From);
                long to = spans.Max(static s => s.To);
                return to > from ? (from, to) : null;
            }

            return _tempos.Count >= 2 ? (_tempos.Min, _tempos.Max) : null;
        }
    }

    /// <summary>Whether <paramref name="note"/> of <paramref name="track"/> is selected.</summary>
    public bool Contains(ChartTrackKey track, EditableNote note) => Track == track && _notes.Contains(NotePlace.Of(note));

    /// <summary>
    /// Whether <paramref name="note"/> of <paramref name="track"/> is drawn as selected: in the note set, or inside a range
    /// selected across lanes that covers its track.
    /// </summary>
    public bool IsMarked(ChartTrackKey track, EditableNote note) =>
        Contains(track, note) || (HasRangeAcrossLanes && Range is { } range && note.Tick >= range.From && note.Tick <= range.To && _rangeTracks.Contains(track));

    /// <summary>Whether <paramref name="phrase"/> of <paramref name="part"/> is selected.</summary>
    public bool Contains(ChartInstrument part, Phrase phrase) => PhrasePart == part && _phrases.Contains(phrase);

    /// <summary>Whether <paramref name="section"/> is selected.</summary>
    public bool Contains(DiscoFlipSection section) => _discoFlips.Contains(section);

    /// <summary>Whether the vocal note at <paramref name="place"/> of <paramref name="track"/> is selected.</summary>
    public bool Contains(VocalTrack track, VocalNotePlace place) => Vocal == track && _vocalNotes.Contains(place);

    /// <summary>Whether <paramref name="phrase"/> of <paramref name="track"/> is selected.</summary>
    public bool Contains(VocalTrack track, VocalPhraseEvent phrase) => Vocal == track && _vocalPhrases.Contains(phrase);

    /// <summary>Selects nothing.</summary>
    public void Clear()
    {
        if (IsEmpty)
            return;
        _notes.Clear();
        _tempos.Clear();
        ClearPhraseSet();
        _discoFlips.Clear();
        _rangeTracks.Clear();
        ClearVocalSets();
        Track = null;
        Range = null;
        Raise();
    }

    /// <summary>
    /// Selects the span from <paramref name="from"/> to <paramref name="to"/> across <paramref name="tracks"/>, in place of
    /// what was selected (charter-spec C15 item 2): a time range across lanes, a whole lane's or a section's.
    /// </summary>
    public void SelectRange(long from, long to, IEnumerable<ChartTrackKey> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        _notes.Clear();
        _tempos.Clear();
        ClearPhraseSet();
        _discoFlips.Clear();
        ClearVocalSets();
        Track = null;
        _rangeTracks.Clear();
        _rangeTracks.AddRange(tracks.Distinct());
        Range = (Math.Max(0, Math.Min(from, to)), Math.Max(0, Math.Max(from, to)));
        Raise();
    }

    /// <summary>Selects <paramref name="notes"/> of <paramref name="track"/>, in place of what was selected or as well as it.</summary>
    /// <param name="add">Whether to keep what's selected; only the notes of another track are dropped.</param>
    public void Select(ChartTrackKey track, IEnumerable<EditableNote> notes, bool add = false)
    {
        ArgumentNullException.ThrowIfNull(notes);
        SelectPlaces(track, notes.Select(NotePlace.Of), add);
    }

    /// <summary>Selects the notes at <paramref name="places"/> of <paramref name="track"/>.</summary>
    public void SelectPlaces(ChartTrackKey track, IEnumerable<NotePlace> places, bool add = false)
    {
        ArgumentNullException.ThrowIfNull(places);
        if (!add)
        {
            _notes.Clear();
            _tempos.Clear();
            ClearPhraseSet();
            _discoFlips.Clear();
            _rangeTracks.Clear();
            ClearVocalSets();
            Range = null;
        }
        else if (Track != track)
        {
            _notes.Clear();
        }

        // The note set is one track's: a vocal part's goes.
        _vocalNotes.Clear();
        if (_vocalPhrases.Count == 0)
            Vocal = null;
        Track = track;
        _notes.UnionWith(places);
        if (_notes.Count == 0)
            Track = null;
        Raise();
    }

    /// <summary>Selects <paramref name="note"/> when it isn't, and the other way round, keeping the rest.</summary>
    public void Toggle(ChartTrackKey track, EditableNote note)
    {
        if (Track != track)
            _notes.Clear();
        _vocalNotes.Clear();
        if (_vocalPhrases.Count == 0)
            Vocal = null;
        Track = track;
        var place = NotePlace.Of(note);
        if (!_notes.Remove(place))
            _notes.Add(place);
        if (_notes.Count == 0)
            Track = null;
        Raise();
    }

    /// <summary>Selects the tempo markers at <paramref name="ticks"/>, in place of what was selected or as well as it.</summary>
    public void SelectTempos(IEnumerable<long> ticks, bool add = false)
    {
        ArgumentNullException.ThrowIfNull(ticks);
        if (!add)
        {
            _notes.Clear();
            _tempos.Clear();
            ClearPhraseSet();
            _discoFlips.Clear();
            _rangeTracks.Clear();
            ClearVocalSets();
            Track = null;
            Range = null;
        }

        _tempos.UnionWith(ticks);
        Raise();
    }

    /// <summary>Selects <paramref name="phrases"/> of <paramref name="part"/>, in place of what was selected or as well as it.</summary>
    /// <param name="add">Whether to keep what's selected; only the phrases of another part are dropped.</param>
    public void SelectPhrases(ChartInstrument part, IEnumerable<Phrase> phrases, bool add = false)
    {
        ArgumentNullException.ThrowIfNull(phrases);
        if (!add)
        {
            _notes.Clear();
            _tempos.Clear();
            _discoFlips.Clear();
            _rangeTracks.Clear();
            ClearVocalSets();
            Track = null;
            Range = null;
        }

        if (!add || PhrasePart != part)
            _phrases.Clear();
        PhrasePart = part;
        _phrases.UnionWith(phrases);
        if (_phrases.Count == 0)
            PhrasePart = null;
        Raise();
    }

    /// <summary>Selects <paramref name="phrase"/> when it isn't, and the other way round, keeping the rest.</summary>
    public void Toggle(ChartInstrument part, Phrase phrase)
    {
        if (PhrasePart != part)
            _phrases.Clear();
        PhrasePart = part;
        if (!_phrases.Remove(phrase))
            _phrases.Add(phrase);
        if (_phrases.Count == 0)
            PhrasePart = null;
        Raise();
    }

    /// <summary>
    /// Selects <paramref name="phrases"/> of <paramref name="part"/> in place of the phrases selected, keeping the notes,
    /// markers and span: the same phrases as an edit has changed them.
    /// </summary>
    public void ReplacePhrases(ChartInstrument part, IEnumerable<Phrase> phrases)
    {
        ArgumentNullException.ThrowIfNull(phrases);
        _phrases.Clear();
        PhrasePart = part;
        _phrases.UnionWith(phrases);
        if (_phrases.Count == 0)
            PhrasePart = null;
        Raise();
    }

    /// <summary>Selects no phrases, keeping the rest.</summary>
    public void ClearPhrases()
    {
        if (_phrases.Count == 0)
            return;
        ClearPhraseSet();
        Raise();
    }

    /// <summary>
    /// Selects <paramref name="sections"/>, in place of what was selected or as well as it. Disco flip sections are selected
    /// as phrases are: selecting them drops the notes, tempo markers, span and phrases, unless adding.
    /// </summary>
    public void SelectDiscoFlips(IEnumerable<DiscoFlipSection> sections, bool add = false)
    {
        ArgumentNullException.ThrowIfNull(sections);
        if (!add)
        {
            _notes.Clear();
            _tempos.Clear();
            ClearPhraseSet();
            _discoFlips.Clear();
            _rangeTracks.Clear();
            ClearVocalSets();
            Track = null;
            Range = null;
        }

        _discoFlips.UnionWith(sections);
        Raise();
    }

    /// <summary>Selects <paramref name="section"/> when it isn't, and the other way round, keeping the rest.</summary>
    public void Toggle(DiscoFlipSection section)
    {
        if (!_discoFlips.Remove(section))
            _discoFlips.Add(section);
        Raise();
    }

    /// <summary>
    /// Selects <paramref name="sections"/> in place of the disco flip sections selected, keeping the rest: the same sections
    /// as an edit has changed them.
    /// </summary>
    public void ReplaceDiscoFlips(IEnumerable<DiscoFlipSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        _discoFlips.Clear();
        _discoFlips.UnionWith(sections);
        Raise();
    }

    /// <summary>Selects no disco flip sections, keeping the rest.</summary>
    public void ClearDiscoFlips()
    {
        if (_discoFlips.Count == 0)
            return;
        _discoFlips.Clear();
        Raise();
    }

    /// <summary>
    /// Selects the vocal notes at <paramref name="places"/> of <paramref name="track"/>, in place of what was selected or as
    /// well as it. Either way a highway's notes, and the span, are dropped, as the note set is one track's.
    /// </summary>
    /// <param name="add">Whether to keep what's selected; only the notes and phrases of another vocal part are dropped.</param>
    public void SelectVocalNotes(VocalTrack track, IEnumerable<VocalNotePlace> places, bool add = false)
    {
        ArgumentNullException.ThrowIfNull(places);
        StartVocal(track, add);
        _vocalNotes.UnionWith(places);
        EndVocal();
    }

    /// <summary>Selects the vocal note at <paramref name="place"/> when it isn't, and the other way round, keeping the rest.</summary>
    public void ToggleVocalNote(VocalTrack track, VocalNotePlace place)
    {
        StartVocal(track, add: true);
        if (!_vocalNotes.Remove(place))
            _vocalNotes.Add(place);
        EndVocal();
    }

    /// <summary>
    /// Selects <paramref name="phrases"/> of <paramref name="track"/>, in place of what was selected or as well as it.
    /// Either way a highway's notes, and the span, are dropped.
    /// </summary>
    /// <param name="add">Whether to keep what's selected; only the notes and phrases of another vocal part are dropped.</param>
    public void SelectVocalPhrases(VocalTrack track, IEnumerable<VocalPhraseEvent> phrases, bool add = false)
    {
        ArgumentNullException.ThrowIfNull(phrases);
        StartVocal(track, add);
        _vocalPhrases.UnionWith(phrases);
        EndVocal();
    }

    /// <summary>Selects <paramref name="phrase"/> of <paramref name="track"/> when it isn't, and the other way round, keeping the rest.</summary>
    public void ToggleVocalPhrase(VocalTrack track, VocalPhraseEvent phrase)
    {
        StartVocal(track, add: true);
        if (!_vocalPhrases.Remove(phrase))
            _vocalPhrases.Add(phrase);
        EndVocal();
    }

    /// <summary>
    /// Selects <paramref name="phrases"/> of <paramref name="track"/> in place of the vocal phrases selected, keeping the
    /// notes: the same phrases as an edit has changed them.
    /// </summary>
    public void ReplaceVocalPhrases(VocalTrack track, IEnumerable<VocalPhraseEvent> phrases)
    {
        ArgumentNullException.ThrowIfNull(phrases);
        if (Vocal != track)
            _vocalNotes.Clear();
        Vocal = track;
        _vocalPhrases.Clear();
        _vocalPhrases.UnionWith(phrases);
        EndVocal();
    }

    /// <summary>Selects the span from <paramref name="from"/> to <paramref name="to"/>, keeping the notes and markers selected.</summary>
    public void SetRange(long from, long to)
    {
        var range = (Math.Min(from, to), Math.Max(from, to));
        if (Range == range && _rangeTracks.Count == 0)
            return;
        Range = range;
        _rangeTracks.Clear();
        Raise();
    }

    /// <summary>
    /// Drops what the chart no longer has: notes gone from their places, markers gone from their ticks, and phrases and
    /// disco flip sections the chart no longer has as they were.
    /// </summary>
    /// <returns>Whether anything was dropped.</returns>
    public bool Reconcile()
    {
        int before = _notes.Count + _tempos.Count + _phrases.Count + _discoFlips.Count + _rangeTracks.Count + _vocalNotes.Count + _vocalPhrases.Count;
        _rangeTracks.RemoveAll(k => _chart.PartOf(k.Instrument) is null);
        if (Track is { } key && _chart.PartOf(key.Instrument) is { } part)
        {
            var track = part[key.Difficulty];
            _notes.RemoveWhere(p => !track.TryGetNote(p.Tick, p.Lane, p.DoubleKick, out _));
        }
        else
        {
            _notes.Clear();
        }

        if (_notes.Count == 0)
            Track = null;
        _tempos.RemoveWhere(t => _chart.TempoMap.Tempos.At(t).IsEmpty);
        if (PhrasePart is { } instrument && _chart.PartOf(instrument) is { } phrased)
            _phrases.RemoveWhere(p => phrased.Phrases.IndexOf(p) < 0);
        else
            _phrases.Clear();
        if (_phrases.Count == 0)
            PhrasePart = null;
        if (_chart.PartOf(ChartInstrument.Drums) is { } drums)
            _discoFlips.RemoveWhere(s => !Charting.DiscoFlips.RangesOf(drums[s.Difficulty]).Contains(s.Range));
        else
            _discoFlips.Clear();
        if (Vocal is { } vocal && _vocals(vocal) is { } content)
        {
            var places = content.Notes.Select(static n => n.Place).ToHashSet();
            _vocalNotes.RemoveWhere(p => !places.Contains(p));
            var phrases = content.Phrases.ToHashSet();
            _vocalPhrases.RemoveWhere(p => !phrases.Contains(p));
            if (_vocalNotes.Count == 0 && _vocalPhrases.Count == 0)
                Vocal = null;
        }
        else
        {
            ClearVocalSets();
        }

        bool dropped = _notes.Count + _tempos.Count + _phrases.Count + _discoFlips.Count + _rangeTracks.Count + _vocalNotes.Count + _vocalPhrases.Count != before;
        if (dropped)
            Raise();
        return dropped;
    }

    private void ClearPhraseSet()
    {
        _phrases.Clear();
        PhrasePart = null;
    }

    private void ClearVocalSets()
    {
        _vocalNotes.Clear();
        _vocalPhrases.Clear();
        Vocal = null;
    }

    // Before selecting a vocal part's notes or phrases: a highway's notes and the span go, and unless adding, the rest.
    private void StartVocal(VocalTrack track, bool add)
    {
        if (!add)
        {
            _tempos.Clear();
            ClearPhraseSet();
            _discoFlips.Clear();
            ClearVocalSets();
        }
        else if (Vocal != track)
        {
            ClearVocalSets();
        }

        _notes.Clear();
        Track = null;
        _rangeTracks.Clear();
        Range = null;
        Vocal = track;
    }

    private void EndVocal()
    {
        if (_vocalNotes.Count == 0 && _vocalPhrases.Count == 0)
            Vocal = null;
        Raise();
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
