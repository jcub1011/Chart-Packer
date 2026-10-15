using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Editing;

/// <summary>What putting notes into a track takes out of it, and puts in (<see cref="NoteMerge.Plan"/>).</summary>
/// <param name="Remove">The track's notes taken out.</param>
/// <param name="Add">The notes put in.</param>
/// <param name="Flags">The marking each chord put in gets, in place of what its tick had.</param>
public sealed record NoteMergePlan(IReadOnlyList<EditableNote> Remove, IReadOnlyList<EditableNote> Add, IReadOnlyList<FlaggedChord> Flags)
{
    /// <summary>Whether it changes nothing.</summary>
    public bool IsEmpty => Remove.Count == 0 && Add.Count == 0;
}

/// <summary>
/// How notes pasted or recorded go into a track (charter-spec C15 items 3 and 5): over a stretch, in place of everything
/// there, or merged with what's there.
/// </summary>
/// <remarks>
/// Merging goes chord by chord on frets: a chord put in takes the place of the chord at its tick, so an open note never
/// meets frets and a shape stays the shape it was. Drums merge note by note, so a kick recorded over a groove keeps its
/// snares, and a note put in takes the place only of the one at its place.
/// </remarks>
public static class NoteMerge
{
    /// <summary>The notes and markings that putting <paramref name="incoming"/> into <paramref name="track"/> changes.</summary>
    /// <param name="incoming">The notes, in the lanes they're stored in, no two at one place.</param>
    /// <param name="incomingFlags">The incoming chords' markings; a chord not listed is unmarked.</param>
    /// <param name="clear">The ticks, both ends included, whose notes are all taken out first; null to merge.</param>
    public static NoteMergePlan Plan(EditableTrack track, IReadOnlyList<EditableNote> incoming, IReadOnlyList<FlaggedChord> incomingFlags, (long From, long To)? clear)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(incomingFlags);

        var remove = new HashSet<EditableNote>();
        if (clear is var (from, to) && to >= from)
        {
            foreach (var note in track.Notes.Between(from, to))
                remove.Add(note);
        }

        bool drums = track.Kind == ChartInstrumentKind.Drums;
        foreach (var chord in incoming.GroupBy(static n => n.Tick))
        {
            foreach (var there in track.Notes.At(chord.Key))
            {
                if (!drums || chord.Any(n => n.Lane == there.Lane && n.DoubleKick == there.DoubleKick))
                    remove.Add(there);
            }
        }

        // A note put back exactly as it was is neither taken out nor put in.
        var add = new List<EditableNote>(incoming.Count);
        foreach (var note in incoming)
        {
            if (!remove.Remove(note))
                add.Add(note);
        }

        var flags = new List<FlaggedChord>();
        if (!drums)
        {
            var marked = incomingFlags.ToDictionary(static f => f.Tick, static f => f.Flags);
            foreach (long tick in incoming.Select(static n => n.Tick).Distinct().Order())
            {
                var value = marked.GetValueOrDefault(tick);
                if (track.FlagsAt(tick) != value)
                    flags.Add(new FlaggedChord(tick, value));
            }
        }

        var removed = remove.ToList();
        removed.Sort(EditableNote.Order);
        add.Sort(EditableNote.Order);
        return new NoteMergePlan(removed, add, flags);
    }
}
