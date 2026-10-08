using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting.Editing;

/// <summary>What <see cref="EditorTools.SetSustains"/> does to each selected note.</summary>
public enum SustainChange
{
    /// <summary>No sustain: a plain note.</summary>
    Clear,

    /// <summary>Held for one step of the grid.</summary>
    GridStep,

    /// <summary>Held until the next chord of the track, the last note keeping its own.</summary>
    UntilNextNote,
}

// The transforms on the selection (charter-spec C15 item 4): lefty flip, shifting lanes, sustains and stretching the timing.
// Each is one step over every track the selection covers, notes or a range across lanes.
public sealed partial class EditorTools
{
    private const string NothingToTransform = "Select notes, or a range across lanes, first.";
    private const string NoDrumFlip = "Drums aren't flipped: mirroring the pads would change what's a cymbal, a tom and a disco flip.";
    private const string NoDrumSustain = "Drum notes have no sustains.";
    private const string NothingToStretch = "Select notes or a span to stretch or squeeze.";

    /// <summary>
    /// Mirrors the selected notes across the lanes (lefty flip): green swaps with orange, and on GHL the first column with
    /// the third, each note keeping its colour. Open notes stay. Drums are refused.
    /// </summary>
    public void LeftyFlip() => TransformLanes(static (track, notes) =>
    {
        if (track.Kind == ChartInstrumentKind.Drums)
            return (null, NoDrumFlip);
        return (Relaned(notes, n => n.Lane == ChartNote.OpenLane ? n.Lane
            : track.Kind == ChartInstrumentKind.SixFret ? (n.Lane < 3 ? 2 - n.Lane : 8 - n.Lane)
            : 4 - n.Lane), null);
    });

    /// <summary>
    /// Shifts the selected notes <paramref name="lanes"/> lanes up, or down when negative, each stopping at the edge, and
    /// notes of a chord that land together becoming one. On GHL a note moves between columns, keeping its colour. Open
    /// notes and kicks stay where they are.
    /// </summary>
    public void ShiftLanes(int lanes)
    {
        if (lanes == 0)
            return;
        TransformLanes((track, notes) =>
        {
            int lastPad = _session.IsFiveLaneDrums ? 5 : 4;
            return (Relaned(notes, n => track.Kind switch
            {
                _ when n.Lane == ChartNote.OpenLane => n.Lane,
                ChartInstrumentKind.Drums => n.Lane == 0 ? 0 : Math.Clamp(n.Lane + lanes, 1, lastPad),
                ChartInstrumentKind.SixFret => (n.Lane / 3 * 3) + Math.Clamp((n.Lane % 3) + lanes, 0, 2),
                _ => Math.Clamp(n.Lane + lanes, 0, 4),
            }), null);
        });
    }

    /// <summary>Sets or clears the selected notes' sustains (charter-spec C15 item 4), as one step.</summary>
    public void SetSustains(SustainChange change) => TransformLengths((track, notes) =>
    {
        if (track.Kind == ChartInstrumentKind.Drums)
            return (null, NoDrumSustain);
        var lengths = new List<(EditableNote Note, long Length)>();
        foreach (var note in notes)
        {
            long length = change switch
            {
                SustainChange.Clear => EditableNote.PlainLength,
                SustainChange.GridStep => Math.Max(EditableNote.PlainLength, Snap.Step(note.Tick, 1, Map) - note.Tick),
                _ => NextChordAfter(track, note.Tick) is { } next ? next - note.Tick : note.Length,
            };
            if (length != note.Length)
                lengths.Add((note, length));
        }

        return (lengths, null);
    });

    /// <summary>
    /// Stretches or squeezes the timing of the selection's span by <paramref name="factor"/> (2 makes it twice as long), or
    /// to end at the cursor when null (charter-spec C15 item 4), keeping every note's time: for fixing the double- and
    /// half-time sections imported charts often have.
    /// </summary>
    public void StretchSelection(double? factor)
    {
        if (_session.Selection.Span is not { } span || span.To <= span.From)
        {
            _session.Status = NothingToStretch;
            return;
        }

        long newTo = factor is { } f
            ? span.From + Math.Max(1, (long)Math.Round((span.To - span.From) * f))
            : _session.CursorTick;
        Cancel();
        var edit = TempoMapEdits.Stretch(Chart, span.From, span.To, newTo);
        if (edit.IsRefused)
        {
            _session.Status = edit.Refusal;
            return;
        }

        var selection = _session.Selection;
        var tracks = selection.HasRangeAcrossLanes ? selection.RangeTracks.ToList() : null;
        if (_session.Execute(edit.Command!) && tracks is not null)
            selection.SelectRange(span.From, newTo, tracks);
    }

    /// <summary>The tick of the first chord after <paramref name="tick"/> in <paramref name="track"/>, or null for none.</summary>
    private static long? NextChordAfter(EditableTrack track, long tick)
    {
        int index = track.Notes.FirstAfter(tick);
        return index < track.Notes.Count ? track.Notes[index].Tick : null;
    }

    /// <summary>The notes moved to the lanes <paramref name="lane"/> gives them, those of a chord landing on one place becoming one.</summary>
    private static List<EditableNote> Relaned(IReadOnlyList<EditableNote> notes, Func<EditableNote, int> lane)
    {
        var placed = new Dictionary<NotePlace, EditableNote>();
        foreach (var note in notes)
        {
            var moved = note with { Lane = lane(note) };
            var place = NotePlace.Of(moved);
            if (!placed.TryGetValue(place, out var there) || moved.Length > there.Length)
                placed[place] = moved;
        }

        var list = placed.Values.ToList();
        list.Sort(EditableNote.Order);
        return list;
    }

    // Puts each track's notes where the transform moves them, as one step, and selects them there.
    private void TransformLanes(Func<EditableTrack, IReadOnlyList<EditableNote>, (List<EditableNote>? Moved, string? Refusal)> transform) =>
        Transform((track, notes) =>
        {
            var (moved, why) = transform(track, notes);
            return (moved, null, why);
        });

    // Gives each track's notes the lengths the transform gives them, as one step.
    private void TransformLengths(Func<EditableTrack, IReadOnlyList<EditableNote>, (List<(EditableNote Note, long Length)>? Lengths, string? Refusal)> transform) =>
        Transform((track, notes) =>
        {
            var (lengths, why) = transform(track, notes);
            return (null, lengths, why);
        });

    private void Transform(Func<EditableTrack, IReadOnlyList<EditableNote>, (List<EditableNote>? Moved, List<(EditableNote Note, long Length)>? Lengths, string? Refusal)> transform)
    {
        var selection = _session.Selection;
        var tracks = selection.NotesByTrack();
        if (tracks.Count == 0)
        {
            _session.Status = NothingToTransform;
            return;
        }

        Cancel();
        var commands = new List<IChartCommand>();
        var reselect = new List<(ChartTrackKey Key, IReadOnlyList<EditableNote> Notes)>();
        foreach (var (key, notes) in tracks)
        {
            var track = _session.TrackOf(key)!;
            var (moved, lengths, why) = transform(track, notes);
            if (why is not null)
            {
                _session.Status = why;
                return;
            }

            if (moved is not null)
            {
                if (PlacementRefusal(track, moved, notes as IReadOnlyCollection<EditableNote> ?? [.. notes]) is { } refusal)
                {
                    _session.Status = refusal;
                    return;
                }

                var plan = NoteMergeOf(track, notes, moved);
                if (plan is not null)
                    commands.Add(new ReplaceNotes(key.Instrument, key.Difficulty, plan.Value.Remove, plan.Value.Add));
                reselect.Add((key, moved));
            }
            else if (lengths is { Count: > 0 })
            {
                commands.Add(new SetSustain(key.Instrument, key.Difficulty, lengths));
                var changed = lengths.ToDictionary(static l => l.Note, static l => l.Length);
                reselect.Add((key, [.. notes.Select(n => changed.TryGetValue(n, out long length) ? n with { Length = length } : n)]));
            }
        }

        if (commands.Count == 0)
            return;
        if (!_session.Execute(commands.Count == 1 ? commands[0] : new CompositeCommand(commands)))
            return;
        if (!selection.HasRangeAcrossLanes && reselect.Count == 1)
            selection.Select(reselect[0].Key, reselect[0].Notes);
    }

    // What taking out the notes and putting in the moved ones changes: notes that stay as they were are neither.
    private static (List<EditableNote> Remove, List<EditableNote> Add)? NoteMergeOf(EditableTrack track, IReadOnlyList<EditableNote> notes, List<EditableNote> moved)
    {
        var remove = notes.Except(moved).ToList();
        var add = moved.Except(notes).ToList();

        // A note moved onto one that isn't selected would be refused; it takes that note's place instead.
        var leaving = new HashSet<NotePlace>(remove.Select(NotePlace.Of));
        foreach (var note in add)
        {
            if (track.TryGetNote(note.Tick, note.Lane, note.DoubleKick, out var there) && !notes.Contains(there) && leaving.Add(NotePlace.Of(there)))
                remove.Add(there);
        }

        return remove.Count == 0 && add.Count == 0 ? null : (remove, add);
    }
}
