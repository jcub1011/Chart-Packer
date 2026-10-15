using ChartShared.Core.Charts;
using ChartShared.Core.Validation;

namespace ChartShared.Core.Charting.Lint;

/// <summary>
/// What one <see cref="IChartLintRule"/> is given to check (charter-spec C16): the snapshot and the song's settings,
/// the game view, and how much of the chart to check: all of it, one part, or one region of a part.
/// </summary>
/// <remarks>
/// The helpers give just what's in scope, so a range rule looks at the notes, chords and phrases of its region rather
/// than the whole chart. It may still read outside, such as the chord before its region's first, within its
/// <see cref="IChartLintRule.Reach"/>.
/// </remarks>
public sealed class ChartLintContext
{
    private readonly LintRun _run;
    private readonly EditablePart[] _parts;

    internal ChartLintContext(LintRun run, ChartInstrument? part, ChartLocation? region)
    {
        _run = run;
        Part = part;
        Region = region;
        if (part is not { } instrument)
            _parts = [.. run.Input.Chart.Parts];
        else
            _parts = run.Input.Chart.PartOf(instrument) is { } found ? [found] : [];
        FromTick = region?.Tick ?? 0;
        ToTick = region?.EndTick ?? long.MaxValue;
    }

    /// <summary>The snapshot of the chart.</summary>
    public EditableChart Chart => _run.Input.Chart;

    /// <summary>The song's metadata, as drafted.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> SongIni => _run.Input.SongIni;

    /// <summary>The settings the chart is read with.</summary>
    public ChartReadOptions Options => _run.Input.Options;

    /// <summary>How long the song's audio is, or null when it isn't known.</summary>
    public TimeSpan? AudioLength => _run.Input.AudioLength;

    /// <summary>How much later than its ticks say the chart plays against the audio.</summary>
    public TimeSpan Delay => _run.Input.Delay;

    /// <summary>The chart's tempo map, for times and the grid, made once in a run.</summary>
    public TempoMap TempoMap => _run.TempoMap;

    /// <summary>
    /// What the games read from the chart (<see cref="Charting.GameView.Project"/>, with <see cref="Options"/>). It's
    /// worked out the first time a rule asks for it, and at most once in a run, as it's costly on a big chart.
    /// </summary>
    public ChartDocument GameView => _run.GameView;

    /// <summary>The part to check, or null to check the whole chart.</summary>
    public ChartInstrument? Part { get; }

    /// <summary>
    /// The region of <see cref="Part"/> to check, its difficulty null for every difficulty; or null to check the whole
    /// part (or the whole chart).
    /// </summary>
    public ChartLocation? Region { get; }

    /// <summary>Whether the whole chart is to be checked.</summary>
    public bool IsWholeChart => Part is null;

    /// <summary>The first tick to check: the region's, or 0.</summary>
    public long FromTick { get; }

    /// <summary>The last tick to check: the region's, or <see cref="long.MaxValue"/>.</summary>
    public long ToTick { get; }

    /// <summary>The parts to check, in <see cref="ChartInstrument"/> order.</summary>
    public IReadOnlyList<EditablePart> Parts => _parts;

    /// <summary>
    /// The vocal parts the chart has, in <see cref="Layout.VocalTrack"/> order, each read from the first track of its
    /// name as the editor edits it; one that can't be read is left out. Read once in a run.
    /// </summary>
    public IReadOnlyList<(Layout.VocalTrack Track, VocalTrackContent Content)> VocalParts => _run.VocalParts;

    /// <summary>The tracks of <paramref name="part"/> to check, Easy to Expert: the region's difficulty, or every one.</summary>
    public IReadOnlyList<EditableTrack> TracksOf(EditablePart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        return Region?.Difficulty is { } difficulty ? [part[difficulty]] : part.Tracks;
    }

    /// <summary>
    /// The notes of <paramref name="track"/> that start from <see cref="FromTick"/> to <see cref="ToTick"/>, in order.
    /// A note that starts earlier isn't among them, though its sustain may reach in. Valid only while the snapshot is.
    /// </summary>
    public ReadOnlySpan<EditableNote> NotesIn(EditableTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return Region is null ? track.Notes.AsSpan() : track.Notes.Between(FromTick, ToTick);
    }

    /// <summary>The chords of <paramref name="track"/> that are from <see cref="FromTick"/> to <see cref="ToTick"/>, in order.</summary>
    public ChordSpans ChordsIn(EditableTrack track) => new(NotesIn(track));

    /// <summary>
    /// The phrases of <paramref name="part"/> that cover any tick from <see cref="FromTick"/> to <see cref="ToTick"/>,
    /// each from its tick to its tick plus its length, in order.
    /// </summary>
    public IEnumerable<Phrase> PhrasesIn(EditablePart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        if (Region is null)
            return part.Phrases;

        var phrases = new List<Phrase>();
        long from = FromTick;
        foreach (var phrase in part.Phrases.AsSpan()[..part.Phrases.FirstAfter(ToTick)])
        {
            if (phrase.Tick + phrase.Length >= from)
                phrases.Add(phrase);
        }

        return phrases;
    }

    /// <summary>The tick of the last chord of <paramref name="track"/> before <paramref name="tick"/>, or null when there's none.</summary>
    public static long? PreviousChordTick(EditableTrack track, long tick) => ChordTickBefore(track, tick, 1);

    /// <summary>The tick of the first chord of <paramref name="track"/> after <paramref name="tick"/>, or null when there's none.</summary>
    public static long? NextChordTick(EditableTrack track, long tick) => ChordTickAfter(track, tick, 1);

    /// <summary>
    /// The tick of the <paramref name="count"/>th chord of <paramref name="track"/> before <paramref name="tick"/>; the
    /// first chord's when there are fewer, or null when there's none.
    /// </summary>
    internal static long? ChordTickBefore(EditableTrack track, long tick, int count)
    {
        ArgumentNullException.ThrowIfNull(track);
        var notes = track.Notes;
        long? found = null;
        int index = notes.FirstAtOrAfter(tick);
        for (int i = 0; i < count && index > 0; i++)
        {
            long before = notes[index - 1].Tick;
            found = before;
            index = notes.FirstAtOrAfter(before);
        }

        return found;
    }

    /// <summary>
    /// The tick of the <paramref name="count"/>th chord of <paramref name="track"/> after <paramref name="tick"/>; the
    /// last chord's when there are fewer, or null when there's none.
    /// </summary>
    internal static long? ChordTickAfter(EditableTrack track, long tick, int count)
    {
        ArgumentNullException.ThrowIfNull(track);
        var notes = track.Notes;
        long? found = null;
        int index = notes.FirstAfter(tick);
        for (int i = 0; i < count && index < notes.Count; i++)
        {
            long after = notes[index].Tick;
            found = after;
            index = notes.FirstAfter(after);
        }

        return found;
    }
}

/// <summary>A track's notes as chords: each the notes at one tick, in order. For <c>foreach</c>.</summary>
public readonly ref struct ChordSpans
{
    private readonly ReadOnlySpan<EditableNote> _notes;

    /// <param name="notes">The notes, in tick order.</param>
    public ChordSpans(ReadOnlySpan<EditableNote> notes) => _notes = notes;

    public Enumerator GetEnumerator() => new(_notes);

    /// <summary>Steps through the chords.</summary>
    public ref struct Enumerator
    {
        private ReadOnlySpan<EditableNote> _rest;

        internal Enumerator(ReadOnlySpan<EditableNote> notes)
        {
            _rest = notes;
            Current = [];
        }

        /// <summary>The notes of the chord, at least one, all at one tick.</summary>
        public ReadOnlySpan<EditableNote> Current { get; private set; }

        public bool MoveNext()
        {
            if (_rest.IsEmpty)
                return false;
            long tick = _rest[0].Tick;
            int length = 1;
            while (length < _rest.Length && _rest[length].Tick == tick)
                length++;
            Current = _rest[..length];
            _rest = _rest[length..];
            return true;
        }
    }
}
