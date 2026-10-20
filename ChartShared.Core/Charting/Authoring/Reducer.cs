using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Authoring;

/// <summary>A draft of a lower difficulty (<see cref="Reducer"/>): the notes, the chords' forcing and tap, and the drum mixes.</summary>
/// <param name="Removed">How many of the source's notes it leaves out (in the range, when there is one).</param>
public sealed record ReductionResult(IReadOnlyList<EditableNote> Notes, IReadOnlyList<FlaggedChord> Flags, IReadOnlyList<DrumMix> Mixes, int Removed);

/// <summary>
/// How much each thing about a chord counts towards keeping it when thinning (<see cref="Reducer"/>), beside where it falls
/// in the measure: each step down from a measure's first beat counts −1.
/// </summary>
internal static class ThinningWeights
{
    /// <summary>A chord holding a sustain of a beat or longer.</summary>
    public const double Sustain = 1.5;

    /// <summary>A chord of two columns or more; on drums, a hit of two notes or more.</summary>
    public const double Chord = 0.5;

    /// <summary>The first chord after a rest of a beat or longer.</summary>
    public const double AfterRest = 1.0;

    /// <summary>A chord where the melody turns: up then down, or down then up.</summary>
    public const double DirectionChange = 0.5;

    /// <summary>The first or last chord of a phrase: a run of chords between rests and sections.</summary>
    public const double PhraseEdge = 1.0;

    /// <summary>What a step of the contour costs when it goes flat where the source moved, or moves where it stayed.</summary>
    public const double FlatStep = 4;

    /// <summary>What a step of the contour costs when it goes the other way to the source's.</summary>
    public const double ReversedStep = 8;
}

/// <summary>
/// The gap thinning keeps between chords: a grid step in ticks, and a time in seconds. Whichever is larger holds, so the
/// gap stays the same at a fast tempo as at a slow one.
/// </summary>
internal readonly record struct ThinningGap(long Ticks, double Seconds)
{
    /// <summary>Whether this keeps every chord.</summary>
    public bool IsNone => Ticks <= 1 && Seconds <= 0;
}

/// <summary>
/// Makes a draft of a lower difficulty from a higher one (charter-spec C9 item 2), as a starting point to edit. Nothing
/// here changes the chart: the draft is shown as a ghost, then added as one command.
/// </summary>
/// <remarks>
/// <para><b>Phrases</b> here are runs of chords split at rests of a beat or longer and at sections. Opens split none.</para>
/// <para><b>Thinning</b> scores every chord: where it falls (a measure's first beat, then a beat, then each halving of it),
/// with more for a sustain, a chord, the first chord after a rest, a turn of the melody, and a phrase's first or last chord
/// (<see cref="ThinningWeights"/>). Chords are taken best first where no chord taken already is closer than the gap, on
/// either side: the grid's step, or the rules' milliseconds, whichever is larger. Before them, each star power phrase and
/// solo takes its best chord, so none is left empty.</para>
/// <para><b>Chords</b> keep their outermost columns, or their lowest alone when only one is kept. A phrase past the highest
/// column moves down as a whole when it fits, so a trill stays a trill; one wider than the range is fitted to it with each
/// step kept going up, down or the same where it can be. Only notes a chord would push below green are pressed together.
/// On GHL a column is a white and a black fret together, and a note keeps its colour.</para>
/// <para><b>Drums</b> are thinned kick and pads together, then the kicks again on their own grid. Each hit keeps the pads a
/// player is likeliest to be asked for first: red, then cymbals, then toms, by the pads as shown inside a disco flip. The
/// source's mixes, which hold disco flip, are carried over. 2× kicks are left out, as only Expert has them.</para>
/// <para><b>A range</b> reduces only the source's notes inside it; the target keeps what it has outside, and the gap holds
/// against its notes either side.</para>
/// </remarks>
public static class Reducer
{
    /// <summary>A rank no strong position has, for a note off every grid of the beat.</summary>
    internal const int OffGrid = 99;

    /// <summary>
    /// A draft of <paramref name="to"/> of the 5-fret or GHL <paramref name="part"/>, from <paramref name="from"/>: in
    /// place of all of it, or only of <paramref name="range"/> (ticks, both ends in).
    /// </summary>
    public static ReductionResult Reduce(EditableChart chart, ChartInstrument part, ChartDifficulty from, ChartDifficulty to, FretReductionRules rules, (long From, long To)? range = null)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(rules);
        var source = SourceOf(chart, part, from, to);
        if (source.Kind == ChartInstrumentKind.Drums)
            throw new ArgumentException("Drums are reduced by the drum rules.", nameof(part));
        var whole = chart.PartOf(part)!;
        var target = whole[to];
        var map = chart.TempoMap.ToTempoMap();
        bool ghl = source.Kind == ChartInstrumentKind.SixFret;
        int Column(EditableNote n) => ghl ? n.Lane % 3 : n.Lane;

        var inRange = Inside(source.Notes, range);
        var chords = ChordsOf(inRange);
        var phrases = Phrases(chords, SectionTicks(chart), chart.Resolution);
        var anchors = chords.Select(c => Anchor(c.Notes, Column)).ToArray();
        var scores = Scores(chords, phrases, anchors, map, chart.Resolution, (c, i) => c.Notes.Where(static n => n.Lane != ChartNote.OpenLane).Select(Column).Distinct().Count() > 1);

        var gap = new ThinningGap(MinGap(rules.ThinningDivision, chart.Resolution), (rules.MinGapMs ?? 0) / 1000.0);
        var kept = Thin(chords, scores, gap, map, whole.Phrases, Outside(target.Notes, range));

        // Each kept chord's columns, then each phrase fitted to the range as a whole.
        int highest = rules.HighestLane;
        var columns = new Dictionary<int, List<int>>();
        foreach (int i in kept)
        {
            if (SelectColumns(chords[i].Notes, rules.MaxChordSize, Column) is { } chosen)
                columns[i] = chosen;
        }

        var placedAnchor = new Dictionary<int, int>();
        foreach (var (start, end) in phrases)
        {
            var frets = kept.Where(i => i >= start && i <= end && columns.ContainsKey(i)).ToList();
            var mapped = MapPhrase([.. frets.Select(i => columns[i][0])], [.. frets.Select(i => columns[i][^1] - columns[i][0])], highest);
            for (int k = 0; k < frets.Count; k++)
                placedAnchor[frets[k]] = mapped[k];
        }

        var notes = new List<EditableNote>();
        var flags = new List<FlaggedChord>();
        foreach (int i in kept)
        {
            var (tick, chord) = chords[i];
            var placed = chord.Where(static n => n.Lane == ChartNote.OpenLane).ToList();
            if (columns.TryGetValue(i, out var chosen))
                placed.AddRange(Place(chord, chosen, placedAnchor[i], highest, ghl, Column));
            foreach (var note in placed)
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

        int made = notes.Count;
        if (range is { } r)
        {
            notes.AddRange(target.Notes.AsSpan().ToArray().Where(n => n.Tick < r.From || n.Tick > r.To));
            flags.AddRange(target.FlaggedChords.AsSpan().ToArray().Where(f => f.Tick < r.From || f.Tick > r.To));
            flags.Sort(static (a, b) => a.Tick.CompareTo(b.Tick));
        }

        CutOverlappingSustains(notes);
        return new ReductionResult(notes, flags, [], inRange.Length - made);
    }

    /// <summary>
    /// A draft of <paramref name="to"/> of the drums, from <paramref name="from"/>, charted as <paramref name="variant"/>: in
    /// place of all of it, or only of <paramref name="range"/> (ticks, both ends in).
    /// </summary>
    public static ReductionResult Reduce(EditableChart chart, ChartDifficulty from, ChartDifficulty to, DrumReductionRules rules, DrumsVariant variant, (long From, long To)? range = null)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(rules);
        var source = SourceOf(chart, ChartInstrument.Drums, from, to);
        var drums = chart.PartOf(ChartInstrument.Drums)!;
        var target = drums[to];
        var map = chart.TempoMap.ToTempoMap();

        var inRange = Inside(source.Notes, range);
        var notes = inRange.Where(n => !n.DoubleKick || (rules.KeepDoubleKick && to == ChartDifficulty.Expert)).ToList();
        var hits = ChordsOf([.. notes]);
        var phrases = Phrases(hits, SectionTicks(chart), chart.Resolution);
        var scores = Scores(hits, phrases, null, map, chart.Resolution, static (h, _) => h.Notes.Length > 1);
        var gap = new ThinningGap(MinGap(rules.ThinningDivision, chart.Resolution), (rules.MinGapMs ?? 0) / 1000.0);
        var outside = Outside(target.Notes, range);
        var keptHits = Thin(hits, scores, gap, map, drums.Phrases, outside).Select(i => hits[i].Tick).ToHashSet();
        notes.RemoveAll(n => !keptHits.Contains(n.Tick));

        if (rules.KickDivision > 0)
        {
            var kicks = ChordsOf([.. notes.Where(static n => n.Lane == 0)]);
            var kickGap = new ThinningGap(MinGap(rules.KickDivision, chart.Resolution), 0);
            var keptKicks = Thin(kicks, [.. kicks.Select(k => -(double)BeatStrength(k.Tick, map))], kickGap, map, null, Outside(target.Notes, range, static n => n.Lane == 0))
                .Select(i => kicks[i].Tick).ToHashSet();
            notes.RemoveAll(n => n.Lane == 0 && !keptKicks.Contains(n.Tick));
        }

        var result = new List<EditableNote>(notes.Count);
        foreach (var hit in notes.GroupBy(static n => n.Tick))
        {
            result.AddRange(hit.Where(static n => n.Lane == 0));
            result.AddRange(hit.Where(static n => n.Lane > 0)
                .OrderBy(n => PadRank(drums, source, variant, n))
                .Take(rules.PadsPerHit));
        }

        int made = result.Count;
        var mixes = source.Mixes.AsSpan().ToArray().ToList();
        if (range is { } r)
        {
            result.AddRange(target.Notes.AsSpan().ToArray().Where(n => n.Tick < r.From || n.Tick > r.To));
            mixes = [.. target.Mixes.AsSpan().ToArray().Where(m => m.Tick < r.From || m.Tick > r.To), .. mixes.Where(m => m.Tick >= r.From && m.Tick <= r.To)];
            mixes.Sort(static (a, b) => a.Tick.CompareTo(b.Tick));
        }

        result.Sort(EditableNote.Order);
        return new ReductionResult(result, [], mixes, inRange.Length - made);
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
    /// The ticks of <paramref name="ticks"/> kept by thinning on position alone: stronger positions first, each where no
    /// tick kept is closer than <paramref name="minGap"/>, in tick order.
    /// </summary>
    internal static IReadOnlyList<long> Thin(IReadOnlyList<long> ticks, long minGap, TempoMap map)
    {
        var distinct = ticks.Distinct().Order().ToList();
        var kept = Thin(distinct, [.. distinct.Select(t => -(double)BeatStrength(t, map))], new ThinningGap(minGap, 0), map, [], []);
        return [.. kept.Select(i => distinct[i])];
    }

    /// <summary>
    /// The indexes of <paramref name="ticks"/> (in tick order, one per chord) kept by thinning: <paramref name="first"/>'s
    /// first, then the rest by <paramref name="scores"/>, best first and the earlier on a tie, each where no tick kept or in
    /// <paramref name="fixedTicks"/> is closer than <paramref name="gap"/>. In tick order.
    /// </summary>
    internal static List<int> Thin(IReadOnlyList<long> ticks, IReadOnlyList<double> scores, ThinningGap gap, TempoMap map, IEnumerable<int> first, IEnumerable<long> fixedTicks)
    {
        if (gap.IsNone)
            return [.. Enumerable.Range(0, ticks.Count)];

        var taken = new SortedSet<long>(fixedTicks);
        var kept = new List<int>();
        var tried = new HashSet<int>();
        void Try(int i)
        {
            if (tried.Add(i) && !taken.Contains(ticks[i]) && IsClear(taken, ticks[i], gap, map))
            {
                taken.Add(ticks[i]);
                kept.Add(i);
            }
        }

        foreach (int i in first)
            Try(i);
        foreach (int i in Enumerable.Range(0, ticks.Count).OrderByDescending(i => scores[i]).ThenBy(i => ticks[i]))
            Try(i);
        kept.Sort();
        return kept;
    }

    /// <summary>The fewest ticks two chords are apart on the grid of <paramref name="division"/>; 0 for none.</summary>
    internal static long MinGap(int division, int resolution) =>
        division <= 0 ? 0 : (long)Math.Ceiling(SnapGrid.Of(division).StepTicks(resolution) - 1e-9);

    /// <summary>
    /// The phrases of <paramref name="chords"/> (in tick order), as the first and last index of each: split where a chord
    /// starts a beat (<paramref name="resolution"/>) or more after the one before it ends, or at a section.
    /// </summary>
    internal static List<(int Start, int End)> Phrases(IReadOnlyList<(long Tick, EditableNote[] Notes)> chords, IReadOnlyList<long> sections, int resolution)
    {
        var phrases = new List<(int Start, int End)>();
        int start = 0;
        int section = 0;
        for (int i = 1; i <= chords.Count; i++)
        {
            bool split = i == chords.Count;
            if (!split)
            {
                long end = chords[i - 1].Tick + chords[i - 1].Notes.Max(static n => Math.Max(1, n.Length));
                split = chords[i].Tick - end >= resolution;
                while (section < sections.Count && sections[section] <= chords[i - 1].Tick)
                    section++;
                if (section < sections.Count && sections[section] <= chords[i].Tick)
                    split = true;
            }

            if (split)
            {
                if (chords.Count > 0)
                    phrases.Add((start, i - 1));
                start = i;
            }
        }

        return phrases;
    }

    /// <summary>
    /// Where each chord of a phrase goes, by its lowest column (<paramref name="anchors"/>) and how wide it is
    /// (<paramref name="widths"/>), so none reaches past <paramref name="highest"/>: where they are when they fit, the
    /// phrase moved down as a whole when it fits, or else fitted to the range with each step kept going up, down or the
    /// same where it can be.
    /// </summary>
    internal static int[] MapPhrase(IReadOnlyList<int> anchors, IReadOnlyList<int> widths, int highest)
    {
        int n = anchors.Count;
        if (n == 0)
            return [];
        var width = widths.Select(w => Math.Min(w, highest)).ToArray();
        int top = Enumerable.Range(0, n).Max(i => anchors[i] + width[i]);
        if (top <= highest)
            return [.. anchors];

        int bottom = anchors.Min();
        if (top - bottom <= highest)
            return [.. anchors.Select(a => a - (top - highest))];

        // Wider than the range: the fewest steps turned, then the nearest to the phrase scaled into it.
        double scale = (double)highest / (top - bottom);
        var cost = new double[n, highest + 1];
        var back = new int[n, highest + 1];
        for (int i = 0; i < n; i++)
        {
            double ideal = (anchors[i] - bottom) * scale;
            for (int v = 0; v <= highest; v++)
            {
                cost[i, v] = double.PositiveInfinity;
                if (v > highest - width[i])
                    continue;
                double here = Math.Abs(v - ideal);
                if (i == 0)
                {
                    cost[i, v] = here;
                    continue;
                }

                int wanted = Math.Sign(anchors[i] - anchors[i - 1]);
                for (int u = 0; u <= highest; u++)
                {
                    if (double.IsPositiveInfinity(cost[i - 1, u]))
                        continue;
                    int made = Math.Sign(v - u);
                    double step = made == wanted ? 0 : made == -wanted && wanted != 0 ? ThinningWeights.ReversedStep : ThinningWeights.FlatStep;
                    double total = cost[i - 1, u] + step + here;
                    if (total < cost[i, v] - 1e-9)
                    {
                        cost[i, v] = total;
                        back[i, v] = u;
                    }
                }
            }
        }

        var placed = new int[n];
        int best = 0;
        for (int v = 1; v <= highest; v++)
        {
            if (cost[n - 1, v] < cost[n - 1, best] - 1e-9)
                best = v;
        }

        for (int i = n - 1; i >= 0; i--)
        {
            placed[i] = best;
            best = back[i, best];
        }

        return placed;
    }

    private static EditableTrack SourceOf(EditableChart chart, ChartInstrument part, ChartDifficulty from, ChartDifficulty to)
    {
        if (to >= from)
            throw new ArgumentException("A difficulty is reduced to a lower one.", nameof(to));
        return (chart.PartOf(part) ?? throw new InvalidOperationException($"The chart has no {part} part."))[from];
    }

    private static EditableNote[] Inside(TickList<EditableNote> notes, (long From, long To)? range) =>
        range is { } r ? notes.Between(r.From, r.To).ToArray() : notes.AsSpan().ToArray();

    // The target's chords outside the range, which the gap holds against; none when the draft replaces the whole track.
    private static long[] Outside(TickList<EditableNote> notes, (long From, long To)? range, Func<EditableNote, bool>? which = null) =>
        range is { } r
            ? [.. notes.AsSpan().ToArray().Where(n => (n.Tick < r.From || n.Tick > r.To) && (which is null || which(n))).Select(static n => n.Tick).Distinct()]
            : [];

    private static List<(long Tick, EditableNote[] Notes)> ChordsOf(EditableNote[] notes) =>
        [.. notes.GroupBy(static n => n.Tick).OrderBy(static g => g.Key).Select(static g => (g.Key, g.ToArray()))];

    private static long[] SectionTicks(EditableChart chart) => [.. SectionEvents.All(chart).Select(static e => e.Tick)];

    // A chord's lowest column, or null for one of opens alone.
    private static int? Anchor(EditableNote[] chord, Func<EditableNote, int> column)
    {
        var frets = chord.Where(static n => n.Lane != ChartNote.OpenLane).ToList();
        return frets.Count == 0 ? null : frets.Min(column);
    }

    // How readily each chord is kept (ThinningWeights); anchors give the melody's turns, and are null for drums.
    private static double[] Scores(List<(long Tick, EditableNote[] Notes)> chords, List<(int Start, int End)> phrases, int?[]? anchors, TempoMap map, int resolution, Func<(long Tick, EditableNote[] Notes), int, bool> isChord)
    {
        var scores = new double[chords.Count];
        for (int i = 0; i < chords.Count; i++)
        {
            scores[i] = -BeatStrength(chords[i].Tick, map);
            if (chords[i].Notes.Any(n => n.Length >= resolution))
                scores[i] += ThinningWeights.Sustain;
            if (isChord(chords[i], i))
                scores[i] += ThinningWeights.Chord;
        }

        for (int p = 0; p < phrases.Count; p++)
        {
            var (start, end) = phrases[p];
            if (p > 0)
                scores[start] += ThinningWeights.AfterRest;
            scores[start] += ThinningWeights.PhraseEdge;
            if (end != start)
                scores[end] += ThinningWeights.PhraseEdge;
            if (anchors is null)
                continue;

            // A turn: the fretted chords either side are both higher, or both lower.
            var frets = Enumerable.Range(start, end - start + 1).Where(i => anchors[i] is not null).ToList();
            for (int k = 1; k < frets.Count - 1; k++)
            {
                int before = Math.Sign(anchors[frets[k]]!.Value - anchors[frets[k - 1]]!.Value);
                int after = Math.Sign(anchors[frets[k + 1]]!.Value - anchors[frets[k]]!.Value);
                if (before != 0 && after != 0 && before != after)
                    scores[frets[k]] += ThinningWeights.DirectionChange;
            }
        }

        return scores;
    }

    // The chords kept: each star power phrase and solo's best first, then the rest by score.
    private static List<int> Thin(List<(long Tick, EditableNote[] Notes)> chords, double[] scores, ThinningGap gap, TempoMap map, TickList<Phrase>? phrases, IEnumerable<long> fixedTicks)
    {
        var ticks = chords.Select(static c => c.Tick).ToArray();
        var first = new List<int>();
        if (phrases is not null)
        {
            foreach (var phrase in phrases.AsSpan())
            {
                if (phrase.Kind is not (PhraseKind.StarPower or PhraseKind.Solo))
                    continue;
                int lo = Array.BinarySearch(ticks, phrase.Tick);
                lo = lo < 0 ? ~lo : lo;
                int best = -1;
                for (int i = lo; i < ticks.Length && phrase.Covers(ticks[i]); i++)
                {
                    if (best < 0 || scores[i] > scores[best])
                        best = i;
                }

                if (best >= 0)
                    first.Add(best);
            }
        }

        return Thin(ticks, scores, gap, map, first, fixedTicks);
    }

    // Whether no tick taken is closer to tick than the gap, in ticks or in time.
    private static bool IsClear(SortedSet<long> taken, long tick, ThinningGap gap, TempoMap map)
    {
        long lo = tick - Math.Max(0, gap.Ticks - 1);
        long hi = tick + Math.Max(0, gap.Ticks - 1);
        double at = 0;
        if (gap.Seconds > 0)
        {
            at = map.SecondsAt(tick);
            lo = Math.Min(lo, (long)Math.Floor(map.TickAt(at - gap.Seconds)));
            hi = Math.Max(hi, (long)Math.Ceiling(map.TickAt(at + gap.Seconds)));
        }

        foreach (long other in taken.GetViewBetween(lo, hi))
        {
            if (Math.Abs(other - tick) < gap.Ticks)
                return false;
            if (gap.Seconds > 0 && Math.Abs(map.SecondsAt(other) - at) < gap.Seconds - 1e-9)
                return false;
        }

        return true;
    }

    // The columns a chord keeps, lowest first: its outermost, then inner ones up to the limit; null for opens alone.
    private static List<int>? SelectColumns(EditableNote[] chord, int maxChordSize, Func<EditableNote, int> column)
    {
        var columns = chord.Where(static n => n.Lane != ChartNote.OpenLane).Select(column).Distinct().Order().ToList();
        if (columns.Count == 0)
            return null;
        if (maxChordSize <= 1 || columns.Count == 1)
            return [columns[0]];

        var keep = new List<int> { columns[0], columns[^1] };
        foreach (int c in columns.Skip(1).SkipLast(1))
        {
            if (keep.Count >= maxChordSize)
                break;
            keep.Add(c);
        }

        keep.Sort();
        return keep;
    }

    // A chord's kept frets with its lowest column at anchor, its shape kept; only notes past the highest column move down,
    // as a whole, and those that would go below green are pressed together, the longer sustain kept.
    private static IEnumerable<EditableNote> Place(EditableNote[] chord, List<int> columns, int anchor, int highest, bool ghl, Func<EditableNote, int> column)
    {
        int offset = anchor - columns[0];
        int shift = Math.Max(0, columns[^1] + offset - highest);
        var placed = new Dictionary<int, EditableNote>();
        foreach (var note in chord.Where(n => n.Lane != ChartNote.OpenLane && columns.Contains(column(n))).OrderBy(static n => n.Lane))
        {
            int moved = Math.Max(0, column(note) + offset - shift);
            int lane = ghl ? (note.Lane / 3 * 3) + moved : moved;
            if (!placed.TryGetValue(lane, out var there) || note.Length > there.Length)
                placed[lane] = note with { Lane = lane };
        }

        return placed.Values.OrderBy(static n => n.Lane);
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
