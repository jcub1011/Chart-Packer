using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting.Editing;

/// <summary>Where a note is in its track: its tick, lane and kind of kick, which no two notes of a track share.</summary>
public readonly record struct NotePlace(long Tick, int Lane, bool DoubleKick = false)
{
    public static NotePlace Of(EditableNote note) => new(note.Tick, note.Lane, note.DoubleKick);
}

/// <summary>
/// What's selected in the editor (charter-spec C15 item 2): notes of one track, tempo markers, phrases of one part, and a
/// span of ticks across the lanes. Notes are kept by their place, so a note keeps its selection when its length changes,
/// and loses it when it goes. Phrases are kept as they are, so a phrase changed is selected again as what it became.
/// </summary>
public sealed class EditorSelection
{
    private readonly EditableChart _chart;
    private readonly HashSet<NotePlace> _notes = [];
    private readonly SortedSet<long> _tempos = [];
    private readonly HashSet<Phrase> _phrases = [];

    public EditorSelection(EditableChart chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        _chart = chart;
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

    /// <summary>The part the selected phrases are of, or null when no phrases are selected.</summary>
    public ChartInstrument? PhrasePart { get; private set; }

    public bool IsEmpty => _notes.Count == 0 && _tempos.Count == 0 && _phrases.Count == 0 && Range is null;

    public bool HasNotes => _notes.Count > 0;

    public bool HasPhrases => _phrases.Count > 0;

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
    /// The span of ticks the selection covers, for playing it, looping it or fitting a tempo to it: the span selected,
    /// or else from the first selected note to where the last is let go, or else from the first selected phrase's start
    /// to the last one's end, or else from the first selected tempo marker to the last. Null when it covers no ticks.
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

            if (_phrases.Count > 0)
            {
                long from = _phrases.Min(static p => p.Tick);
                long to = _phrases.Max(static p => p.Tick + p.Length);
                return to > from ? (from, to) : null;
            }

            return _tempos.Count >= 2 ? (_tempos.Min, _tempos.Max) : null;
        }
    }

    /// <summary>Whether <paramref name="note"/> of <paramref name="track"/> is selected.</summary>
    public bool Contains(ChartTrackKey track, EditableNote note) => Track == track && _notes.Contains(NotePlace.Of(note));

    /// <summary>Whether <paramref name="phrase"/> of <paramref name="part"/> is selected.</summary>
    public bool Contains(ChartInstrument part, Phrase phrase) => PhrasePart == part && _phrases.Contains(phrase);

    /// <summary>Selects nothing.</summary>
    public void Clear()
    {
        if (IsEmpty)
            return;
        _notes.Clear();
        _tempos.Clear();
        ClearPhraseSet();
        Track = null;
        Range = null;
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
            Range = null;
        }
        else if (Track != track)
        {
            _notes.Clear();
        }

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

    /// <summary>Selects the span from <paramref name="from"/> to <paramref name="to"/>, keeping the notes and markers selected.</summary>
    public void SetRange(long from, long to)
    {
        var range = (Math.Min(from, to), Math.Max(from, to));
        if (Range == range)
            return;
        Range = range;
        Raise();
    }

    /// <summary>
    /// Drops what the chart no longer has: notes gone from their places, markers gone from their ticks, and phrases the
    /// part no longer has as they were.
    /// </summary>
    /// <returns>Whether anything was dropped.</returns>
    public bool Reconcile()
    {
        int before = _notes.Count + _tempos.Count + _phrases.Count;
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
        bool dropped = _notes.Count + _tempos.Count + _phrases.Count != before;
        if (dropped)
            Raise();
        return dropped;
    }

    private void ClearPhraseSet()
    {
        _phrases.Clear();
        PhrasePart = null;
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
