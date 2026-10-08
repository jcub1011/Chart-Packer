using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting.Editing;

/// <summary>
/// What a row of the phrase rail holds, and what the Phrase tool draws (charter-spec C8): a kind of phrase, or a drums
/// difficulty's disco flip sections (C6 item 7), which aren't phrases but are drawn and edited as they are. A
/// <see cref="PhraseKind"/> is one as it is.
/// </summary>
public readonly record struct RailKind
{
    private readonly PhraseKind _phrase;
    private readonly bool _discoFlip;

    public RailKind(PhraseKind phrase) => _phrase = phrase;

    private RailKind(bool discoFlip) => _discoFlip = discoFlip;

    /// <summary>The disco flip sections of the lane's difficulty.</summary>
    public static RailKind DiscoFlip { get; } = new(discoFlip: true);

    /// <summary>The kind of phrase; null for <see cref="DiscoFlip"/>.</summary>
    public PhraseKind? Phrase => _discoFlip ? null : _phrase;

    public bool IsDiscoFlip => _discoFlip;

    public static implicit operator RailKind(PhraseKind phrase) => new(phrase);
}

/// <summary>
/// The phrase rail beside a lane (charter-spec C8): a row for each kind of phrase the part can have, in a fixed order, in
/// which every phrase of the part is drawn as a bar, so overlapping ones don't hide each other; on drums, then a row of
/// the lane's difficulty's disco flip sections (C6 item 7), drawn and edited as phrases are. The Phrase tool offers the
/// same kinds.
/// </summary>
public static class PhraseRail
{
    private static readonly PhraseKind[] FretKinds = [PhraseKind.StarPower, PhraseKind.Solo, PhraseKind.BigRockEnding];
    private static readonly PhraseKind[] DrumKinds = [PhraseKind.StarPower, PhraseKind.Solo, PhraseKind.DrumFill, PhraseKind.Roll, PhraseKind.Trill];
    private static readonly RailKind[] FretRows = [.. FretKinds.Select(static k => (RailKind)k)];
    private static readonly RailKind[] DrumPhraseRows = [.. DrumKinds.Select(static k => (RailKind)k)];
    private static readonly RailKind[] DrumRows = [.. DrumPhraseRows, RailKind.DiscoFlip];

    /// <summary>
    /// The kinds of phrase a part of <paramref name="kind"/> can have, in the rail's order. Drums have no big rock ending
    /// of their own: theirs is a fill after <c>[coda]</c> (C8 item 4), in the fills' row.
    /// </summary>
    public static IReadOnlyList<PhraseKind> KindsOf(ChartInstrumentKind kind) => kind == ChartInstrumentKind.Drums ? DrumKinds : FretKinds;

    /// <summary>
    /// The rows of the rail beside a lane of a part of <paramref name="kind"/>, in order: <see cref="KindsOf"/>' kinds, then
    /// on drums the disco flip row, whatever the variant, so a loaded file's sections show on 4-lane and 5-lane drums too.
    /// </summary>
    public static IReadOnlyList<RailKind> RowsOf(ChartInstrumentKind kind) => kind == ChartInstrumentKind.Drums ? DrumRows : FretRows;

    /// <summary>
    /// The kinds the Phrase tool offers for a part of <paramref name="kind"/>: the rail's rows, but disco flip only on Pro
    /// drums (<paramref name="proDrums"/>), the only drums the games flip.
    /// </summary>
    public static IReadOnlyList<RailKind> ToolKindsOf(ChartInstrumentKind kind, bool proDrums) =>
        kind == ChartInstrumentKind.Drums && !proDrums ? DrumPhraseRows : RowsOf(kind);

    /// <summary>The kind whose row <paramref name="phrase"/> is drawn in, in a part of <paramref name="kind"/>.</summary>
    public static PhraseKind RowOf(ChartInstrumentKind kind, PhraseKind phrase) =>
        kind == ChartInstrumentKind.Drums && phrase == PhraseKind.BigRockEnding ? PhraseKind.DrumFill : phrase;

    /// <summary>
    /// The phrase of <paramref name="part"/> whose bar is under <paramref name="point"/> in the rail, and which part of it,
    /// or null for none. A bar is hit up to <see cref="ChartPoint.Tolerance"/> ticks beyond its ends; when bars of a row
    /// overlap, one the pointer is inside comes first, then the shortest, so a phrase inside another can be picked. Its
    /// ends are grabbed within the tolerance of them, but never more than a quarter of the bar, which leaves a short bar
    /// a body to grab.
    /// </summary>
    public static PhraseHit? HitTest(EditablePart part, ChartPoint point)
    {
        ArgumentNullException.ThrowIfNull(part);
        if (point.Rail?.Phrase is not { } row)
            return null;

        Phrase? best = null;
        double bestDistance = double.MaxValue;
        foreach (var phrase in part.Phrases)
        {
            if (RowOf(part.Kind, phrase.Kind) != row)
                continue;
            long end = phrase.Tick + phrase.Length;
            if (point.Tick < phrase.Tick - point.Tolerance || point.Tick > end + point.Tolerance)
                continue;
            double distance = point.Tick < phrase.Tick ? phrase.Tick - point.Tick : Math.Max(0, point.Tick - end);
            if (distance < bestDistance || (distance == bestDistance && phrase.Length < best!.Value.Length))
                (best, bestDistance) = (phrase, distance);
        }

        return best is { } hit ? new PhraseHit(hit, GripOf(point, hit.Tick, hit.Tick + hit.Length)) : null;
    }

    /// <summary>
    /// The disco flip section of <paramref name="track"/> whose bar is under <paramref name="point"/> in the rail's disco
    /// flip row, and which part of it, or null for none, as <see cref="HitTest"/> finds a phrase's. Sections never overlap.
    /// One with no end, as only a loaded file can have, reaches on past every tick: it has a start and a body to grab.
    /// </summary>
    public static DiscoFlipHit? HitTestDiscoFlip(EditableTrack track, ChartPoint point)
    {
        ArgumentNullException.ThrowIfNull(track);
        return track.Kind == ChartInstrumentKind.Drums ? HitTestDiscoFlip(track.Difficulty, DiscoFlips.RangesOf(track), point) : null;
    }

    /// <summary>
    /// The disco flip section of <paramref name="difficulty"/> whose bar is under <paramref name="point"/>, of its sections
    /// <paramref name="ranges"/> as <see cref="DiscoFlips.RangesOf"/> gives them, such as the session keeps
    /// (<see cref="EditorSession.DiscoFlipRangesOf"/>), so a pointer move doesn't read them from the mixes again.
    /// </summary>
    public static DiscoFlipHit? HitTestDiscoFlip(ChartDifficulty difficulty, IReadOnlyList<DiscoFlipRange> ranges, ChartPoint point)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        if (point.Rail is not { IsDiscoFlip: true })
            return null;

        DiscoFlipRange? best = null;
        double bestDistance = double.MaxValue;
        foreach (var range in ranges)
        {
            long end = range.EndTick ?? long.MaxValue;
            if (point.Tick < range.Tick - point.Tolerance || point.Tick > end + point.Tolerance)
                continue;
            double distance = point.Tick < range.Tick ? range.Tick - point.Tick : Math.Max(0, point.Tick - end);
            if (distance < bestDistance)
                (best, bestDistance) = (range, distance);
        }

        if (best is not { } hit)
            return null;
        var section = new DiscoFlipSection(difficulty, hit);
        return new DiscoFlipHit(section, GripOf(point, hit.Tick, hit.EndTick ?? long.MaxValue));
    }

    /// <summary>
    /// How many notes <paramref name="solo"/> covers on <paramref name="track"/>, for the count drawn at its start (C8 item
    /// 2): a chord counts once, as the games count a solo's notes.
    /// </summary>
    public static int NoteCount(EditableTrack track, Phrase solo)
    {
        ArgumentNullException.ThrowIfNull(track);
        int count = 0;
        long last = -1;
        foreach (var note in track.Notes.Between(solo.Tick, solo.CoverEnd - 1))
        {
            if (note.Tick == last)
                continue;
            last = note.Tick;
            count++;
        }

        return count;
    }

    // Which part of a bar from start to end the point grabs: an end within the tolerance of it, but never more than a
    // quarter of the bar, which leaves a short bar a body to grab; otherwise the body.
    private static PhraseGrip GripOf(ChartPoint point, long start, long end)
    {
        double grip = Math.Min(point.Tolerance, (end - (double)start) / 4.0);
        double fromStart = Math.Abs(point.Tick - start), fromEnd = Math.Abs(point.Tick - end);
        return fromEnd <= grip && fromEnd < fromStart ? PhraseGrip.End
            : fromStart <= grip ? PhraseGrip.Start
            : PhraseGrip.Body;
    }
}

/// <summary>Which part of a phrase's bar in the rail is grabbed: its body, to move it, or an end, to resize it.</summary>
public enum PhraseGrip
{
    Body,
    Start,
    End,
}

/// <summary>A phrase's bar in the rail under the pointer (<see cref="PhraseRail.HitTest"/>).</summary>
public readonly record struct PhraseHit(Phrase Phrase, PhraseGrip Grip);

/// <summary>
/// A disco flip section of one drums difficulty (charter-spec C6 item 7), as the editor selects and edits it: the
/// difficulty and the section, exactly as <see cref="DiscoFlips.RangesOf"/> gives it, so it's kept by value and dropped
/// once the difficulty no longer has it.
/// </summary>
public readonly record struct DiscoFlipSection(ChartDifficulty Difficulty, DiscoFlipRange Range);

/// <summary>A disco flip section's bar in the rail under the pointer (<see cref="PhraseRail.HitTestDiscoFlip"/>).</summary>
public readonly record struct DiscoFlipHit(DiscoFlipSection Section, PhraseGrip Grip);
