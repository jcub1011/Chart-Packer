using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting.Editing;

/// <summary>
/// The phrase rail beside a lane (charter-spec C8): a row for each kind of phrase the part can have, in a fixed order, in
/// which every phrase of the part is drawn as a bar, so overlapping ones don't hide each other. The Phrase tool offers
/// the same kinds.
/// </summary>
public static class PhraseRail
{
    private static readonly PhraseKind[] FretKinds = [PhraseKind.StarPower, PhraseKind.Solo, PhraseKind.BigRockEnding];
    private static readonly PhraseKind[] DrumKinds = [PhraseKind.StarPower, PhraseKind.Solo, PhraseKind.DrumFill, PhraseKind.Roll, PhraseKind.Trill];

    /// <summary>
    /// The kinds of phrase a part of <paramref name="kind"/> can have, in the rail's order. Drums have no big rock ending
    /// of their own: theirs is a fill after <c>[coda]</c> (C8 item 4), in the fills' row.
    /// </summary>
    public static IReadOnlyList<PhraseKind> KindsOf(ChartInstrumentKind kind) => kind == ChartInstrumentKind.Drums ? DrumKinds : FretKinds;

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
        if (point.Rail is not { } row)
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

        if (best is not { } hit)
            return null;
        double grip = Math.Min(point.Tolerance, hit.Length / 4.0);
        double fromStart = Math.Abs(point.Tick - hit.Tick), fromEnd = Math.Abs(point.Tick - (hit.Tick + hit.Length));
        var grabbed = fromEnd <= grip && fromEnd < fromStart ? PhraseGrip.End
            : fromStart <= grip ? PhraseGrip.Start
            : PhraseGrip.Body;
        return new PhraseHit(hit, grabbed);
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
