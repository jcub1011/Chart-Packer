using System.Text;
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

    /// <summary>
    /// How far below the stronger position it anticipates a syncopated chord ranks: one off the beat, with nothing on the
    /// next stronger position, counts as that position, less this, so a chord on that position would still go first.
    /// </summary>
    public const double Anticipation = 0.5;

    /// <summary>What a step of the contour costs when it goes flat where the source moved, or moves where it stayed.</summary>
    public const double FlatStep = 4;

    /// <summary>What a step of the contour costs when it goes the other way to the source's.</summary>
    public const double ReversedStep = 8;

    /// <summary>
    /// What a chord costs for each column it's made narrower by, fitting a phrase, beside each column it's away from where
    /// it would be: more, so a chord is moved before it's narrowed, and far less than a step turned.
    /// </summary>
    public const double Narrowed = 1.5;
}

/// <summary>
/// The spacing thinning keeps, in ticks, so in fractions of a beat whatever the tempo: chords on average no closer than
/// <paramref name="Pace"/>, so any <paramref name="Burst"/> + 1 in a row span at least <paramref name="Burst"/> of its gaps,
/// and none ever closer than <paramref name="Floor"/>. A burst of up to <paramref name="Burst"/> chords can come closer than
/// the pace, with room around it to make up.
/// </summary>
internal readonly record struct ThinningSpacing(long Pace, long Floor, int Burst)
{
    /// <summary>Whether this keeps every chord.</summary>
    public bool IsNone => Pace <= 1;

    /// <summary>Whether every two chords are kept the pace apart, with no bursts.</summary>
    public bool IsEven => Burst <= 1 || Floor >= Pace;

    /// <summary>Every two chords at least <paramref name="gap"/> ticks apart.</summary>
    public static ThinningSpacing Even(long gap) => new(gap, gap, 1);
}

/// <summary>
/// A measure charted the same more than once (<see cref="Reducer.ShareRiffScores"/>): the index of its first chord in each
/// measure it's in, and how many chords it has.
/// </summary>
internal sealed record Riff(int Length, IReadOnlyList<int> Starts);

/// <summary>
/// Makes a draft of a lower difficulty from a higher one (charter-spec C9 item 2), as a starting point to edit. Nothing
/// here changes the chart: the draft is shown as a ghost, then added as one command.
/// </summary>
/// <remarks>
/// <para><b>Phrases</b> here are runs of chords split at rests of a beat or longer and at sections. Opens split none.</para>
/// <para><b>Thinning</b> scores every chord: where it falls (a measure's first beat, then a beat, then each halving of it),
/// with more for a sustain, a chord, the first chord after a rest, a turn of the melody, and a phrase's first or last chord
/// (<see cref="ThinningWeights"/>). A chord off the beat with nothing on the next stronger position anticipates it, and
/// counts as nearly that position. A measure charted the same as another shares its scores, so a riff is thinned the same
/// each time. Chords are taken best first where they keep the spacing (<see cref="ThinningSpacing"/>): on average no closer
/// than the thinning grid's step, and in a short burst the source has, with room around it, no closer than the burst's
/// grid; a steady stream thins evenly. Every gap is a fraction of a beat, so a faster song keeps as many notes a beat as a
/// slower one, and is harder. Before them, each star power phrase and solo takes its best chord the spacing allows, so none
/// is left empty unless the spacing leaves it none.</para>
/// <para><b>Chords</b> keep their outermost columns, or their lowest alone when only one is kept. A phrase past the highest
/// column moves down as a whole when it fits, so a trill stays a trill; one wider than the range is fitted to it with each
/// step kept going up, down or the same where it can be. Only notes a chord would push below green are pressed together.
/// On GHL a column is a white and a black fret together, and a note keeps its colour. Where the rules fret opens, a phrase
/// with opens played alone and pressed notes both, which is hard to play, has each open made green, one with frets pressed
/// with them, and each fret next to an open alone lifted a column and kept off green, so going between them is still a
/// move; a phrase without an open alone keeps its opens, as does one where green is the highest column.</para>
/// <para><b>Drums</b> are thinned kick and pads together, then the kicks again on their own grid. Each hit keeps the pads a
/// player is likeliest to be asked for first: red, then cymbals, then toms, by the pads as shown inside a disco flip. The
/// source's mixes, which hold disco flip, are carried over. 2× kicks are left out, as only Expert has them.</para>
/// <para><b>A range</b> reduces only the source's notes inside it; the target keeps what it has outside, and the gap holds
/// against its notes either side. The source's drum mixes in the range are spliced in, with a mix at either end of it where
/// needed, so disco flip and the stems are as each had them.</para>
/// </remarks>
public static class Reducer
{
    /// <summary>A rank no strong position has, for a note off every grid of the beat.</summary>
    internal const int OffGrid = 99;

    /// <summary>
    /// A draft of <paramref name="to"/> of the 5-fret or GHL <paramref name="part"/>, from <paramref name="from"/>: in
    /// place of all of it, or only of <paramref name="range"/> (ticks, both ends in). Where the rules keep HOPOs and taps,
    /// each chord is played as the source's is, and the target's first after the range as it was, read with
    /// <paramref name="options"/> (the defaults when null).
    /// </summary>
    public static ReductionResult Reduce(EditableChart chart, ChartInstrument part, ChartDifficulty from, ChartDifficulty to, FretReductionRules rules, (long From, long To)? range = null, ChartReadOptions? options = null)
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
        var riffs = ShareRiffScores(chords, scores, map, chart.Resolution);

        var spacing = SpacingOf(rules.ThinningDivision, rules.BurstDivision, rules.MaxBurst, chart.Resolution);
        var kept = Thin(chords, scores, spacing, whole.Phrases, Outside(target.Notes, range), riffs);

        // Each chord's fret columns, one bit each (none for opens alone), and whether it has an open.
        var shape = new int[chords.Count];
        var hasOpen = new bool[chords.Count];
        for (int i = 0; i < chords.Count; i++)
        {
            foreach (var n in chords[i].Notes)
            {
                if (n.Lane == ChartNote.OpenLane)
                    hasOpen[i] = true;
                else
                    shape[i] |= 1 << Column(n);
            }
        }

        bool OpenOnly(int i) => shape[i] == 0;

        // Where the rules fret opens, each phrase of the source with opens alone and pressed notes both: there each open is
        // green, and each fret next to an open alone, with the same chord repeated beside it, is lifted a column, so going
        // between them is still a move. Frets further from an open keep their place, so a fill keeps its top; a chord with
        // an open and frets has the open as its green. With green the highest column, nothing can be lifted off it.
        int highest = rules.HighestLane;
        var mixed = new bool[chords.Count];
        var lifted = new bool[chords.Count];
        if (rules.KeepOpenNotes is false && highest >= 1)
        {
            foreach (var (start, end) in phrases)
            {
                var all = Enumerable.Range(start, end - start + 1);
                if (!all.Any(OpenOnly) || !all.Any(i => shape[i] != 0))
                    continue;
                Array.Fill(mixed, true, start, end - start + 1);
                for (int i = start; i <= end; i++)
                    lifted[i] = !hasOpen[i] && ((i > start && OpenOnly(i - 1)) || (i < end && OpenOnly(i + 1)));
                for (int i = start + 1; i <= end; i++)
                    lifted[i] |= lifted[i - 1] && !hasOpen[i] && shape[i] == shape[i - 1];
                for (int i = end - 1; i >= start; i--)
                    lifted[i] |= lifted[i + 1] && !hasOpen[i] && shape[i] == shape[i + 1];
            }
        }

        // A chord's notes as pressed: in a phrase whose opens are fretted, an open with frets is green (on GHL, the first
        // white), pressed with them.
        EditableNote[] Pressed(int i) => mixed[i] && hasOpen[i] && !OpenOnly(i)
            ? [.. chords[i].Notes.Select(static n => n.Lane == ChartNote.OpenLane ? n with { Lane = 0 } : n)]
            : chords[i].Notes;

        // Each kept chord's columns, then each phrase fitted to the range as a whole.
        var columns = new Dictionary<int, List<int>>();
        foreach (int i in kept)
        {
            if (SelectColumns(Pressed(i), rules.MaxChordSize, Column) is { } chosen)
                columns[i] = chosen;
        }

        // The phrases cover the chords in order, so one walk through the kept chords, which are in order too, splits them.
        var placedAt = new Dictionary<int, (int Anchor, int Width)>();
        int next = 0;
        foreach (var (start, end) in phrases)
        {
            var inPhrase = new List<int>();
            for (; next < kept.Count && kept[next] <= end; next++)
                inPhrase.Add(kept[next]);

            // A fret is lifted by the source's opens, so each difficulty has it alike, and by an open the draft keeps beside
            // it, which is green, where it's kept off green too: thinned out, green is free again.
            bool BesideOpen(int k) => mixed[start] && !hasOpen[inPhrase[k]] && ((k > 0 && OpenOnly(inPhrase[k - 1])) || (k + 1 < inPhrase.Count && OpenOnly(inPhrase[k + 1])));
            var frets = new List<int>();
            var floors = new List<int>();
            for (int k = 0; k < inPhrase.Count; k++)
            {
                if (!columns.ContainsKey(inPhrase[k]))
                    continue;
                frets.Add(inPhrase[k]);
                floors.Add(BesideOpen(k) ? 1 : 0);
            }

            int[] anchorsOf = [.. frets.Select(i => columns[i][0])];
            var tops = frets.Select(i => Pressed(i).Where(static n => n.Lane != ChartNote.OpenLane).Max(Column)).ToArray();
            int[] places = [.. frets.Select((i, k) => anchorsOf[k] + (lifted[i] || floors[k] > 0 ? 1 : 0))];
            var fitted = FitPhrase(anchorsOf, [.. frets.Select(i => columns[i][^1] - columns[i][0])], highest, tops, places, floors);
            for (int k = 0; k < frets.Count; k++)
                placedAt[frets[k]] = fitted[k];
        }

        var notes = new List<EditableNote>();
        var flags = new List<FlaggedChord>();
        foreach (int i in kept)
        {
            var (tick, chord) = chords[i];

            // In a phrase whose opens are fretted, opens alone are the chord's longest on green (on GHL, the first white); one
            // with frets is pressed with them.
            List<EditableNote> placed = !mixed[i] ? [.. chord.Where(static n => n.Lane == ChartNote.OpenLane)]
                : OpenOnly(i) ? [chord.MaxBy(static n => n.Length)! with { Lane = 0 }]
                : [];
            if (columns.TryGetValue(i, out var chosen))
                placed.AddRange(Place(Pressed(i), chosen, placedAt[i].Anchor, placedAt[i].Width, highest, ghl, Column));

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

        CutOverlappingSustains(notes, rules.KeepShortSustains ? 0 : chart.Resolution, range);
        if (rules.KeepExtendedSustains is false)
            PlainExtendedSustains(notes, range);
        if (rules.KeepForcedHopoAndTap)
        {
            var read = options ?? ChartReadOptions.Default;
            var fromSource = HopoStates(GameView.Frets(source.Notes, source.FlaggedChords, chart.Resolution, read, []));
            var wanted = kept.Select(i => chords[i].Tick).Where(fromSource.ContainsKey).ToDictionary(static t => t, t => fromSource[t]);

            // The target's first chord after the range follows the draft's last now, so it's played as it was before.
            int first = range is { } after ? target.Notes.FirstAfter(after.To) : target.Notes.Count;
            if (first < target.Notes.Count && HopoStates(GameView.Frets(target.Notes, target.FlaggedChords, chart.Resolution, read, [])).TryGetValue(target.Notes[first].Tick, out var was))
                wanted[target.Notes[first].Tick] = was;

            flags = PlayedAs(wanted, notes, flags, chart.Resolution, read);
        }

        return new ReductionResult(notes, flags, [], inRange.Length - made);
    }

    /// <summary>
    /// <paramref name="flags"/>, with each chord of <paramref name="wanted"/> forced to a HOPO or a strum where the draft's
    /// <paramref name="notes"/> would play it otherwise than it wants: notes left out between two can put them past the
    /// HOPO threshold, or bring them within it. A chord on the same frets as the one before it is a strum, whatever it
    /// wants: a HOPO there, where frets the source had apart were pressed together, is awkward to play. A tap stays a tap.
    /// </summary>
    private static List<FlaggedChord> PlayedAs(Dictionary<long, NoteFlags> wanted, List<EditableNote> notes, List<FlaggedChord> flags, int resolution, ChartReadOptions options)
    {
        var played = HopoStates(GameView.Frets(notes, flags, resolution, options, []));
        var marked = flags.ToDictionary(static f => f.Tick, static f => f.Flags);

        // Each chord's frets, and whether it's on the same ones as the chord before it.
        var chords = notes.GroupBy(static n => n.Tick).Select(static g => (g.Key, Frets: string.Join(",", g.Select(static n => n.Lane).Distinct().Order()))).ToList();
        var repeated = Enumerable.Range(1, Math.Max(0, chords.Count - 1)).Where(i => chords[i].Frets == chords[i - 1].Frets).Select(i => chords[i].Key).ToHashSet();
        foreach (var (tick, wants) in wanted)
        {
            var want = wants;
            if (!played.TryGetValue(tick, out var have) || want == NoteFlags.Tap || have == NoteFlags.Tap)
                continue;
            bool again = repeated.Contains(tick);
            if (again)
                want = NoteFlags.None;
            if (want == have)
                continue;

            // A repeated chord is no natural HOPO, so leaving out its forcing makes it a strum.
            var was = marked.GetValueOrDefault(tick) & ~(ChordFlags.ForceHopo | ChordFlags.ForceStrum);
            marked[tick] = again ? was : was | (want == NoteFlags.Hopo ? ChordFlags.ForceHopo : ChordFlags.ForceStrum);
        }

        return [.. marked.Where(static m => m.Value != ChordFlags.None).OrderBy(static m => m.Key).Select(static m => new FlaggedChord(m.Key, m.Value))];
    }

    // Each chord's tick, and whether it's a tap, a HOPO or a strum (none), as the games give a chord's notes the same: the
    // most of its notes' flags, which rank a tap over a HOPO over neither.
    private static Dictionary<long, NoteFlags> HopoStates(List<ChartNote> read)
    {
        var states = new Dictionary<long, NoteFlags>();
        foreach (var note in read)
        {
            var state = (note.Flags & NoteFlags.Tap) != 0 ? NoteFlags.Tap : note.Flags & NoteFlags.Hopo;
            states[note.Tick] = states.TryGetValue(note.Tick, out var other) && other > state ? other : state;
        }

        return states;
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
        var riffs = ShareRiffScores(hits, scores, map, chart.Resolution);
        var spacing = SpacingOf(rules.ThinningDivision, rules.BurstDivision, rules.MaxBurst, chart.Resolution);
        var outside = Outside(target.Notes, range);
        var keptHits = Thin(hits, scores, spacing, drums.Phrases, outside, riffs).Select(i => hits[i].Tick).ToHashSet();
        notes.RemoveAll(n => !keptHits.Contains(n.Tick));

        if (rules.KickDivision > 0)
        {
            var kicks = ChordsOf([.. notes.Where(static n => n.Lane == 0)]);
            var kickGap = ThinningSpacing.Even(MinGap(rules.KickDivision, chart.Resolution));
            var keptKicks = Thin(kicks, [.. kicks.Select(k => -(double)BeatStrength(k.Tick, map))], kickGap, null, Outside(target.Notes, range, static n => n.Lane == 0), [])
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
            mixes = SpliceMixes(source.Mixes, target.Mixes, r);
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
        var (signature, _) = SignatureAt(tick, map);
        long beat = BeatOf(signature, map);
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
    /// The first position after <paramref name="tick"/> stronger than it (<see cref="BeatStrength"/>), and how strong:
    /// the beat an offbeat leads to, or the measure's first beat a beat leads to. A time signature's start is a measure's
    /// first beat. Null for a measure's first beat, and for a tick off every grid.
    /// </summary>
    internal static (long Tick, int Strength)? NextStronger(long tick, TempoMap map)
    {
        int own = BeatStrength(tick, map);
        return own <= 0 || own == OffGrid ? null : NextAtLeast(tick, own - 1, map);
    }

    // The first position after tick as strong as strength or stronger (BeatStrength), and how strong; a time signature's
    // start is a measure's first beat. Null where there's none.
    private static (long Tick, int Strength)? NextAtLeast(long tick, int strength, TempoMap map)
    {
        var (signature, next) = SignatureAt(tick, map);
        long beat = BeatOf(signature, map);
        long offset = Math.Max(0, tick - signature.Tick);

        // Every position stronger than the tick is on a grid whose every step is as strong, so the nearest is the first
        // step after it of one of the grids: the measure, the beat, each halving, each triplet.
        var grids = new List<long> { beat * Math.Max(1, signature.Numerator), beat };
        for (int level = 1; level <= 6; level++)
        {
            if (beat >> level > 0 && beat % (1L << level) == 0)
                grids.Add(beat >> level);
            if (beat / (3L << (level - 1)) > 0 && beat % (3L << (level - 1)) == 0)
                grids.Add(beat / (3L << (level - 1)));
        }

        (long Tick, int Strength)? best = next is { } change ? (change, 0) : null;
        foreach (long grid in grids)
        {
            long at = signature.Tick + (((offset / grid) + 1) * grid);
            if (best is { } b && at >= b.Tick)
                continue;
            int found = BeatStrength(at, map);
            if (found <= strength)
                best = (at, found);
        }

        return best;
    }

    /// <summary>
    /// The ticks of <paramref name="ticks"/> kept by thinning on position alone: stronger positions first, each where no
    /// tick kept is closer than <paramref name="minGap"/>, in tick order.
    /// </summary>
    internal static IReadOnlyList<long> Thin(IReadOnlyList<long> ticks, long minGap, TempoMap map)
    {
        var distinct = ticks.Distinct().Order().ToList();
        var kept = Thin(distinct, [.. distinct.Select(t => -(double)BeatStrength(t, map))], ThinningSpacing.Even(minGap), [], []);
        return [.. kept.Select(i => distinct[i])];
    }

    /// <summary>
    /// The indexes of <paramref name="ticks"/> (in tick order, one per chord) kept by thinning: one of each of
    /// <paramref name="first"/>'s groups first, the earliest in it the spacing allows, unless one is kept already; then the
    /// rest by <paramref name="scores"/>, best first and the earlier on a tie. Each is kept where, with the ticks kept and
    /// <paramref name="fixedTicks"/>, it keeps <paramref name="spacing"/>. Then each of <paramref name="riffs"/> is made the
    /// same in each measure it's in, as most of them have it: a chord most leave out is left out of the rest, unless a group
    /// of <paramref name="first"/> would be left empty, and one most keep is kept in the rest where the spacing allows. In
    /// tick order.
    /// </summary>
    internal static List<int> Thin(IReadOnlyList<long> ticks, IReadOnlyList<double> scores, ThinningSpacing spacing, IEnumerable<IReadOnlyList<int>> first, IEnumerable<long> fixedTicks, IReadOnlyList<Riff>? riffs = null)
    {
        if (spacing.IsNone)
            return [.. Enumerable.Range(0, ticks.Count)];

        var groups = first.ToList();
        var fixedSet = new SortedSet<long>(fixedTicks);
        var taken = new SortedSet<long>(fixedSet);
        var kept = new HashSet<int>();
        var tried = new HashSet<int>();
        var bursts = spacing.IsEven ? null : SourceBursts(ticks, spacing);

        // A chord of one of the source's bursts can be nearer than the pace to the others in it; any other keeps the pace.
        // Either way, with bursts, every Burst + 1 in a row it's among keep the pace on average, whichever was taken first.
        bool Keeps(int i) => bursts is null
            ? IsClear(taken, ticks[i], spacing.Pace)
            : (bursts[i]
                ? IsClear(taken, ticks[i], spacing.Floor) && IsClear(fixedSet, ticks[i], spacing.Pace)
                : IsClear(taken, ticks[i], spacing.Pace))
              && KeepsPace(taken, ticks[i], spacing);

        bool Try(int i)
        {
            if (!tried.Add(i) || taken.Contains(ticks[i]) || !Keeps(i))
                return false;
            taken.Add(ticks[i]);
            kept.Add(i);
            return true;
        }

        foreach (var group in groups)
        {
            if (group.Any(kept.Contains))
                continue;
            foreach (int i in group)
            {
                if (Try(i))
                    break;
            }
        }

        var order = Enumerable.Range(0, ticks.Count).OrderByDescending(i => scores[i]).ThenBy(i => ticks[i]).ToList();
        foreach (int i in order)
            Try(i);
        if (riffs is { Count: > 0 })
            Conform(riffs);
        return [.. kept.Order()];

        bool Add(int i)
        {
            if (kept.Contains(i) || taken.Contains(ticks[i]) || !Keeps(i))
                return false;
            taken.Add(ticks[i]);
            kept.Add(i);
            return true;
        }

        // Leaving a chord out never brings two closer, so those most measures leave out go first; then those most keep are
        // tried in the rest, best first; then, where any was left out, every other chord again, as one it had kept too
        // close may fit now.
        void Conform(IReadOnlyList<Riff> riffs)
        {
            var groupsOf = groups.SelectMany(g => g.Select(i => (i, g))).ToLookup(static p => p.i, static p => p.g);
            bool Needed(int i) => groupsOf[i].Any(g => g.Count(kept.Contains) == 1);
            int Kept(Riff riff, int k) => riff.Starts.Count(s => kept.Contains(s + k));

            var places = riffs.SelectMany(r => Enumerable.Range(0, r.Length).Select(k => (Riff: r, K: k))).ToList();
            var leftOut = new HashSet<int>();
            bool removed = false;
            foreach (var (riff, k) in places.Where(p => Kept(p.Riff, p.K) * 2 < p.Riff.Starts.Count))
            {
                leftOut.UnionWith(riff.Starts.Select(s => s + k));
                foreach (int s in riff.Starts.Where(s => kept.Contains(s + k) && !Needed(s + k)))
                {
                    kept.Remove(s + k);
                    taken.Remove(ticks[s + k]);
                    removed = true;
                }
            }

            foreach (var (riff, k) in places.OrderByDescending(p => scores[p.Riff.Starts[0] + p.K]).ThenBy(p => ticks[p.Riff.Starts[0] + p.K]))
            {
                if (Kept(riff, k) * 2 < riff.Starts.Count)
                    continue;
                foreach (int s in riff.Starts)
                    Add(s + k);
            }

            if (!removed)
                return;
            foreach (int i in order.Where(i => !leftOut.Contains(i)))
                Add(i);
        }
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
                split = RestsBefore(chords, i, resolution);
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
    /// (<paramref name="widths"/>), so none reaches past <paramref name="highest"/>: where they are when they fit and keep
    /// each step, the phrase moved down as a whole when that does, or else fitted to the range with each step kept going
    /// up, down or the same where it can be. A step goes the way the lowest column goes, or, where that stays, the way the
    /// highest goes: in the source, <paramref name="tops"/> (the anchors when null), and as placed, the chord's width. So
    /// green–red then green–yellow made single notes is green then red, not green twice.
    /// </summary>
    /// <remarks>
    /// Fitted, a chord can be made narrower, its top pressed in, where that keeps a step its full width can't: a chord as
    /// wide as the range has nowhere to go, so green–blue then red–orange on Medium is green–blue then red–blue, not
    /// green–blue twice. A chord stays a chord. Chords repeated one after another are one step of the melody, so a long
    /// run of one counts no more than a short one.
    /// <para>Each chord can be put somewhere other than its source columns before fitting (<paramref name="places"/>, the
    /// anchors when null), with its steps still those of the source, and kept no lower than a floor
    /// (<paramref name="floors"/>, none when null) where the range leaves room, raised to it where moving the phrase down
    /// would leave it below: so frets lifted off green for the opens beside them stay off it. A run of one chord repeated
    /// takes the highest place and floor of its chords, as it's placed as one.</para>
    /// </remarks>
    internal static (int Anchor, int Width)[] FitPhrase(IReadOnlyList<int> anchors, IReadOnlyList<int> widths, int highest, IReadOnlyList<int>? tops = null, IReadOnlyList<int>? places = null, IReadOnlyList<int>? floors = null)
    {
        int n = anchors.Count;
        if (n == 0)
            return [];
        var width = widths.Select(w => Math.Min(w, highest)).ToArray();
        tops ??= anchors;

        // The runs of one chord repeated, each one step of the melody.
        var starts = Enumerable.Range(0, n).Where(i => i == 0 || anchors[i] != anchors[i - 1] || width[i] != width[i - 1] || tops[i] != tops[i - 1]).ToList();
        int m = starts.Count;

        // A run is placed as one, so each of its chords is put where the highest of them is, and kept off its highest floor.
        var place = (places ?? anchors).ToArray();
        var floor = floors?.ToArray() ?? new int[n];
        for (int k = 0; k < m; k++)
        {
            int end = k + 1 < m ? starts[k + 1] : n;
            int p = place[starts[k]..end].Max(), f = floor[starts[k]..end].Max();
            Array.Fill(place, p, starts[k], end - starts[k]);
            Array.Fill(floor, f, starts[k], end - starts[k]);
        }

        // Moved down as a whole where it fits, and up to any floor that leaves it below, which is never past the range.
        int Floor(int i, int w) => Math.Min(floor[i], highest - w);
        int top = Enumerable.Range(0, n).Max(i => place[i] + width[i]);
        int bottom = place.Min();
        int shift = Math.Max(0, top - highest);
        int[]? fitted = top - bottom <= highest ? [.. Enumerable.Range(0, n).Select(i => Math.Max(place[i] - shift, Floor(i, width[i])))] : null;
        int A(int k) => anchors[starts[k]];
        int Wanted(int k) => A(k) != A(k - 1) ? Math.Sign(A(k) - A(k - 1)) : Math.Sign(tops[starts[k]] - tops[starts[k - 1]]);
        static int Made((int V, int W) now, (int V, int W) before) => now.V != before.V ? Math.Sign(now.V - before.V) : Math.Sign(now.W - before.W);

        if (fitted is not null && Enumerable.Range(1, m - 1).All(k => Made((fitted[starts[k]], width[starts[k]]), (fitted[starts[k - 1]], width[starts[k - 1]])) == Wanted(k)))
            return [.. Enumerable.Range(0, n).Select(i => (fitted[i], width[i]))];

        // Otherwise the fewest steps turned, then the nearest to the phrase and the least narrowed: as it fits, where it
        // does, with a step turned costing more than any nearness can make up; or scaled into the range, which it doesn't
        // fit only when wider than it, so never by dividing by nothing.
        double scale = fitted is null ? (double)highest / (top - bottom) : 1;
        double nearness = fitted is null ? 1 : ThinningWeights.FlatStep / ((m * (highest + 1.0) * (1 + ThinningWeights.Narrowed)) + 1);
        var states = new (int V, int W)[m][];
        var cost = new double[m][];
        var back = new int[m][];
        for (int k = 0; k < m; k++)
        {
            int w = width[starts[k]];
            states[k] = [.. Enumerable.Range(w > 0 ? 1 : 0, w > 0 ? w : 1).SelectMany(x => Enumerable.Range(0, highest - x + 1).Where(v => v >= Floor(starts[k], x)).Select(v => (v, x)))];
            cost[k] = new double[states[k].Length];
            back[k] = new int[states[k].Length];
            double ideal = fitted?[starts[k]] ?? (place[starts[k]] - bottom) * scale;
            for (int s = 0; s < states[k].Length; s++)
            {
                var (v, x) = states[k][s];
                double here = (Math.Abs(v - ideal) + ((w - x) * ThinningWeights.Narrowed)) * nearness;
                if (k == 0)
                {
                    cost[k][s] = here;
                    continue;
                }

                int wanted = Wanted(k);
                cost[k][s] = double.PositiveInfinity;
                for (int p = 0; p < states[k - 1].Length; p++)
                {
                    int made = Made(states[k][s], states[k - 1][p]);
                    double step = made == wanted ? 0 : made == -wanted && wanted != 0 ? ThinningWeights.ReversedStep : ThinningWeights.FlatStep;
                    double total = cost[k - 1][p] + step + here;
                    if (total < cost[k][s] - 1e-9)
                    {
                        cost[k][s] = total;
                        back[k][s] = p;
                    }
                }
            }
        }

        var placed = new (int Anchor, int Width)[n];
        int best = 0;
        for (int s = 1; s < cost[m - 1].Length; s++)
        {
            if (cost[m - 1][s] < cost[m - 1][best] - 1e-9)
                best = s;
        }

        for (int k = m - 1; k >= 0; k--)
        {
            int end = k + 1 < m ? starts[k + 1] : n;
            for (int i = starts[k]; i < end; i++)
                placed[i] = states[k][best];
            best = back[k][best];
        }

        return placed;
    }

    // The time signature in effect at tick, and where the next one starts, if any.
    private static (TimeSignature Signature, long? Next) SignatureAt(long tick, TempoMap map)
    {
        var signatures = map.TimeSignatures;
        var signature = signatures[0];
        foreach (var s in signatures)
        {
            if (s.Tick > tick)
                return (signature, s.Tick);
            signature = s;
        }

        return (signature, null);
    }

    // A beat of the signature, in ticks.
    private static long BeatOf(TimeSignature signature, TempoMap map) => Math.Max(1, map.Resolution * 4L / signature.Denominator);

    private static EditableTrack SourceOf(EditableChart chart, ChartInstrument part, ChartDifficulty from, ChartDifficulty to)
    {
        if (to >= from)
            throw new ArgumentException("A difficulty is reduced to a lower one.", nameof(to));
        return (chart.PartOf(part) ?? throw new InvalidOperationException($"The chart has no {part} part."))[from];
    }

    private static EditableNote[] Inside(TickList<EditableNote> notes, (long From, long To)? range) =>
        range is { } r ? notes.Between(r.From, r.To).ToArray() : [.. notes];

    // The target's chords outside the range, which the gap holds against; none when the draft replaces the whole track.
    private static long[] Outside(TickList<EditableNote> notes, (long From, long To)? range, Func<EditableNote, bool>? which = null) =>
        range is { } r
            ? [.. notes.AsSpan().ToArray().Where(n => (n.Tick < r.From || n.Tick > r.To) && (which is null || which(n))).Select(static n => n.Tick).Distinct()]
            : [];

    internal static List<(long Tick, EditableNote[] Notes)> ChordsOf(EditableNote[] notes) =>
        [.. notes.GroupBy(static n => n.Tick).OrderBy(static g => g.Key).Select(static g => (g.Key, g.ToArray()))];

    internal static long[] SectionTicks(EditableChart chart) => [.. SectionEvents.All(chart).Select(static e => e.Tick)];

    // Whether chord i starts a beat (resolution) or more after the one before it ends.
    private static bool RestsBefore(IReadOnlyList<(long Tick, EditableNote[] Notes)> chords, int i, int resolution) =>
        chords[i].Tick - (chords[i - 1].Tick + chords[i - 1].Notes.Max(static n => Math.Max(1, n.Length))) >= resolution;

    // A range's mixes: the target's outside it and the source's inside, with a mix at the range's start where what the
    // source has in effect there isn't what the target has, and one after its end back to the target's, so disco flip and
    // the stems are as each had them on either side.
    private static List<DrumMix> SpliceMixes(TickList<DrumMix> source, TickList<DrumMix> target, (long From, long To) range)
    {
        var mixes = target.AsSpan().ToArray().Where(m => m.Tick < range.From).ToList();
        var inside = source.Between(range.From, range.To).ToArray();
        var wanted = InEffect(source, range.From);
        if ((inside.Length == 0 || inside[0].Tick != range.From) && !SameMix(wanted, mixes.Count > 0 ? mixes[^1] : null))
            mixes.Add(new DrumMix(range.From, wanted?.Configuration ?? 0, wanted?.Setting ?? string.Empty));
        mixes.AddRange(inside);

        long after = range.To + 1;
        var restored = InEffect(target, after);
        if (target.At(after).IsEmpty && !SameMix(restored, mixes.Count > 0 ? mixes[^1] : null))
            mixes.Add(new DrumMix(after, restored?.Configuration ?? 0, restored?.Setting ?? string.Empty));
        mixes.AddRange(target.AsSpan().ToArray().Where(m => m.Tick > range.To));
        return mixes;

        // The mix in effect at tick: the last at or before it.
        static DrumMix? InEffect(TickList<DrumMix> track, long tick)
        {
            int index = track.FirstAfter(tick) - 1;
            return index >= 0 ? track[index] : null;
        }

        // Whether two mixes, wherever they are, read the same; no mix reads as the games' default, drums0 with no setting.
        static bool SameMix(DrumMix? a, DrumMix? b) =>
            (a?.Configuration ?? 0) == (b?.Configuration ?? 0) && (a?.Setting ?? string.Empty) == (b?.Setting ?? string.Empty);
    }

    // A chord's lowest column, or null for one of opens alone.
    private static int? Anchor(EditableNote[] chord, Func<EditableNote, int> column)
    {
        var frets = chord.Where(static n => n.Lane != ChartNote.OpenLane).ToList();
        return frets.Count == 0 ? null : frets.Min(column);
    }

    // How readily each chord is kept (ThinningWeights); anchors give the melody's turns, and are null for drums.
    internal static double[] Scores(List<(long Tick, EditableNote[] Notes)> chords, List<(int Start, int End)> phrases, int?[]? anchors, TempoMap map, int resolution, Func<(long Tick, EditableNote[] Notes), int, bool> isChord)
    {
        var scores = new double[chords.Count];
        for (int i = 0; i < chords.Count; i++)
        {
            // A chord whose next stronger position is empty, the next chord after it, anticipates it; but only where the next
            // chord comes before the position after that as strong, so the part goes on through it rather than rests.
            scores[i] = i + 1 < chords.Count && NextStronger(chords[i].Tick, map) is { } stronger && chords[i + 1].Tick > stronger.Tick
                && chords[i + 1].Tick < (NextAtLeast(stronger.Tick, stronger.Strength, map)?.Tick ?? long.MaxValue)
                ? -(stronger.Strength + ThinningWeights.Anticipation)
                : -BeatStrength(chords[i].Tick, map);
            if (chords[i].Notes.Any(n => n.Length >= resolution))
                scores[i] += ThinningWeights.Sustain;
            if (isChord(chords[i], i))
                scores[i] += ThinningWeights.Chord;
        }

        for (int p = 0; p < phrases.Count; p++)
        {
            var (start, end) = phrases[p];
            if (p > 0 && RestsBefore(chords, start, resolution))
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

    /// <summary>
    /// Gives each measure of <paramref name="chords"/> charted the same as another (the same chords at the same places, each
    /// with the same notes and whether it holds a sustain of a beat or longer) the same scores: each chord's mean over every
    /// such measure. So thinning, best first and the earlier on a tie, makes the same choices in each. The riffs, for
    /// thinning to make the same where what's around them differs.
    /// </summary>
    internal static List<Riff> ShareRiffScores(List<(long Tick, EditableNote[] Notes)> chords, double[] scores, TempoMap map, int resolution)
    {
        var riffs = new Dictionary<string, (int Length, List<int> Starts)>();
        var key = new StringBuilder();
        int start = 0;
        while (start < chords.Count)
        {
            var (signature, next) = SignatureAt(chords[start].Tick, map);
            long measure = BeatOf(signature, map) * Math.Max(1, signature.Numerator);
            long from = signature.Tick + (Math.Max(0, chords[start].Tick - signature.Tick) / measure * measure);
            long to = Math.Min(from + measure, next ?? long.MaxValue);
            int end = start;
            while (end < chords.Count && chords[end].Tick < to)
                end++;

            key.Clear().Append(to - from);
            for (int i = start; i < end; i++)
            {
                key.Append(';').Append(chords[i].Tick - from).Append(chords[i].Notes.Any(n => n.Length >= resolution) ? 'S' : ':');
                foreach (var note in chords[i].Notes.OrderBy(static n => n.Lane))
                    key.Append(note.Lane).Append(',').Append((int)note.Dynamics).Append(note.DoubleKick ? '+' : ',');
            }

            if (end - start > 1)
            {
                string riff = key.ToString();
                if (!riffs.TryGetValue(riff, out var found))
                    riffs[riff] = found = (end - start, []);
                found.Starts.Add(start);
            }

            start = end;
        }

        var repeated = new List<Riff>();
        foreach (var (length, starts) in riffs.Values.Where(static r => r.Starts.Count > 1))
        {
            for (int k = 0; k < length; k++)
            {
                double mean = starts.Average(s => scores[s + k]);
                foreach (int s in starts)
                    scores[s + k] = mean;
            }

            repeated.Add(new Riff(length, starts));
        }

        return repeated;
    }

    // The chords kept: the best of each star power phrase and solo the spacing allows first, then the rest by score, then
    // each riff made the same in each measure it's in.
    private static List<int> Thin(List<(long Tick, EditableNote[] Notes)> chords, double[] scores, ThinningSpacing spacing, TickList<Phrase>? phrases, IEnumerable<long> fixedTicks, IReadOnlyList<Riff> riffs)
    {
        var ticks = chords.Select(static c => c.Tick).ToArray();
        var first = new List<IReadOnlyList<int>>();
        if (phrases is not null)
        {
            foreach (var phrase in phrases.AsSpan())
            {
                if (phrase.Kind is not (PhraseKind.StarPower or PhraseKind.Solo))
                    continue;
                int lo = Array.BinarySearch(ticks, phrase.Tick);
                lo = lo < 0 ? ~lo : lo;
                int hi = lo;
                while (hi < ticks.Length && phrase.Covers(ticks[hi]))
                    hi++;
                if (hi > lo)
                    first.Add([.. Enumerable.Range(lo, hi - lo).OrderByDescending(i => scores[i]).ThenBy(i => ticks[i])]);
            }
        }

        return Thin(ticks, scores, spacing, first, fixedTicks, riffs);
    }

    // The spacing of the rules: the pace from the thinning grid, and the floor from the burst's, never above the pace.
    private static ThinningSpacing SpacingOf(int division, int? burstDivision, int? maxBurst, int resolution)
    {
        long pace = MinGap(division, resolution);
        long floor = burstDivision is { } d ? Math.Min(pace, MinGap(d, resolution)) : pace;
        return new ThinningSpacing(pace, floor, Math.Max(1, maxBurst ?? 1));
    }

    // Which of ticks (the source's chords, in order) are in one of its bursts: a run of two or more, each nearer the next
    // than the pace, with the pace or more either side, that fits in Burst of the pace's gaps. A longer run is a steady
    // stream, which thins evenly: as bursts it would be cut into groups the source never had.
    private static bool[] SourceBursts(IReadOnlyList<long> ticks, ThinningSpacing spacing)
    {
        var bursts = new bool[ticks.Count];
        int start = 0;
        for (int i = 1; i <= ticks.Count; i++)
        {
            if (i < ticks.Count && ticks[i] - ticks[i - 1] < spacing.Pace)
                continue;
            if (i - start > 1 && ticks[i - 1] - ticks[start] <= spacing.Pace * spacing.Burst)
                Array.Fill(bursts, true, start, i - start);
            start = i;
        }

        return bursts;
    }

    // Whether every Burst + 1 ticks in a row with tick among them, of those taken and it, span Burst of the pace's gaps.
    // Only ticks within that span of it can make a run too short, so only those are looked at.
    private static bool KeepsPace(SortedSet<long> taken, long tick, ThinningSpacing spacing)
    {
        int burst = spacing.Burst;
        long span = spacing.Pace * burst;
        var near = taken.GetViewBetween(tick - span, tick + span);
        var row = new List<long>(near.Where(t => t < tick).TakeLast(burst));
        int here = row.Count;
        row.Add(tick);
        row.AddRange(near.Where(t => t > tick).Take(burst));
        for (int start = Math.Max(0, here - burst); start <= here && start + burst < row.Count; start++)
        {
            if (row[start + burst] - row[start] < span)
                return false;
        }

        return true;
    }

    // Whether no tick taken is closer to tick than gap.
    private static bool IsClear(SortedSet<long> taken, long tick, long gap) =>
        gap <= 1 || taken.GetViewBetween(tick - gap + 1, tick + gap - 1).Count == 0;

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
    // as a whole, and those that would go below green are pressed together, the longer sustain kept. Made narrower than
    // that (width), its notes are spread over the width instead, the lowest at anchor and the highest at its top.
    private static IEnumerable<EditableNote> Place(EditableNote[] chord, List<int> columns, int anchor, int width, int highest, bool ghl, Func<EditableNote, int> column)
    {
        int span = columns[^1] - columns[0];
        bool narrowed = width < Math.Min(span, highest);
        int offset = anchor - columns[0];
        int shift = Math.Max(0, columns[^1] + offset - highest);
        var placed = new Dictionary<int, EditableNote>();
        foreach (var note in chord.Where(n => n.Lane != ChartNote.OpenLane && columns.Contains(column(n))).OrderBy(static n => n.Lane))
        {
            int moved = narrowed
                ? anchor + (int)Math.Round((column(note) - columns[0]) * (double)width / span, MidpointRounding.AwayFromZero)
                : Math.Max(0, column(note) + offset - shift);
            int lane = ghl ? (note.Lane / 3 * 3) + moved : moved;
            if (!placed.TryGetValue(lane, out var there) || note.Length > there.Length)
                placed[lane] = note with { Lane = lane };
        }

        return placed.Values.OrderBy(static n => n.Lane);
    }

    // A sustain held into the next note on its lane ends there, or, one the draft makes (in range, when there's one), is a
    // plain note where that leaves it shorter than shortest (0 for none): a burst can bring a sustain's next note nearer
    // than the rules keep sustains. The target's outside the range stay sustains, only shorter.
    private static void CutOverlappingSustains(List<EditableNote> notes, long shortest, (long From, long To)? range)
    {
        notes.Sort(EditableNote.Order);
        var next = new Dictionary<int, long>();
        for (int i = notes.Count - 1; i >= 0; i--)
        {
            var note = notes[i];
            if (next.TryGetValue(note.Lane, out long after) && note.Tick + note.Length > after)
            {
                long length = Math.Max(EditableNote.PlainLength, after - note.Tick);
                bool drafted = range is not { } r || (note.Tick >= r.From && note.Tick <= r.To);
                notes[i] = note with { Length = drafted && length < shortest ? EditableNote.PlainLength : EditableNote.SustainOrPlain(length) };
            }

            next[note.Lane] = note.Tick;
        }
    }

    // Each sustain the draft makes (in range, when there's one) held past the start of the next chord, so held while other
    // notes are played, as a plain note. The notes are in order, as CutOverlappingSustains leaves them.
    private static void PlainExtendedSustains(List<EditableNote> notes, (long From, long To)? range)
    {
        var ticks = notes.Select(static n => n.Tick).Distinct().ToArray();
        for (int i = 0; i < notes.Count; i++)
        {
            var note = notes[i];
            if (note.Length <= EditableNote.PlainLength || (range is { } r && (note.Tick < r.From || note.Tick > r.To)))
                continue;
            int next = Array.BinarySearch(ticks, note.Tick) + 1;
            if (next < ticks.Length && ticks[next] < note.Tick + note.Length)
                notes[i] = note with { Length = EditableNote.PlainLength };
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
