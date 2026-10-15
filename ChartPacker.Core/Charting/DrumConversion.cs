using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting;

/// <summary>What a drum conversion did to a note it couldn't convert as the mapping says.</summary>
public enum DrumConversionOutcome
{
    /// <summary>Left out: another note landed on its pad at its tick, and was kept.</summary>
    LeftOut,

    /// <summary>
    /// Kept, as the other of cymbal and tom: a harder difficulty's note on its pad at its tick decided it, cymbal or tom
    /// being the whole part's.
    /// </summary>
    CymbalOrTomChanged,
}

/// <summary>A note a drum conversion couldn't convert as the mapping says (charter-spec C6 item 8).</summary>
/// <param name="Difficulty">The difficulty it's on.</param>
/// <param name="Tick">Its tick.</param>
/// <param name="Pad">Its pad before the conversion, numbered as <see cref="EditableNote.Lane"/> numbers drum pads.</param>
/// <param name="Tom">Whether it was a tom before the conversion (Pro and 4-lane yellow, blue and green only).</param>
/// <param name="Outcome">What became of it.</param>
public sealed record DrumConversionNote(ChartDifficulty Difficulty, long Tick, int Pad, bool Tom, DrumConversionOutcome Outcome);

/// <summary>What a drum conversion couldn't convert as the mapping says, for its report.</summary>
/// <param name="Notes">The notes, in order of tick, then difficulty, then pad.</param>
/// <param name="FromPlainFourLane">
/// Whether the drums were 4-lane, whose cymbals and toms the player never saw, so the conversion took them as the tom
/// markers stood (charter-spec C6 item 9).
/// </param>
public sealed record DrumConversionReport(IReadOnlyList<DrumConversionNote> Notes, bool FromPlainFourLane);

/// <summary>
/// The fixed mapping between 4-lane or Pro drums and 5-lane drums (charter-spec §7.6), chosen to round-trip. The kick,
/// the 2× kick and red are unchanged, and every note keeps its length, accent or ghost.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>To 5-lane: a yellow cymbal is yellow, a yellow tom blue, a blue cymbal orange, a blue tom blue, a green cymbal
/// orange and a green tom green. A fifth-pad note, which 4-lane and Pro drums shouldn't have (CHR046), is taken as the
/// green tom the games play it as. Of two notes landing on one pad, the one that converts back to itself is kept. The
/// tom markers go, as 5-lane drums have none.</item>
/// <item>From 5-lane: yellow is a yellow cymbal, blue a blue tom, orange a green cymbal and green a green tom. Of orange
/// and green on one tick, orange is kept. Cymbal or tom is the whole part's, so where one difficulty's green cymbal
/// meets another's green tom, the hardest difficulty's is taken, as <see cref="ChartToMidi"/> takes it. The tom markers
/// are made anew, as few as cover the toms and no cymbal.</item>
/// </list>
/// </remarks>
internal static class DrumConversion
{
    /// <summary>
    /// The notes of each difficulty of <paramref name="drums"/> and its tom markers once converted from
    /// <paramref name="from"/> to <paramref name="to"/>, one of which is 5-lane, and what couldn't be converted as the
    /// mapping says.
    /// </summary>
    public static (EditableNote[][] Notes, TomMarker[] Markers, DrumConversionReport Report) Convert(EditablePart drums, DrumsVariant from, DrumsVariant to)
    {
        if ((from == DrumsVariant.FiveLane) == (to == DrumsVariant.FiveLane))
            throw new ArgumentException("A conversion goes to or from 5-lane drums.", nameof(to));

        var report = new List<DrumConversionNote>();
        var notes = new EditableNote[drums.Tracks.Count][];
        TomMarker[] markers;
        if (to == DrumsVariant.FiveLane)
        {
            foreach (var track in drums.Tracks)
                notes[(int)track.Difficulty] = ToFiveLane(drums, track, report);
            markers = [];
        }
        else
        {
            foreach (var track in drums.Tracks)
                notes[(int)track.Difficulty] = FromFiveLane(track, report);
            markers = Markers(drums, notes, report);
        }

        report.Sort(static (a, b) => (a.Tick, a.Difficulty, a.Pad).CompareTo((b.Tick, b.Difficulty, b.Pad)));
        return (notes, markers, new DrumConversionReport(report, from == DrumsVariant.FourLane));
    }

    private static EditableNote[] ToFiveLane(EditablePart drums, EditableTrack track, List<DrumConversionNote> report)
    {
        var converted = new List<EditableNote>(track.Notes.Count);
        foreach (var chord in track.Notes.GroupBy(static n => n.Tick))
        {
            var landed = new Dictionary<(int Lane, bool DoubleKick), (EditableNote From, bool Tom, EditableNote To)>();
            foreach (var note in chord)
            {
                bool tom = note.Lane is >= TomMarker.FirstPad and <= TomMarker.LastPad && DrumCymbals.IsTom(drums, note.Lane, note.Tick);
                int lane = note.Lane switch
                {
                    2 => tom ? 3 : 2,
                    3 => tom ? 3 : 4,
                    4 => tom ? 5 : 4,
                    5 => 5,
                    _ => note.Lane,
                };

                var to = note with { Lane = lane };
                var key = (lane, note.DoubleKick);
                if (landed.TryGetValue(key, out var there))
                {
                    // The note that converts back to itself is kept.
                    bool keepNew = !ConvertsBack(there.From.Lane, there.Tom, lane) && ConvertsBack(note.Lane, tom, lane);
                    var (kept, left, leftTom) = keepNew ? ((note, tom, to), there.From, there.Tom) : (there, note, tom);
                    landed[key] = kept;
                    report.Add(new DrumConversionNote(track.Difficulty, left.Tick, left.Lane, leftTom, DrumConversionOutcome.LeftOut));
                }
                else
                {
                    landed[key] = (note, tom, to);
                }
            }

            converted.AddRange(landed.Values.Select(static v => v.To).Order(NoteOrder.Instance));
        }

        return [.. converted];
    }

    /// <summary>Whether a note on <paramref name="pad"/> (a tom when <paramref name="tom"/>) converts back from 5-lane <paramref name="lane"/> to where it was.</summary>
    private static bool ConvertsBack(int pad, bool tom, int lane) => lane switch
    {
        2 => pad == 2 && !tom,
        3 => pad == 3 && tom,
        4 => pad == 4 && !tom,
        5 => pad == 4 && tom,
        _ => pad == lane,
    };

    private static EditableNote[] FromFiveLane(EditableTrack track, List<DrumConversionNote> report)
    {
        var converted = new List<EditableNote>(track.Notes.Count);
        foreach (var chord in track.Notes.GroupBy(static n => n.Tick))
        {
            bool orange = chord.Any(static n => n.Lane == 4);
            foreach (var note in chord)
            {
                if (note.Lane == 5 && orange)
                {
                    report.Add(new DrumConversionNote(track.Difficulty, note.Tick, note.Lane, Tom: false, DrumConversionOutcome.LeftOut));
                    continue;
                }

                converted.Add(note.Lane == 5 ? note with { Lane = 4 } : note);
            }
        }

        converted.Sort(NoteOrder.Instance);
        return [.. converted];
    }

    /// <summary>
    /// The tom markers of drums converted from 5-lane: blue is always a tom, and green a tom where it was 5-lane green on
    /// the hardest difficulty with a note there. Each marker covers a run of toms with no cymbal between.
    /// </summary>
    private static TomMarker[] Markers(EditablePart drums, EditableNote[][] converted, List<DrumConversionNote> report)
    {
        // Was each converted green a 5-lane green (a tom) or an orange (a cymbal)? By tick, the hardest difficulty's.
        var greenTom = new SortedDictionary<long, bool>();
        foreach (var track in drums.Tracks.Reverse())
        {
            foreach (var note in track.Notes)
            {
                if (note.Lane is not (4 or 5))
                    continue;
                if (note.Lane == 5 && track.TryGetNote(note.Tick, 4, doubleKick: false, out _))
                    continue; // Left out for the orange.
                bool tom = note.Lane == 5;
                if (!greenTom.TryAdd(note.Tick, tom) && greenTom[note.Tick] != tom)
                    report.Add(new DrumConversionNote(track.Difficulty, note.Tick, note.Lane, Tom: false, DrumConversionOutcome.CymbalOrTomChanged));
            }
        }

        var blue = new SortedSet<long>();
        foreach (var notes in converted)
        {
            foreach (var note in notes)
            {
                if (note.Lane == 3)
                    blue.Add(note.Tick);
            }
        }

        var markers = new List<TomMarker>();
        Runs(3, blue.Select(static t => (t, true)), markers);
        Runs(4, greenTom.Select(static p => (p.Key, p.Value)), markers);
        markers.Sort(TomMarker.Order);
        return [.. markers];
    }

    /// <summary>Adds to <paramref name="markers"/> one marker on <paramref name="pad"/> over each run of toms among <paramref name="notes"/>, which are in tick order.</summary>
    private static void Runs(int pad, IEnumerable<(long Tick, bool Tom)> notes, List<TomMarker> markers)
    {
        long? start = null, last = null;
        foreach (var (tick, tom) in notes)
        {
            if (tom)
            {
                start ??= tick;
                last = tick;
                continue;
            }

            Close();
        }

        Close();

        void Close()
        {
            if (start is { } s && last is { } l)
                markers.Add(new TomMarker(pad, s, l + 1 - s));
            start = last = null;
        }
    }

    /// <summary>The order of a track's notes, for sorting a list of them.</summary>
    private sealed class NoteOrder : IComparer<EditableNote>
    {
        public static NoteOrder Instance { get; } = new();

        public int Compare(EditableNote x, EditableNote y) => EditableNote.Order(x, y);
    }
}
