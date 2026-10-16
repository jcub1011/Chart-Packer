using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting;

/// <summary>What a fill <see cref="FillGenerator"/> proposes must end on (charter-spec C6 item 5).</summary>
public enum FillEnd
{
    /// <summary>A crash: a green cymbal on Pro drums, orange on 5-lane, green on 4-lane.</summary>
    Crash,

    /// <summary>Any note but a 2× kick, which only Expert+ plays.</summary>
    AnyNote,
}

/// <summary>What the author picks for a run of Generate fills (charter-spec C6 item 5).</summary>
/// <param name="SpacingMeasures">How many measures apart fills start, at the least, counting the fills already there.</param>
/// <param name="LengthMeasures">How many measures a fill lasts, up to the note it ends on.</param>
/// <param name="EndOn">What a fill must end on.</param>
public sealed record FillOptions(int SpacingMeasures = 4, int LengthMeasures = 1, FillEnd EndOn = FillEnd.Crash)
{
    /// <summary>The spec's defaults: 4 measures apart, 1 measure long, ending on a crash.</summary>
    public static FillOptions Default { get; } = new();
}

/// <summary>A fill <see cref="FillGenerator"/> proposes, and the tick of the note star power is activated on.</summary>
/// <param name="Fill">The fill, ending on <paramref name="ActivationTick"/> as CHR043's fix ends one: its last tick is the note's.</param>
/// <param name="ActivationTick">The measure's first beat, where every difficulty with notes has one.</param>
public readonly record struct FillProposal(Phrase Fill, long ActivationTick);

/// <summary>Why <see cref="FillGenerator.Propose"/> proposed no fills.</summary>
public enum NoFillsReason
{
    /// <summary>It proposed some.</summary>
    None,

    /// <summary>The chart has no drums, or they have no notes.</summary>
    NoDrumNotes,

    /// <summary>The drums have fewer than two star power phrases, so a player never has enough to activate.</summary>
    TooFewStarPower,

    /// <summary>No spot fits: none has the note a fill must end on, clear of star power, other fills and <c>[coda]</c>.</summary>
    NoSpotFits,
}

/// <summary>What <see cref="FillGenerator.Propose"/> proposed, or why it proposed nothing.</summary>
public sealed record FillGeneration(IReadOnlyList<FillProposal> Proposals, NoFillsReason Reason);

/// <summary>
/// Generate fills (charter-spec C6 item 5): proposes drum fills over a span of the drums, or the whole part, which the
/// author then keeps or drops (<see cref="Editing.FillProposals"/>). It changes nothing.
/// </summary>
/// <remarks>
/// <para>A spot is a measure line: a fill starting there lasts <see cref="FillOptions.LengthMeasures"/> measures, by the
/// time signatures in force, and ends on the first beat of the measure after, its <b>activation note</b>. That note must be
/// what <see cref="FillOptions.EndOn"/> asks for on the hardest difficulty with notes, and every difficulty with notes must
/// have a note there, or the spot is left out. The fill ends the tick after the note, as CHR043's fix ends one, so the note
/// is the last it covers (§6) and CHR043 never fires on it.</para>
/// <para>Spots are taken earliest first, from where the second star power phrase ends (overlapping ones counted as one,
/// as saving merges them). A fill never overlaps star power (CHR042), another fill or a big rock ending, never reaches
/// <c>[coda]</c>, and starts at least <see cref="FillOptions.SpacingMeasures"/> measures from every other fill's start,
/// those already there and those proposed.</para>
/// </remarks>
public static class FillGenerator
{
    // The drums pad a crash is on: green on 4-lane and Pro (a cymbal on Pro), orange on 5-lane.
    private const int CrashPad = 4;

    /// <summary>Proposes fills for the drums of <paramref name="chart"/>.</summary>
    /// <param name="variant">How the drums are charted, which says what a crash is.</param>
    /// <param name="span">The ticks a fill must lie within, from its start to its activation note; the whole part when null.</param>
    public static FillGeneration Propose(EditableChart chart, DrumsVariant variant, FillOptions options, (long From, long To)? span = null)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.SpacingMeasures, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.LengthMeasures, 1, nameof(options));

        if (chart.PartOf(ChartInstrument.Drums) is not { HasNotes: true } drums)
            return new FillGeneration([], NoFillsReason.NoDrumNotes);
        if (SecondStarPowerEnd(drums) is not { } earliest)
            return new FillGeneration([], NoFillsReason.TooFewStarPower);

        var charted = drums.Tracks.Where(static t => t.Notes.Count > 0).ToList();
        var hardest = charted[^1];
        long from = Math.Max(earliest, span?.From ?? 0);
        long to = Math.Min(hardest.Notes[hardest.Notes.Count - 1].Tick, span?.To ?? long.MaxValue);
        if (DrumFills.FirstCoda(chart) is { } coda)
            to = Math.Min(to, coda - 1);

        var starPower = drums.Phrases.Where(static p => p.Kind == PhraseKind.StarPower).ToList();
        var fills = drums.Phrases.Where(static p => p.Kind is PhraseKind.DrumFill or PhraseKind.BigRockEnding).ToList();
        long horizon = Math.Max(to, fills.Count > 0 ? fills.Max(static f => f.Tick) : 0);
        var lines = chart.TempoMap.ToTempoMap().BeatLines(0, horizon).Where(static b => b.IsMeasure).Select(static b => b.Tick).ToList();

        var proposals = new List<FillProposal>();
        int spacing = options.SpacingMeasures, length = options.LengthMeasures;
        for (int m = FirstAtOrAfter(lines, from); m + length < lines.Count; m++)
        {
            long start = lines[m], activation = lines[m + length];
            if (activation > to)
                break;
            var fill = new Phrase(PhraseKind.DrumFill, start, activation + 1 - start);
            if (starPower.Any(fill.Overlaps) || fills.Any(f => fill.Overlaps(f) || (f.Kind == PhraseKind.DrumFill && TooClose(lines, m, spacing, f.Tick))))
                continue;
            if (!Ends(drums, variant, options.EndOn, hardest, charted, activation))
                continue;
            proposals.Add(new FillProposal(fill, activation));
            fills.Add(fill);
        }

        return new FillGeneration(proposals, proposals.Count > 0 ? NoFillsReason.None : NoFillsReason.NoSpotFits);
    }

    /// <summary>
    /// The tick after the second of <paramref name="drums"/>' star power phrases ends, the earliest a player can have
    /// enough to activate; null when there are fewer than two. Phrases that overlap count as one, as saving merges them.
    /// </summary>
    public static long? SecondStarPowerEnd(EditablePart drums)
    {
        ArgumentNullException.ThrowIfNull(drums);
        int phrases = 0;
        long end = 0;
        foreach (var phrase in drums.Phrases)
        {
            if (phrase.Kind != PhraseKind.StarPower)
                continue;
            if (phrases > 0 && phrase.Tick < end)
            {
                end = Math.Max(end, phrase.CoverEnd);
                continue;
            }

            if (phrases == 2)
                break;
            phrases++;
            end = phrase.CoverEnd;
        }

        return phrases == 2 ? end : null;
    }

    /// <summary>
    /// Whether <paramref name="note"/> of <paramref name="drums"/> is a crash: a green cymbal on Pro drums, orange on 5-lane
    /// and green on 4-lane (charter-spec C6 item 5).
    /// </summary>
    public static bool IsCrash(EditablePart drums, DrumsVariant variant, EditableNote note)
    {
        ArgumentNullException.ThrowIfNull(drums);
        return !note.DoubleKick && note.Lane == CrashPad && (variant != DrumsVariant.Pro || !DrumCymbals.IsTom(drums, CrashPad, note.Tick));
    }

    // Whether a fill can end on the note at the tick: one the options ask for on the hardest difficulty, and a note every
    // difficulty with notes can play.
    private static bool Ends(EditablePart drums, DrumsVariant variant, FillEnd endOn, EditableTrack hardest, List<EditableTrack> charted, long tick)
    {
        bool qualifies = false;
        foreach (var note in hardest.Notes.At(tick))
            qualifies |= endOn == FillEnd.AnyNote ? !note.DoubleKick : IsCrash(drums, variant, note);
        return qualifies && charted.All(t => HasPlainNote(t, tick));
    }

    private static bool HasPlainNote(EditableTrack track, long tick)
    {
        foreach (var note in track.Notes.At(tick))
        {
            if (!note.DoubleKick)
                return true;
        }

        return false;
    }

    // Whether a fill starting on the measure line at index m starts fewer than spacing measures from a fill starting at
    // tick: it must start at or before the line spacing measures before m, or at or after the one spacing measures after.
    // The lines run past every fill's start, so a line beyond them is after tick.
    private static bool TooClose(List<long> lines, int m, int spacing, long tick)
    {
        bool before = m - spacing >= 0 && tick <= lines[m - spacing];
        bool after = m + spacing < lines.Count && tick >= lines[m + spacing];
        return !before && !after;
    }

    private static int FirstAtOrAfter(List<long> lines, long tick)
    {
        int index = lines.BinarySearch(tick);
        return index >= 0 ? index : ~index;
    }
}
