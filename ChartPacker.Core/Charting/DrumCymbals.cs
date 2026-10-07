using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting;

/// <summary>
/// Whether Pro drums' yellow, blue and green notes are cymbals or toms (charter-spec C6 item 3), as the part's tom
/// markers say. The markers apply to every difficulty, so it's held per tick and pad for the whole part.
/// </summary>
/// <remarks>
/// The commands keep each pad's markers from overlapping, as a MIDI file's can't (a second note-on while one is held is
/// ignored), so the marker covering a tick, if there's one, is the last on its pad that starts by then.
/// </remarks>
public static class DrumCymbals
{
    /// <summary>Whether a note on <paramref name="pad"/> (2 yellow, 3 blue, 4 green) at <paramref name="tick"/> is a tom.</summary>
    public static bool IsTom(EditablePart part, int pad, long tick)
    {
        ArgumentNullException.ThrowIfNull(part);
        var markers = part.TomMarkers;
        for (int i = markers.FirstAfter(tick) - 1; i >= 0; i--)
        {
            if (markers[i].Pad == pad)
                return Covers(markers[i], tick);
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="marker"/> covers <paramref name="tick"/>: from its start to the tick before its end, or its
    /// start alone when it has no length, as the games read it.
    /// </summary>
    public static bool Covers(TomMarker marker, long tick) => tick >= marker.Tick && tick < CoverEnd(marker);

    /// <summary>The tick after the last one <paramref name="marker"/> covers.</summary>
    public static long CoverEnd(TomMarker marker) => marker.Tick + Math.Max(1, marker.Length);

    /// <summary>
    /// Whether any difficulty of <paramref name="part"/> has a note on <paramref name="pad"/> from
    /// <paramref name="fromTick"/> up to, not including, <paramref name="toTick"/>.
    /// </summary>
    internal static bool HasPadNote(EditablePart part, int pad, long fromTick, long toTick)
    {
        if (toTick <= fromTick)
            return false;
        foreach (var track in part.Tracks)
        {
            foreach (var note in track.Notes.Between(fromTick, toTick - 1))
            {
                if (note.Lane == pad)
                    return true;
            }
        }

        return false;
    }

    /// <summary>The difficulties of <paramref name="part"/> with a note on <paramref name="pad"/> at <paramref name="tick"/>, easiest first.</summary>
    internal static IEnumerable<ChartDifficulty> DifficultiesWithNote(EditablePart part, int pad, long tick) =>
        part.Tracks.Where(t => t.TryGetNote(tick, pad, doubleKick: false, out _)).Select(static t => t.Difficulty);
}
