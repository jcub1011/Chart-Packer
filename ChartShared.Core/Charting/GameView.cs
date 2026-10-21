using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting;

/// <summary>
/// The model's <b>game view</b> (charter-spec §4.3): the <see cref="ChartDocument"/> the games read from the file the
/// model saves to, worked out from the model itself. The editor draws natural HOPOs and sustain cutoffs from it, the
/// preview builds its scene from it, and the save's read-back check compares <see cref="ChartReader"/>'s reading of
/// the saved bytes with it (<see cref="ChartReadBack"/>).
/// </summary>
/// <remarks>
/// <para>
/// The model is put through the same <see cref="GameNotes"/> code <see cref="MidiChartReader"/> puts a file through,
/// so the HOPO, sustain, star-power, tom, mix and layout rules are YARG's in one place. Only what the file says is
/// taken from the model rather than from MIDI events: notes as the model holds them (opens already in their lane),
/// each flagged chord as a marker over its tick, the part's star-power phrases, tom markers and mixes.
/// </para>
/// <para>
/// The game view has only the instruments the model edits (<see cref="Instruments"/>). <c>PART KEYS</c>, which the
/// reader reads, passes through byte for byte, and is checked that way rather than here.
/// </para>
/// <para>
/// It holds for a file written in <see cref="PartEventOrder"/>, which the writer follows: the order of a part
/// track's events at one tick decides which of two notes on a pad is kept, which mix is in effect, and whether a
/// fifth-pad note or a tom marker settles the drums' layout.
/// </para>
/// </remarks>
public static class GameView
{
    /// <summary>The instruments the model edits, and so the game view has: all but <c>PART KEYS</c> (charter-spec §7.4).</summary>
    public static IReadOnlyList<ChartInstrument> Instruments { get; } =
        [.. Enum.GetValues<ChartInstrument>().Where(i => i != ChartInstrument.Keys)];

    /// <summary>The chart the games read from the file <paramref name="chart"/> saves to.</summary>
    /// <param name="options">
    /// The song.ini settings the saved file will be read with: those that apply after the save, as
    /// <see cref="ChartReadOptions.From"/> reads them. Their <c>multiplier_note</c> is ignored, as the writer writes star
    /// power on 116 and clears it; their sustain cutoff, HOPO threshold and drums settings are used as the reader uses them.
    /// </param>
    public static ChartDocument Project(EditableChart chart, ChartReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(options);

        int resolution = chart.Resolution;
        var parts = new List<(ChartInstrument, IReadOnlyDictionary<ChartDifficulty, List<ChartNote>>, IReadOnlyList<StarPowerPhrase>)>(chart.Parts.Count);
        DrumsLayout? drumsLayout = null;
        foreach (var part in chart.Parts)
        {
            if (part.Instrument == ChartInstrument.Keys)
                continue;

            var starPower = StarPowerOf(part);
            if (part.Kind == ChartInstrumentKind.Drums)
            {
                var markers = SavedTomMarkers(chart, part);
                var layout = DrumsLayoutOf(part, markers, options);
                drumsLayout = layout;
                parts.Add((part.Instrument, Drums(part, markers, layout, starPower), starPower));
                continue;
            }

            var notes = new Dictionary<ChartDifficulty, List<ChartNote>>();
            foreach (var track in part.Tracks)
                notes[track.Difficulty] = Frets(track, resolution, options, starPower);
            parts.Add((part.Instrument, notes, starPower));
        }

        return GameNotes.Document(chart.TempoMap.ToTempoMap(), parts, drumsLayout);
    }

    /// <summary>
    /// One track of <see cref="Project(EditableChart, ChartReadOptions)"/>, worked out alone, for the editor to redraw a
    /// track after an edit without projecting the whole chart. Drums are worked out for the whole part, as the layout
    /// and the tom markers are the part's.
    /// </summary>
    /// <returns>The track as the games read it; empty when the chart hasn't the part or the track has no notes.</returns>
    public static NoteTrack Project(EditableChart chart, ChartTrackKey key, ChartReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(options);

        var part = chart.PartOf(key.Instrument);
        if (part is null || part.Instrument == ChartInstrument.Keys)
            return new NoteTrack([], []);

        var starPower = StarPowerOf(part);
        List<ChartNote>? notes;
        if (part.Kind == ChartInstrumentKind.Drums)
            DrumsOf(chart, part, options, starPower).TryGetValue(key.Difficulty, out notes);
        else
            notes = Frets(part[key.Difficulty], chart.Resolution, options, starPower);
        notes ??= [];
        return new NoteTrack(notes, starPower);
    }

    /// <summary>
    /// Every difficulty of <paramref name="instrument"/> as <see cref="Project(EditableChart, ChartTrackKey, ChartReadOptions)"/>
    /// works each out, the drums worked out once for them all.
    /// </summary>
    /// <returns>A track for each difficulty; empty where the chart hasn't the part or the difficulty has no notes.</returns>
    public static IReadOnlyDictionary<ChartDifficulty, NoteTrack> ProjectPart(EditableChart chart, ChartInstrument instrument, ChartReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(options);

        var tracks = new Dictionary<ChartDifficulty, NoteTrack>();
        var part = chart.PartOf(instrument);
        if (part is null || part.Instrument == ChartInstrument.Keys)
        {
            foreach (var difficulty in Enum.GetValues<ChartDifficulty>())
                tracks[difficulty] = new NoteTrack([], []);
            return tracks;
        }

        var starPower = StarPowerOf(part);
        var drums = part.Kind == ChartInstrumentKind.Drums ? DrumsOf(chart, part, options, starPower) : null;
        foreach (var difficulty in Enum.GetValues<ChartDifficulty>())
        {
            List<ChartNote>? notes = null;
            if (drums is not null)
                drums.TryGetValue(difficulty, out notes);
            else
                notes = Frets(part[difficulty], chart.Resolution, options, starPower);
            tracks[difficulty] = new NoteTrack(notes ?? [], starPower);
        }

        return tracks;
    }

    // The model's star power is always star power: the writer writes it on 116, and solos never become it.
    private static IReadOnlyList<StarPowerPhrase> StarPowerOf(EditablePart part) =>
        GameNotes.StarPower(part.Phrases.Where(p => p.Kind == PhraseKind.StarPower).Select(p => new TickSpan(p.Tick, p.Tick + p.Length)));

    private static Dictionary<ChartDifficulty, List<ChartNote>> DrumsOf(EditableChart chart, EditablePart part, ChartReadOptions options, IReadOnlyList<StarPowerPhrase> starPower)
    {
        var markers = SavedTomMarkers(chart, part);
        return Drums(part, markers, DrumsLayoutOf(part, markers, options), starPower);
    }

    /// <summary>Whether the games read <paramref name="chart"/>'s drums as five-lane, given <paramref name="options"/>.</summary>
    public static bool IsFiveLaneDrums(EditableChart chart, ChartReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(options);
        var drums = chart.Parts.FirstOrDefault(p => p.Kind == ChartInstrumentKind.Drums);
        return drums is not null && DrumsLayoutOf(drums, SavedTomMarkers(chart, drums), options) == DrumsLayout.FiveLane;
    }

    /// <summary>
    /// Where the games swap red and yellow on <paramref name="chart"/>'s drums (charter-spec C6 item 7), worked out as
    /// <see cref="Project(EditableChart, ChartReadOptions)"/> works out each drum note's: from the mix events the reader
    /// reads, the model's and any kept as text, and the drums' layout, given <paramref name="options"/>. None when the
    /// chart has no drums, and none unless the games read them as Pro.
    /// </summary>
    public static GameDiscoFlips DiscoFlips(EditableChart chart, ChartReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(options);
        var drums = chart.Parts.FirstOrDefault(p => p.Kind == ChartInstrumentKind.Drums);
        if (drums is null)
            return GameDiscoFlips.None;

        // The notes are in tick order, so each difficulty's last is its latest.
        var lastTicks = new Dictionary<ChartDifficulty, long>();
        foreach (var track in drums.Tracks)
        {
            if (track.Notes.Count > 0)
                lastTicks[track.Difficulty] = track.Notes[track.Notes.Count - 1].Tick;
        }

        return GameDiscoFlips.Read(lastTicks, GameNotes.MixesOf(TextsOf(drums)), DrumsLayoutOf(drums, SavedTomMarkers(chart, drums), options));
    }

    /// <summary>
    /// The drums' tom markers as the saved file has them: none for 4-lane drums, whose markers the writer leaves out, as
    /// the games would read any as Pro drums (charter-spec C6 item 9).
    /// </summary>
    internal static IReadOnlyList<TomMarker> SavedTomMarkers(EditableChart chart, EditablePart drums) =>
        chart.DrumsVariant == DrumsVariant.FourLane ? [] : drums.TomMarkers;

    /// <summary>
    /// The drums' layout, as <see cref="MidiChartReader.DrumsLayoutOf"/> works it out from the track the writer writes:
    /// song.ini's settings, then its fifth-pad note-ons and tom-marker note events in the order
    /// <see cref="PartEventOrder"/> puts them.
    /// </summary>
    internal static DrumsLayout DrumsLayoutOf(EditablePart drums, ChartReadOptions options) => DrumsLayoutOf(drums, drums.TomMarkers, options);

    /// <summary>
    /// The drums' layout, as <see cref="DrumsLayoutOf(EditablePart, ChartReadOptions)"/> works it out, with
    /// <paramref name="markers"/> as their tom markers.
    /// </summary>
    private static DrumsLayout DrumsLayoutOf(EditablePart drums, IReadOnlyList<TomMarker> markers, ChartReadOptions options)
    {
        // Each event as (tick, place among the tick's events, pitch, a note-off after its note-on), and whether it's a
        // fifth-pad note-on rather than a tom-marker event.
        var events = new List<((long, int, int, int) Key, bool FifthPad)>();
        foreach (var track in drums.Tracks)
        {
            int fifthPad = MidiLayout.BaseOf(ChartInstrumentKind.Drums, track.Difficulty) + 5;
            foreach (var note in track.Notes)
            {
                if (note.Lane == 5)
                    events.Add(((note.Tick, PartEventOrder.NoteOn, fifthPad, 0), true));
            }
        }

        foreach (var marker in markers)
        {
            int pitch = MidiLayout.FirstTomPitch + marker.Pad - TomMarker.FirstPad;
            events.Add(((marker.Tick, PartEventOrder.NoteOn, pitch, 0), false));
            events.Add((marker.Length == 0 ? (marker.Tick, PartEventOrder.NoteOn, pitch, 1) : (marker.Tick + marker.Length, PartEventOrder.NoteOff, pitch, 0), false));
        }

        events.Sort(static (a, b) => a.Key.CompareTo(b.Key));
        var layouts = new DrumsLayouts(options, midi: true);
        foreach (var (_, fifthPad) in events)
        {
            if (fifthPad)
                layouts.FifthPad();
            else
                layouts.ProMarker();
        }

        return layouts.Layout;
    }

    /// <summary>A fret difficulty's notes, as <see cref="MidiChartReader"/> reads them from the written track.</summary>
    private static List<ChartNote> Frets(EditableTrack track, int resolution, ChartReadOptions options, IReadOnlyList<StarPowerPhrase> starPower) =>
        Frets(track.Notes, track.FlaggedChords, resolution, options, starPower);

    /// <summary>
    /// 5-fret or GHL <paramref name="notes"/> with <paramref name="flags"/>, as the games read them: each note's HOPO and tap
    /// flags as a track of them would have, for a draft not yet in a chart.
    /// </summary>
    internal static List<ChartNote> Frets(IReadOnlyCollection<EditableNote> notes, IEnumerable<FlaggedChord> flags, int resolution, ChartReadOptions options, IReadOnlyList<StarPowerPhrase> starPower)
    {
        var gems = new List<Gem>(notes.Count);
        foreach (var note in notes)
            gems.Add(new Gem(note.Tick, note.Length, note.Lane));

        // A flagged chord is written as a marker over its tick.
        var forceHopo = new List<TickSpan>();
        var forceStrum = new List<TickSpan>();
        var taps = new List<TickSpan>();
        foreach (var chord in flags)
        {
            var span = new TickSpan(chord.Tick, chord.Tick + 1);
            if (chord.Flags.HasFlag(ChordFlags.ForceHopo))
                forceHopo.Add(span);
            if (chord.Flags.HasFlag(ChordFlags.ForceStrum))
                forceStrum.Add(span);
            if (chord.Flags.HasFlag(ChordFlags.Tap))
                taps.Add(span);
        }

        return GameNotes.Frets(
            gems,
            sysexOpens: [],
            forceHopo,
            forceStrum,
            taps,
            cutoff: SustainCutoff.Of(resolution, options.SustainCutoff),
            hopoThreshold: options.MidiHopoTicks ?? options.MidiHopoThresholds(resolution)[0],
            starPower);
    }

    /// <summary>Each difficulty's drum notes, as <see cref="MidiChartReader"/> reads them from the written track.</summary>
    private static Dictionary<ChartDifficulty, List<ChartNote>> Drums(EditablePart part, IReadOnlyList<TomMarker> markers, DrumsLayout layout, IReadOnlyList<StarPowerPhrase> starPower)
    {
        var hits = new Dictionary<ChartDifficulty, List<DrumHit>>();
        foreach (var track in part.Tracks)
        {
            // In the order they're let go, as PartEventOrder writes them: by tick, then the shorter first, then the
            // lower pitch, the 2x kick's being below the kick's. Only a kick and a 2x kick on one tick can share a pad.
            var list = new List<DrumHit>(track.Notes.Count);
            foreach (var note in track.Notes.OrderBy(static n => n.Tick).ThenBy(static n => n.Length).ThenBy(static n => n.DoubleKick ? -1 : n.Lane))
            {
                // YARG reads accents and ghosts on the pads alone: never the kick or the 2x kick. A note has them only
                // where the save writes [ENABLE_CHART_DYNAMICS] (MidiChartWriter.Normalize), so they're read as they are.
                var dynamics = note.Lane > 0 ? note.Dynamics.ToFlags() : NoteFlags.None;
                list.Add(new DrumHit(note.Tick, note.DoubleKick ? -1 : note.Lane, dynamics));
            }

            hits[track.Difficulty] = list;
        }

        IReadOnlyList<TickSpan>[] toms =
        [
            .. Enumerable.Range(TomMarker.FirstPad, TomMarker.LastPad - TomMarker.FirstPad + 1)
                .Select(pad => markers.Where(m => m.Pad == pad).Select(m => new TickSpan(m.Tick, m.Tick + m.Length)).ToList()),
        ];
        return GameNotes.Drums(hits, toms, TextsOf(part), starPower, layout);
    }

    /// <summary>
    /// The drums track's text events the reader reads, normalized, in the order <see cref="PartEventOrder"/> writes
    /// them: by tick, and at a tick the mixes (Easy's first) before the other text events.
    /// </summary>
    internal static List<(long Tick, string Text)> TextsOf(EditablePart part)
    {
        var texts = new List<(long Tick, int Place, string Text)>();
        foreach (var track in part.Tracks)
        {
            foreach (var mix in track.Mixes)
                texts.Add((mix.Tick, PartEventOrder.Mix, ChartTextEvents.Normalize(mix.TextFor(track.Difficulty))));
        }

        foreach (var e in part.TextEvents)
        {
            if (e.Type is TextEvent.TextType or (>= 0x04 and <= 0x09))
                texts.Add((e.Tick, PartEventOrder.Text, ChartTextEvents.Normalize(e.Text)));
        }

        // OrderBy is stable, so those alike keep the order they were added in.
        return [.. texts.OrderBy(static t => t.Tick).ThenBy(static t => t.Place).Select(static t => (t.Tick, t.Text))];
    }
}

/// <summary>
/// The order the writer puts a part track's events in at one tick, which <see cref="GameView"/> relies on (charter-spec
/// §4.3): the file reads as the game view says only when it's written this way.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>Events in tick order.</item>
/// <item>At a tick, first the note-offs of notes held for a tick or more (<see cref="NoteOff"/>), by ascending pitch,
/// so a shorter note is let go before a longer one that started with it, and of two let go together the lower pitch
/// first (a 2x kick, 95, before its kick, 96).</item>
/// <item>Then the drum mix events (<see cref="Mix"/>), Easy's first, before every other text and meta event
/// (<see cref="Text"/>), so a mix in the model gives way to a mix kept as text at its tick. <c>[ENHANCED_OPENS]</c> and
/// <c>[ENABLE_CHART_DYNAMICS]</c> are at tick 0 among the text events, ahead of every note's note-off.</item>
/// <item>Then the note-ons (<see cref="NoteOn"/>), by ascending pitch, each note held for no ticks let go straight after
/// its note-on. So a fifth-pad note (65, 77, 89, 101) starts before a tom marker (110 to 112) at its tick, and settles
/// the drums' layout first.</item>
/// </list>
/// </remarks>
internal static class PartEventOrder
{
    /// <summary>The note-offs of notes held for a tick or more, by ascending pitch.</summary>
    public const int NoteOff = 0;

    /// <summary>The drum mix events, Easy's first, each difficulty's in the model's order.</summary>
    public const int Mix = 1;

    /// <summary>The other text and meta events, in the model's order.</summary>
    public const int Text = 2;

    /// <summary>The note-ons, by ascending pitch, each note held for no ticks let go straight after its note-on.</summary>
    public const int NoteOn = 3;
}
