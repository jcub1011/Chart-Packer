using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Authoring;

/// <summary>A draft of a lower difficulty (<see cref="Reducer"/>): the notes, the chords' forcing and tap, and the drum mixes.</summary>
/// <param name="Removed">How many of the source's notes it leaves out.</param>
public sealed record ReductionResult(IReadOnlyList<EditableNote> Notes, IReadOnlyList<FlaggedChord> Flags, IReadOnlyList<DrumMix> Mixes, int Removed);

/// <summary>
/// Makes a draft of a lower difficulty from a higher one (charter-spec C9 item 2), as a starting point to edit. Nothing
/// here changes the chart: the draft is shown as a ghost, then added as one command.
/// </summary>
/// <remarks>
/// <para><b>Thinning</b> keeps notes on strong beats first: every chord is ranked by where it falls (a measure's first beat,
/// then a beat, then each halving of it, then the rest), and taken in that order where no chord taken already is closer than
/// one step of the grid, on either side. So no two chords of the draft are closer than the grid.</para>
/// <para><b>Chords</b> keep their outermost columns, or their lowest alone when only one is kept. A chord past the highest
/// column allowed moves down as a whole, keeping its shape, and only notes it would push below green are pressed together.
/// On GHL a column is a white and a black fret together, and a note keeps its colour.</para>
/// <para><b>Drums</b> are thinned kick and pads together, then the kicks again on their own grid. Each hit keeps the pads a
/// player is likeliest to be asked for first: red, then cymbals, then toms, by the pads as shown inside a disco flip. The
/// source's mixes, which hold disco flip, are carried over. 2× kicks are left out, as only Expert has them.</para>
/// </remarks>
public static class Reducer
{
    /// <summary>A rank no strong position has, for a note off every grid of the beat.</summary>
    internal const int OffGrid = 99;

    /// <summary>A draft of <paramref name="to"/> of the 5-fret or GHL <paramref name="part"/>, from <paramref name="from"/>.</summary>
    public static ReductionResult Reduce(EditableChart chart, ChartInstrument part, ChartDifficulty from, ChartDifficulty to, FretReductionRules rules)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(rules);
        var source = SourceOf(chart, part, from, to);
        if (source.Kind == ChartInstrumentKind.Drums)
            throw new ArgumentException("Drums are reduced by the drum rules.", nameof(part));
        var map = chart.TempoMap.ToTempoMap();
        bool ghl = source.Kind == ChartInstrumentKind.SixFret;

        var kept = Thin([.. source.Notes.Select(static n => n.Tick).Distinct()], MinGap(rules.ThinningDivision, chart.Resolution), map);
        var notes = new List<EditableNote>();
        var flags = new List<FlaggedChord>();
        foreach (long tick in kept)
        {
            var chord = ReduceChord(source.Notes.At(tick).ToArray(), rules, ghl);
            foreach (var note in chord)
            {
                long length = note.Length;
                if (length < chart.Resolution && !rules.KeepShortSustains)
                    length = EditableNote.PlainLength;
                notes.Add(note with { Length = length });
            }

            var marked = source.FlagsAt(tick);
            if (!rules.KeepForcedHopoAndTap)
                marked &= ~(ChordFlags.ForceHopo | ChordFlags.Tap);
            if (marked != ChordFlags.None)
                flags.Add(new FlaggedChord(tick, marked));
        }

        CutOverlappingSustains(notes);
        return new ReductionResult(notes, flags, [], source.Notes.Count - notes.Count);
    }

    /// <summary>A draft of <paramref name="to"/> of the drums, from <paramref name="from"/>, charted as <paramref name="variant"/>.</summary>
    public static ReductionResult Reduce(EditableChart chart, ChartDifficulty from, ChartDifficulty to, DrumReductionRules rules, DrumsVariant variant)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(rules);
        var source = SourceOf(chart, ChartInstrument.Drums, from, to);
        var drums = chart.PartOf(ChartInstrument.Drums)!;
        var map = chart.TempoMap.ToTempoMap();

        var notes = source.Notes.Where(n => !n.DoubleKick || (rules.KeepDoubleKick && to == ChartDifficulty.Expert)).ToList();
        var hits = Thin([.. notes.Select(static n => n.Tick).Distinct()], MinGap(rules.ThinningDivision, chart.Resolution), map).ToHashSet();
        notes.RemoveAll(n => !hits.Contains(n.Tick));

        if (rules.KickDivision > 0)
        {
            var kicks = Thin([.. notes.Where(static n => n.Lane == 0).Select(static n => n.Tick).Distinct()], MinGap(rules.KickDivision, chart.Resolution), map).ToHashSet();
            notes.RemoveAll(n => n.Lane == 0 && !kicks.Contains(n.Tick));
        }

        var result = new List<EditableNote>(notes.Count);
        foreach (var hit in notes.GroupBy(static n => n.Tick))
        {
            result.AddRange(hit.Where(static n => n.Lane == 0));
            result.AddRange(hit.Where(static n => n.Lane > 0)
                .OrderBy(n => PadRank(drums, source, variant, n))
                .Take(rules.PadsPerHit));
        }

        result.Sort(EditableNote.Order);
        return new ReductionResult(result, [], [.. source.Mixes], source.Notes.Count - result.Count);
    }

    /// <summary>
    /// How strong a position <paramref name="tick"/> is in its measure: 0 for a measure's first beat, 1 for a beat, then 2,
    /// 3, … for each halving of the beat, triplets ranking with the halving they're nearest; <see cref="OffGrid"/> for none.
    /// </summary>
    internal static int BeatStrength(long tick, TempoMap map)
    {
        var signatures = map.TimeSignatures;
        var signature = signatures[0];
        foreach (var s in signatures)
        {
            if (s.Tick > tick)
                break;
            signature = s;
        }

        long beat = Math.Max(1, map.Resolution * 4L / signature.Denominator);
        long offset = Math.Max(0, tick - signature.Tick);
        long within = offset % beat;
        if (within == 0)
            return (offset / beat) % Math.Max(1, signature.Numerator) == 0 ? 0 : 1;

        for (int level = 1; level <= 6; level++)
        {
            long half = beat >> level;
            if (half > 0 && beat % (1L << level) == 0 && within % half == 0)
                return 1 + level;

            // Triplets of this level: a third of the beat, a sixth, and so on, ranked just after the halving.
            long third = beat / (3L << (level - 1));
            if (third > 0 && beat % (3L << (level - 1)) == 0 && within % third == 0)
                return 2 + level;
        }

        return OffGrid;
    }

    /// <summary>
    /// The ticks of <paramref name="ticks"/> kept by thinning: stronger positions first, each where no tick kept is closer
    /// than <paramref name="minGap"/>, in tick order.
    /// </summary>
    internal static IReadOnlyList<long> Thin(IReadOnlyList<long> ticks, long minGap, TempoMap map)
    {
        if (minGap <= 1)
            return [.. ticks.Distinct().Order()];

        var kept = new SortedSet<long>();
        foreach (long tick in ticks.Distinct().OrderBy(t => BeatStrength(t, map)).ThenBy(static t => t))
        {
            if (kept.GetViewBetween(tick - minGap + 1, tick + minGap - 1).Count == 0)
                kept.Add(tick);
        }

        return [.. kept];
    }

    /// <summary>The fewest ticks two chords are apart on the grid of <paramref name="division"/>; 0 for none.</summary>
    internal static long MinGap(int division, int resolution) =>
        division <= 0 ? 0 : (long)Math.Ceiling(SnapGrid.Of(division).StepTicks(resolution) - 1e-9);

    private static EditableTrack SourceOf(EditableChart chart, ChartInstrument part, ChartDifficulty from, ChartDifficulty to)
    {
        if (to >= from)
            throw new ArgumentException("A difficulty is reduced to a lower one.", nameof(to));
        return (chart.PartOf(part) ?? throw new InvalidOperationException($"The chart has no {part} part."))[from];
    }

    // One chord, kept to the columns and range the rules allow.
    private static List<EditableNote> ReduceChord(EditableNote[] chord, FretReductionRules rules, bool ghl)
    {
        var opens = chord.Where(static n => n.Lane == ChartNote.OpenLane).ToList();
        var frets = chord.Where(static n => n.Lane != ChartNote.OpenLane).ToList();
        if (frets.Count == 0)
            return opens;

        int Column(EditableNote n) => ghl ? n.Lane % 3 : n.Lane;
        var columns = frets.Select(Column).Distinct().Order().ToList();
        var keep = new List<int>();
        if (rules.MaxChordSize <= 1 || columns.Count == 1)
        {
            keep.Add(columns[0]);
        }
        else
        {
            keep.Add(columns[0]);
            keep.Add(columns[^1]);
            foreach (int column in columns.Skip(1).SkipLast(1))
            {
                if (keep.Count >= rules.MaxChordSize)
                    break;
                keep.Add(column);
            }
        }

        // Down as a whole past the highest column, then pressed together only below the lowest.
        int shift = Math.Max(0, keep.Max() - rules.HighestLane);
        var placed = new Dictionary<int, EditableNote>();
        foreach (var note in frets.Where(n => keep.Contains(Column(n))).OrderBy(static n => n.Lane))
        {
            int column = Math.Max(0, Column(note) - shift);
            int lane = ghl ? (note.Lane / 3 * 3) + column : column;
            if (!placed.TryGetValue(lane, out var there) || note.Length > there.Length)
                placed[lane] = note with { Lane = lane };
        }

        return [.. placed.Values.OrderBy(static n => n.Lane)];
    }

    // A sustain held into the next note on its lane ends there.
    private static void CutOverlappingSustains(List<EditableNote> notes)
    {
        notes.Sort(EditableNote.Order);
        var next = new Dictionary<int, long>();
        for (int i = notes.Count - 1; i >= 0; i--)
        {
            var note = notes[i];
            if (next.TryGetValue(note.Lane, out long after) && note.Tick + note.Length > after)
                notes[i] = note with { Length = Math.Max(EditableNote.PlainLength, after - note.Tick) };
            next[note.Lane] = note.Tick;
        }
    }

    // How readily a pad is kept at a hit: red, then the cymbals, then the toms, as the player sees them.
    private static int PadRank(EditablePart drums, EditableTrack track, DrumsVariant variant, EditableNote note)
    {
        int shown = note.Lane;
        bool flipped = variant == DrumsVariant.Pro && note.Lane is 1 or 2 && DiscoFlips.IsFlippedAt(track, note.Tick);
        if (flipped)
            shown = 3 - note.Lane;
        return variant switch
        {
            DrumsVariant.FiveLane => shown switch { 1 => 0, 2 => 1, 4 => 2, 3 => 3, _ => 4 },
            DrumsVariant.Pro => shown switch
            {
                1 => 0,
                2 when flipped => 1,
                _ when DrumCymbals.IsTom(drums, shown, note.Tick) => 10 + shown,
                _ => shown,
            },
            _ => shown,
        };
    }
}
