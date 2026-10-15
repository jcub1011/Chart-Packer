using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Editing;

/// <summary>What Quantize will do to the notes picked: the chords it moves, as one undoable command, and those it leaves.</summary>
/// <param name="Command">The change, or <see langword="null"/> when nothing changes.</param>
/// <param name="Moved">How many chords moved, or had a sustain's end put on the grid.</param>
/// <param name="Blocked">The ticks of chords that would land on a note that isn't moving, which stay.</param>
/// <param name="Notes">The notes picked, where they'll be once it's done.</param>
public sealed record QuantizePlan(IChartCommand? Command, int Moved, IReadOnlyList<long> Blocked, IReadOnlyList<EditableNote> Notes);

/// <summary>
/// Quantize: moves notes to the nearest step of a grid, chord by chord, with each sustain's end on the grid too; a sustain
/// that ends where it starts is a plain note. For notes recorded as played (<see cref="RecordPass.Quantize"/>), put on the
/// grid afterwards.
/// </summary>
/// <remarks>
/// Chords that land on one tick are one chord, which is how notes played a little apart become the chord they were meant
/// to be: two notes at one place are one, the longer, and on frets an open note that lands with frets is left out. A chord
/// that would land on a note that isn't moving, or make an open note and frets one chord with it, stays where it is.
/// </remarks>
public static class NoteQuantize
{
    /// <summary>What quantising <paramref name="notes"/> of <paramref name="key"/>'s track to <paramref name="grid"/> would do.</summary>
    public static QuantizePlan Plan(EditorSession session, ChartTrackKey key, IReadOnlyList<EditableNote> notes, SnapGrid grid)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(notes);
        if (session.TrackOf(key) is not { } track || notes.Count == 0)
            return new QuantizePlan(null, 0, [], notes);

        var map = session.TempoMap;
        bool drums = track.Kind == ChartInstrumentKind.Drums;
        var chords = notes.GroupBy(static n => n.Tick).ToDictionary(static g => g.Key, static g => g.ToList());

        // Each chord where it lands, and its notes there.
        var landed = new Dictionary<long, List<EditableNote>>();
        foreach (var (tick, chord) in chords)
        {
            long to = grid.Nearest(tick, map);
            landed[tick] = [.. chord.Select(n => Snapped(n, to, grid, map))];
        }

        // A chord can't land on a note that isn't moving, nor make an open note and frets one chord with one. Blocking one
        // leaves its notes where they are, which may block another, so it's checked until nothing more is.
        var blocked = new List<long>();
        bool changed = true;
        while (changed)
        {
            changed = false;
            var moving = notes.Where(n => landed.ContainsKey(n.Tick)).Select(NotePlace.Of).ToHashSet();
            foreach (var (tick, chord) in landed)
            {
                long to = chord[0].Tick;
                var staying = track.Notes.At(to).ToArray().Where(n => !moving.Contains(NotePlace.Of(n))).ToList();
                if (staying.Count == 0 || !Lands(chord, staying, drums))
                    continue;
                landed.Remove(tick);
                blocked.Add(tick);
                changed = true;
                break;
            }
        }

        // Chords that land together are one: one note at each place, the longest, and on frets no open note with frets.
        var add = landed.Values.SelectMany(static c => c)
            .GroupBy(NotePlace.Of)
            .Select(static g => g.MaxBy(static n => n.Length))
            .ToList();
        if (!drums)
        {
            var fretted = add.Where(static n => n.Lane != ChartNote.OpenLane).Select(static n => n.Tick).ToHashSet();
            add.RemoveAll(n => n.Lane == ChartNote.OpenLane && fretted.Contains(n.Tick));
        }

        var remove = notes.Where(n => landed.ContainsKey(n.Tick)).ToList();
        int moved = landed.Count(c => !c.Value.SequenceEqual(chords[c.Key]));

        // A chord's marking goes with it when the whole chord moved, onto a tick only it lands on.
        var flags = new List<FlaggedChord>();
        if (!drums)
        {
            var removing = remove.Select(NotePlace.Of).ToHashSet();
            foreach (var arrivals in landed.GroupBy(static c => c.Value[0].Tick))
            {
                long to = arrivals.Key;
                long first = arrivals.Min(static c => c.Key);
                bool whole = track.Notes.At(first).Length == chords[first].Count;
                bool alone = track.Notes.At(to).ToArray().All(n => removing.Contains(NotePlace.Of(n)));
                var value = track.FlagsAt(first);
                if (whole && alone && track.FlagsAt(to) != value)
                    flags.Add(new FlaggedChord(to, value));
            }
        }

        var after = add.Concat(notes.Where(n => !landed.ContainsKey(n.Tick))).ToList();

        // A note put back exactly as it was is neither taken out nor put in.
        var kept = remove.Intersect(add).ToHashSet();
        remove.RemoveAll(kept.Contains);
        add.RemoveAll(kept.Contains);
        remove.Sort(EditableNote.Order);
        add.Sort(EditableNote.Order);

        var command = remove.Count == 0 && add.Count == 0 && flags.Count == 0
            ? null
            : new ReplaceNotes(key.Instrument, key.Difficulty, remove, add, flags);
        return new QuantizePlan(command, command is null ? 0 : moved, [.. blocked.Order()], after);
    }

    // A note at its chord's new tick, its sustain ending on the grid; a plain note stays plain.
    private static EditableNote Snapped(EditableNote note, long to, SnapGrid grid, TempoMap map)
    {
        if (note.Length <= EditableNote.PlainLength)
            return note with { Tick = to };
        long length = grid.Nearest(note.Tick + note.Length, map) - to;
        return note with { Tick = to, Length = length > 0 ? length : EditableNote.PlainLength };
    }

    // Whether a chord landing where notes stay would meet one: at its place, or an open note with frets.
    private static bool Lands(List<EditableNote> chord, List<EditableNote> staying, bool drums)
    {
        var places = staying.Select(NotePlace.Of).ToHashSet();
        if (chord.Any(n => places.Contains(NotePlace.Of(n))))
            return true;
        if (drums)
            return false;
        bool open = chord.Any(static n => n.Lane == ChartNote.OpenLane), frets = chord.Any(static n => n.Lane != ChartNote.OpenLane);
        bool openThere = staying.Any(static n => n.Lane == ChartNote.OpenLane), fretsThere = staying.Any(static n => n.Lane != ChartNote.OpenLane);
        return (open && fretsThere) || (frets && openThere);
    }
}
