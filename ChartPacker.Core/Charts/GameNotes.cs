using System.Runtime.InteropServices;

namespace ChartPacker.Core.Charts;

/// <summary>A marker or phrase of a chart, from its start to its end, in ticks.</summary>
internal readonly record struct TickSpan(long Start, long End)
{
    /// <summary>Whether it covers <paramref name="tick"/>: from its start to the tick before its end, or its start if it has no length.</summary>
    public bool Covers(long tick) => tick == Start || (tick > Start && tick < End);
}

/// <summary>A drum note as a chart's lanes have it, before its tom markers and the drums' layout shape it.</summary>
/// <param name="Pad">0 the kick to 5 the fifth pad, as <see cref="DrumGem.Pad"/> has them, or -1 the Expert+ double kick.</param>
/// <param name="Dynamics"><see cref="NoteFlags.Accent"/>, <see cref="NoteFlags.Ghost"/>, or none, as <see cref="DrumGem.Dynamics"/> has them.</param>
internal readonly record struct DrumHit(long Tick, int Pad, NoteFlags Dynamics = NoteFlags.None);

/// <summary>
/// Turns a MIDI chart's lanes into the notes YARG plays (<c>MoonSongLoader</c> in YARG.Core), once each note's
/// lane is known: the notes, markers and phrases of a track as ticks and lanes, wherever they were read from.
/// </summary>
internal static class GameNotes
{
    /// <summary>
    /// A fret difficulty's notes. In tick order, those at a tick in the order they're let go, the first of each
    /// lane at a tick is kept; then every note a Phase Shift open phrase covers is open, so two on a tick can both
    /// become opens. Force markers make a chord a HOPO or a strum, and tap markers a tap.
    /// </summary>
    /// <param name="gems">The notes in the order they're let go, each held for as long as it's written: sorted, cut down, and given their sustains in place.</param>
    /// <param name="sysexOpens">The Phase Shift open phrases.</param>
    /// <param name="taps">The tap markers and Phase Shift tap phrases.</param>
    /// <param name="cutoff">A note held for less is a note without a sustain; see <see cref="SustainCutoff.Of"/>.</param>
    /// <param name="hopoThreshold">How close a note must follow the one before to be a natural HOPO.</param>
    public static List<ChartNote> Frets(
        List<Gem> gems,
        IReadOnlyList<TickSpan> sysexOpens,
        IReadOnlyList<TickSpan> forceHopo,
        IReadOnlyList<TickSpan> forceStrum,
        IReadOnlyList<TickSpan> taps,
        long cutoff,
        long hopoThreshold,
        IReadOnlyList<StarPowerPhrase> starPower)
    {
        TickOrder.Sort(gems, static g => g.Tick);
        TickOrder.KeepFirstOfEach(gems, static g => g.Tick, static g => g.Lane);

        // Phase Shift open phrases make every note in them open.
        var items = CollectionsMarshal.AsSpan(gems);
        for (int i = 0; i < items.Length; i++)
        {
            var gem = items[i];
            items[i] = gem with
            {
                Length = gem.Length >= cutoff ? gem.Length : 0,
                Lane = Covered(sysexOpens, gem.Tick) ? ChartNote.OpenLane : gem.Lane,
            };
        }

        var marks = new Func<long, FretMarks>(tick => new FretMarks(false, Covered(forceHopo, tick), Covered(forceStrum, tick), Covered(taps, tick)));
        return NoteBuilder.Frets(gems, hopoThreshold, marks, starPower, midiChordRule: true);
    }

    /// <summary>
    /// Each difficulty's drum notes, as <see cref="DrumNotes.Build"/> shapes them for <paramref name="layout"/>.
    /// Yellow, blue and the fourth pad are cymbals, each tom marker over one toggling it; in tick order, those at a
    /// tick in the order they're let go, the first of each pad at a tick is kept, a double kick being the kick's pad.
    /// </summary>
    /// <param name="hits">Each difficulty's notes, in the order they're let go.</param>
    /// <param name="toms">The tom markers over yellow, blue and the fourth pad, in that order.</param>
    /// <param name="texts">The track's text events, in order, its drums mix events among them.</param>
    /// <param name="starPower">The star power every difficulty shares.</param>
    public static Dictionary<ChartDifficulty, List<ChartNote>> Drums(
        IReadOnlyDictionary<ChartDifficulty, List<DrumHit>> hits,
        IReadOnlyList<IReadOnlyList<TickSpan>> toms,
        IReadOnlyList<(long Tick, string Text)> texts,
        IReadOnlyList<StarPowerPhrase> starPower,
        DrumsLayout layout)
    {
        var gems = new Dictionary<ChartDifficulty, List<DrumGem>>();
        var mixes = new Dictionary<ChartDifficulty, List<(long, bool)>>();
        foreach (var difficulty in Enum.GetValues<ChartDifficulty>())
        {
            var of = hits.GetValueOrDefault(difficulty) ?? [];
            var list = new List<DrumGem>(of.Count);
            foreach (var hit in of)
            {
                bool cymbal = hit.Pad is >= 2 and <= 4 && CountCovering(toms[hit.Pad - 2], hit.Tick) % 2 == 0;
                list.Add(new DrumGem(hit.Tick, Math.Max(0, hit.Pad), cymbal, DoubleKick: hit.Pad == -1, hit.Dynamics));
            }

            TickOrder.Sort(list, static g => g.Tick);
            TickOrder.KeepFirstOfEach(list, static g => g.Tick, static g => g.Pad);

            gems[difficulty] = list;
            mixes[difficulty] =
            [
                .. texts
                    .Select(t => ChartTextEvents.TryParseMix(t.Text, out var d, out bool flip) && d == difficulty ? (t.Tick, (bool?)flip) : (t.Tick, null))
                    .Where(m => m.Item2 is not null)
                    .Select(m => (m.Tick, m.Item2!.Value)),
            ];
        }

        var starPowerOf = Enum.GetValues<ChartDifficulty>().ToDictionary(d => d, _ => starPower);
        return DrumNotes.Build(gems, mixes, starPowerOf, layout);
    }

    /// <summary>The star-power phrases of <paramref name="spans"/> in order, the first given of two that start together kept.</summary>
    public static List<StarPowerPhrase> StarPower(IEnumerable<TickSpan> spans) =>
    [
        .. spans.OrderBy(s => s.Start).DistinctBy(s => s.Start).Select(s => new StarPowerPhrase(s.Start, s.End - s.Start)),
    ];

    /// <summary>
    /// The chart of <paramref name="tempo"/> and <paramref name="parts"/>, each instrument's star power shared by
    /// its difficulties, with only the difficulties that have notes.
    /// </summary>
    /// <param name="drumsLayout">How the drums are laid out, if there are drums.</param>
    public static ChartDocument Document(
        TempoMap tempo,
        IEnumerable<(ChartInstrument Instrument, IReadOnlyDictionary<ChartDifficulty, List<ChartNote>> Notes, IReadOnlyList<StarPowerPhrase> StarPower)> parts,
        DrumsLayout? drumsLayout)
    {
        var result = new Dictionary<ChartTrackKey, NoteTrack>();
        foreach (var (instrument, byDifficulty, starPower) in parts)
        {
            foreach (var difficulty in Enum.GetValues<ChartDifficulty>())
            {
                if (byDifficulty.TryGetValue(difficulty, out var notes) && notes.Count > 0)
                    result[new ChartTrackKey(instrument, difficulty)] = new NoteTrack(notes, starPower);
            }
        }

        return new ChartDocument(tempo, result, null, drumsLayout == DrumsLayout.FiveLane) { IsProDrums = drumsLayout == DrumsLayout.Pro };
    }

    // Loops rather than LINQ: these run for every note, and a lambda capturing the tick would be an allocation each
    // time. Indexed rather than foreach, which would box the list's enumerator.
    private static bool Covered(IReadOnlyList<TickSpan> spans, long tick)
    {
        for (int i = 0; i < spans.Count; i++)
        {
            if (spans[i].Covers(tick))
                return true;
        }

        return false;
    }

    private static int CountCovering(IReadOnlyList<TickSpan> spans, long tick)
    {
        int count = 0;
        for (int i = 0; i < spans.Count; i++)
        {
            if (spans[i].Covers(tick))
                count++;
        }

        return count;
    }
}
