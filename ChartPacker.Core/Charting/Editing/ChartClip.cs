using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charting.Layout;
using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting.Editing;

/// <summary>
/// A note copied to the editor's clipboard: where it is from the clip's start, in ticks, and the lane it's drawn in, so a
/// passage copied out of a disco flip section keeps the pads the player sees.
/// </summary>
/// <param name="IsTom">Whether it was a Pro drums tom, which a paste into Pro drums keeps.</param>
public readonly record struct ClipNote(long Offset, long Length, int ShownLane, NoteDynamics Dynamics = NoteDynamics.None, bool DoubleKick = false, bool IsTom = false);

/// <summary>A vocal note copied to the editor's clipboard, where it is from the clip's start, with its lyric.</summary>
/// <param name="Lyric">The note's lyric as the track has it, symbols included, or null for none.</param>
/// <param name="LyricType">The meta event the lyric is written as.</param>
public readonly record struct ClipVocalNote(long Offset, long Length, int Pitch, int Velocity, string? Lyric, byte LyricType = VocalTrackContent.LyricType);

/// <summary>A vocal part's notes in a clip (charter-spec C3 item 7), which paste into a vocal lane only.</summary>
public sealed record VocalClip(VocalTrack Source, IReadOnlyList<ClipVocalNote> Notes);

/// <summary>One track's notes in a clip, and its chords' forcing and tap, each by its offset.</summary>
public sealed record ClipTrack(ChartTrackKey Source, ChartInstrumentKind Kind, IReadOnlyList<ClipNote> Notes, IReadOnlyList<FlaggedChord> Flags);

/// <summary>
/// What the editor's clipboard holds (charter-spec C15 item 3): notes of one track or several, or of a vocal part, in
/// ticks from the clip's start, at the resolution they were copied at. Ticks keep the rhythm, so a passage pasted where
/// the tempo differs lands on the grid as it was.
/// </summary>
/// <param name="Length">How many ticks it covers from its start: an overwriting paste clears that much.</param>
/// <param name="Drums">How the drums it was copied from are charted, if it holds drums.</param>
public sealed record ChartClip(int Resolution, long Length, DrumsVariant? Drums, IReadOnlyList<ClipTrack> Tracks)
{
    /// <summary>A vocal part's notes, in place of <see cref="Tracks"/>; null for a clip of highways.</summary>
    public VocalClip? Vocals { get; init; }

    /// <summary>How many notes it holds.</summary>
    public int NoteCount => Tracks.Sum(static t => t.Notes.Count) + (Vocals?.Notes.Count ?? 0);
}

/// <summary>How a 5-fret passage pastes into GHL, or the other way round: the lane each source lane goes to. Opens stay open.</summary>
/// <param name="Lanes">The target lane of each source lane, by index: 5-fret 0 to 4, GHL 0 to 5.</param>
public sealed record LaneMapping(ChartInstrumentKind From, ChartInstrumentKind To, IReadOnlyList<int> Lanes)
{
    /// <summary>
    /// The mapping offered first: 5-fret's green, red and yellow onto GHL's white frets and blue and orange onto the first
    /// two black; GHL's whites onto green, red and yellow and its blacks onto blue, orange and orange.
    /// </summary>
    public static LaneMapping Default(ChartInstrumentKind from, ChartInstrumentKind to) => (from, to) switch
    {
        (ChartInstrumentKind.FiveFret, ChartInstrumentKind.SixFret) => new(from, to, [0, 1, 2, 3, 4]),
        (ChartInstrumentKind.SixFret, ChartInstrumentKind.FiveFret) => new(from, to, [0, 1, 2, 3, 4, 4]),
        _ => throw new ArgumentException("Lanes are mapped between 5-fret and GHL.", nameof(to)),
    };

    /// <summary>The lane <paramref name="lane"/> goes to.</summary>
    public int Map(int lane) => lane == ChartNote.OpenLane ? lane : Lanes[Math.Clamp(lane, 0, Lanes.Count - 1)];
}

/// <summary>How a paste goes in: over the span it covers, in place of what's there, or merged with it.</summary>
public enum PasteMode
{
    /// <summary>Everything in the span the clip covers is taken out first (Ctrl+V).</summary>
    Overwrite,

    /// <summary>Each pasted chord takes the place of the chord at its tick, and the rest stays (Ctrl+Shift+V).</summary>
    Merge,
}

/// <summary>What a paste would do, or why it can't (<see cref="ClipboardEdits.Paste"/>).</summary>
/// <param name="Command">The paste, as one step; null when it's refused or changes nothing.</param>
/// <param name="Refusal">Why it can't be pasted; null when it can.</param>
/// <param name="Placed">The notes put in, by track, to select once it's done.</param>
/// <param name="DroppedDoubleKicks">How many 2× kicks were left out, as only Expert has them.</param>
public sealed record PasteResult(IChartCommand? Command, string? Refusal, IReadOnlyList<(ChartTrackKey Track, IReadOnlyList<EditableNote> Notes)> Placed, int DroppedDoubleKicks);

/// <summary>What a paste into a vocal lane would do, or why it can't (<see cref="ClipboardEdits.PasteVocals"/>).</summary>
/// <param name="Command">The paste, as one step; null when it's refused or changes nothing.</param>
/// <param name="Refusal">Why it can't be pasted; null when it can.</param>
/// <param name="Placed">Where the notes put in are, to select once it's done.</param>
public sealed record VocalPasteResult(IChartCommand? Command, string? Refusal, IReadOnlyList<VocalNotePlace> Placed);

/// <summary>
/// Cut, copy and paste (charter-spec C15 item 3). Pasting goes at a tick and keeps the clip's timing in ticks. It works
/// across difficulties and parts of one family, and between 5-fret and GHL by a lane mapping the user confirms; drums
/// paste only into drums charted the same way.
/// </summary>
/// <remarks>
/// A clip of one track pastes into the focused lane; a clip of several, cut from a range across lanes, pastes into the
/// same parts and difficulties, each of which the chart must have. Phrases and disco flip sections aren't copied.
/// </remarks>
public static class ClipboardEdits
{
    private const string NothingToCopy = "Select notes, or a range across lanes, to copy.";
    private const string NoLane = "Pick a lane to paste into.";
    private const string OtherDrums = "Drums paste only into drums charted the same way: {0} into {1} would change what each pad is.";
    private const string OtherFamily = "A {0} passage can't be pasted into {1}.";
    private const string MissingTrack = "This passage was copied from several lanes, and the chart has no {0} {1} to paste into.";
    private const string NeedsMapping = "Pasting between 5-fret and GHL needs a lane mapping.";
    private const string VocalsOnlyIntoVocals = "Vocal notes paste only into a vocal lane.";
    private const string NotIntoVocals = "A highway's notes can't be pasted into a vocal lane.";
    private const string NoVocalPart = "The chart has no {0} track to paste into. Add it first.";
    private const string VocalsUnreadable = "This vocal track can't be read, so it's kept as it is.";

    /// <summary>The selected notes as a clip, or null, with the status saying why, when none are selected.</summary>
    public static ChartClip? Copy(EditorSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var selection = session.Selection;
        if (selection.Vocal is { } vocal && selection.HasVocalNotes)
            return CopyVocals(session, vocal);
        var tracks = selection.NotesByTrack();
        if (tracks.Count == 0)
        {
            session.Status = NothingToCopy;
            return null;
        }

        long start, end;
        if (selection.HasRangeAcrossLanes && selection.Range is { } range)
        {
            (start, end) = range;
        }
        else
        {
            start = tracks.Min(static t => t.Notes[0].Tick);
            end = tracks.Max(static t => t.Notes.Max(static n => n.Tick + n.Length));
        }

        var chart = session.Chart;
        bool pro = session.IsProDrums;
        var clips = new List<ClipTrack>(tracks.Count);
        foreach (var (key, notes) in tracks)
        {
            var part = chart.PartOf(key.Instrument)!;
            var track = part[key.Difficulty];
            bool drums = part.Kind == ChartInstrumentKind.Drums;
            var clipNotes = notes.Select(n =>
            {
                int shown = session.ShownLane(key, n);
                bool tom = drums && pro && shown == n.Lane && n.Lane is >= TomMarker.FirstPad and <= TomMarker.LastPad && DrumCymbals.IsTom(part, n.Lane, n.Tick);
                return new ClipNote(n.Tick - start, n.Length, shown, n.Dynamics, n.DoubleKick, tom);
            }).ToList();
            var flags = drums ? [] : notes.Select(static n => n.Tick).Distinct()
                .Select(t => new FlaggedChord(t - start, track.FlagsAt(t)))
                .Where(static f => f.Flags != ChordFlags.None)
                .ToList();
            clips.Add(new ClipTrack(key, part.Kind, clipNotes, flags));
        }

        bool hasDrums = clips.Any(static c => c.Kind == ChartInstrumentKind.Drums);
        return new ChartClip(chart.Resolution, Math.Max(0, end - start), hasDrums ? session.DrumsVariantShown : null, clips);
    }

    /// <summary>
    /// Where <paramref name="clip"/> would paste, and whether it needs a lane mapping first: the focused lane for a clip of
    /// one track, the same tracks for one of several.
    /// </summary>
    /// <returns>Why it can't be pasted, or null when it can.</returns>
    public static string? CanPaste(EditorSession session, ChartClip clip, out LaneMapping? mappingNeeded) =>
        CanPaste(session, clip, null, out mappingNeeded);

    /// <summary>
    /// Where <paramref name="clip"/> would paste, as <see cref="CanPaste(EditorSession, ChartClip, out LaneMapping?)"/>
    /// says, or into the vocal part <paramref name="into"/>, when it's pasted in a vocal lane: a vocal part's notes paste
    /// only into one, and a highway's never do.
    /// </summary>
    /// <returns>Why it can't be pasted, or null when it can.</returns>
    public static string? CanPaste(EditorSession session, ChartClip clip, VocalTrack? into, out LaneMapping? mappingNeeded)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(clip);
        mappingNeeded = null;
        if (clip.Vocals is not null || into is not null)
            return VocalRefusal(session, clip, into);
        if (Targets(session, clip, out string? refusal) is not { } targets)
            return refusal;
        foreach (var (source, target) in targets)
        {
            var kind = ChartInstruments.KindOf(target.Instrument);
            if (FamilyRefusal(session, clip, source.Kind, kind) is { } why)
                return why;
            if (source.Kind != kind)
                mappingNeeded = LaneMapping.Default(source.Kind, kind);
        }

        return null;
    }

    /// <summary>
    /// The paste of <paramref name="clip"/> with its start at <paramref name="atTick"/>, as one step, or why it can't be
    /// pasted.
    /// </summary>
    /// <param name="mapping">The lane mapping the user confirmed, for a paste between 5-fret and GHL.</param>
    public static PasteResult Paste(EditorSession session, ChartClip clip, long atTick, PasteMode mode, LaneMapping? mapping = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.Vocals is not null)
            return Refused(VocalsOnlyIntoVocals);
        if (Targets(session, clip, out string? refusal) is not { } targets)
            return Refused(refusal!);

        var chart = session.Chart;
        int resolution = chart.Resolution;
        long Scale(long ticks) => clip.Resolution == resolution ? ticks : ResolutionScaling.Scale(ticks, clip.Resolution, resolution);
        long length = Scale(clip.Length);
        var commands = new List<IChartCommand>();
        var placed = new List<(ChartTrackKey, IReadOnlyList<EditableNote>)>();
        int droppedKicks = 0;

        foreach (var (source, target) in targets)
        {
            var part = chart.PartOf(target.Instrument)!;
            var track = part[target.Difficulty];
            if (FamilyRefusal(session, clip, source.Kind, part.Kind) is { } why)
                return Refused(why);
            if (source.Kind != part.Kind && (mapping is null || mapping.From != source.Kind || mapping.To != part.Kind))
                return Refused(NeedsMapping);

            bool drums = part.Kind == ChartInstrumentKind.Drums;
            var incoming = new Dictionary<NotePlace, EditableNote>();
            var toms = new List<(int Pad, long Tick)>();
            var cymbals = new List<(int Pad, long Tick)>();
            foreach (var note in source.Notes)
            {
                if (note.DoubleKick && target.Difficulty != ChartDifficulty.Expert)
                {
                    droppedKicks++;
                    continue;
                }

                long tick = atTick + Scale(note.Offset);
                int shown = source.Kind != part.Kind ? mapping!.Map(note.ShownLane) : note.ShownLane;
                if (shown > EditableNote.LastLane(part.Kind) || (drums && shown == 5 && session.DrumsVariantShown != DrumsVariant.FiveLane))
                    continue;
                int lane = session.StoredLane(target, tick, shown);
                long noteLength = drums ? EditableNote.PlainLength : Math.Max(EditableNote.PlainLength, Scale(note.Length));
                var placedNote = new EditableNote(tick, noteLength, lane, drums ? note.Dynamics : NoteDynamics.None, note.DoubleKick);
                var place = NotePlace.Of(placedNote);
                if (!incoming.TryGetValue(place, out var there) || placedNote.Length > there.Length)
                    incoming[place] = placedNote;

                // Pro drums keep whether each pad was a tom; the markers belong to the whole part.
                if (drums && session.IsProDrums && lane == shown && lane is >= TomMarker.FirstPad and <= TomMarker.LastPad
                    && DrumCymbals.IsTom(part, lane, tick) != note.IsTom)
                    (note.IsTom ? toms : cymbals).Add((lane, tick));
            }

            var notes = incoming.Values.ToList();
            var flags = drums ? [] : source.Flags.Select(f => f with { Tick = atTick + Scale(f.Tick) }).ToList();
            var plan = NoteMerge.Plan(track, notes, flags, mode == PasteMode.Overwrite ? (atTick, atTick + length) : null);
            if (!plan.IsEmpty || plan.Flags.Count > 0)
                commands.Add(new ReplaceNotes(target.Instrument, target.Difficulty, plan.Remove, plan.Add, plan.Flags));
            if (toms.Count > 0)
                commands.Add(new SetToms(toms.Distinct(), tom: true, target.Difficulty));
            if (cymbals.Count > 0)
                commands.Add(new SetToms(cymbals.Distinct(), tom: false, target.Difficulty));
            notes.Sort(EditableNote.Order);
            placed.Add((target, notes));
        }

        var command = commands.Count switch
        {
            0 => null,
            1 => commands[0],
            _ => new CompositeCommand(commands),
        };
        return new PasteResult(command, null, placed, droppedKicks);
    }

    /// <summary>
    /// The paste of <paramref name="clip"/>'s vocal notes into the vocal part <paramref name="into"/>, with its start at
    /// <paramref name="atTick"/>, as one step, or why it can't be pasted. Overwriting takes out the notes starting in the
    /// span the clip covers first; merging, those starting on a pasted note's tick. Each takes its lyrics with it.
    /// </summary>
    public static VocalPasteResult PasteVocals(EditorSession session, ChartClip clip, VocalTrack into, long atTick, PasteMode mode)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(clip);
        if (VocalRefusal(session, clip, into) is { } refusal)
            return new(null, refusal, []);
        var content = session.VocalContentOf(into)!;
        var source = clip.Vocals!;

        int resolution = session.Chart.Resolution;
        long Scale(long ticks) => clip.Resolution == resolution ? ticks : ResolutionScaling.Scale(ticks, clip.Resolution, resolution);
        long end = atTick + Scale(clip.Length);
        var notes = source.Notes
            .Select(n => (Note: new VocalNoteEvent(atTick + Scale(n.Offset), Scale(n.Length), n.Pitch, n.Velocity), n.Lyric, n.LyricType))
            .ToList();
        var ticks = notes.Select(static n => n.Note.Tick).ToHashSet();

        var cleared = VocalEdits.Remove(content, content.Notes
            .Where(n => mode == PasteMode.Overwrite ? n.Tick >= atTick && (n.Tick < end || ticks.Contains(n.Tick)) : ticks.Contains(n.Tick))
            .Select(static n => n.Place));
        var lyrics = notes
            .Where(static n => n.Lyric is not null)
            .DistinctBy(static n => n.Note.Tick)
            .Select(static n => new VocalLyricEvent(n.Note.Tick, n.Lyric!, n.LyricType));
        VocalTrackContent to;
        try
        {
            to = cleared.With(
                notes: cleared.Notes.Concat(notes.Select(static n => n.Note)),
                lyrics: cleared.Lyrics.Where(l => !ticks.Contains(l.Tick)).Concat(lyrics));
        }
        catch (ArgumentException e)
        {
            return new(null, e.Message, []);
        }

        var placed = notes.Select(static n => n.Note.Place).Distinct().ToList();
        return new(ReferenceEquals(to, content) ? null : new ChangeVocals(into, to), null, placed);
    }

    private static PasteResult Refused(string why) => new(null, why, [], 0);

    private static ChartClip? CopyVocals(EditorSession session, VocalTrack track)
    {
        var content = session.VocalContentOf(track);
        var notes = session.Selection.VocalNotes;
        if (content is null || notes.Count == 0)
        {
            session.Status = NothingToCopy;
            return null;
        }

        long start = notes[0].Tick;
        long end = notes.Max(static n => n.EndTick);
        var clipNotes = notes.Select(n =>
        {
            var lyric = content.Lyrics.FirstOrDefault(l => l.Tick == n.Tick);
            return new ClipVocalNote(n.Tick - start, n.Length, n.Pitch, n.Velocity, lyric.Text, lyric.Text is null ? VocalTrackContent.LyricType : lyric.Type);
        }).ToList();
        return new ChartClip(session.Chart.Resolution, Math.Max(0, end - start), null, []) { Vocals = new VocalClip(track, clipNotes) };
    }

    // Why the clip can't paste into the vocal part, or a highway when that's null; null when it can.
    private static string? VocalRefusal(EditorSession session, ChartClip clip, VocalTrack? into)
    {
        if (into is not { } track)
            return VocalsOnlyIntoVocals;
        if (clip.Vocals is null)
            return NotIntoVocals;
        if (VocalTracks.IndexIn(session.Chart, track) < 0)
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, NoVocalPart, VocalTracks.NameOf(track));
        return session.VocalContentOf(track) is null ? VocalsUnreadable : null;
    }

    // Where each of the clip's tracks pastes, or null, with why, when one can't.
    private static List<(ClipTrack Source, ChartTrackKey Target)>? Targets(EditorSession session, ChartClip clip, out string? refusal)
    {
        refusal = null;
        if (clip.Tracks.Count == 1)
        {
            if (session.Focused is not { } focused || session.TrackOf(focused) is null)
            {
                refusal = NoLane;
                return null;
            }

            return [(clip.Tracks[0], focused)];
        }

        var targets = new List<(ClipTrack, ChartTrackKey)>();
        foreach (var source in clip.Tracks)
        {
            if (session.TrackOf(source.Source) is null)
            {
                refusal = string.Format(System.Globalization.CultureInfo.InvariantCulture, MissingTrack, source.Source.Instrument, source.Source.Difficulty);
                return null;
            }

            targets.Add((source, source.Source));
        }

        return targets;
    }

    // Why notes of one family can't paste into another; null when they can, with a mapping between 5-fret and GHL.
    private static string? FamilyRefusal(EditorSession session, ChartClip clip, ChartInstrumentKind from, ChartInstrumentKind to)
    {
        bool fromDrums = from == ChartInstrumentKind.Drums;
        bool toDrums = to == ChartInstrumentKind.Drums;
        if (fromDrums != toDrums)
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, OtherFamily, Name(from), Name(to));
        if (fromDrums && clip.Drums != session.DrumsVariantShown)
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, OtherDrums, Name(clip.Drums), Name(session.DrumsVariantShown));
        return null;
    }

    private static string Name(ChartInstrumentKind kind) => kind switch
    {
        ChartInstrumentKind.FiveFret => "5-fret",
        ChartInstrumentKind.SixFret => "GHL",
        _ => "drums",
    };

    private static string Name(DrumsVariant? variant) => variant switch
    {
        DrumsVariant.Pro => "Pro drums",
        DrumsVariant.FiveLane => "5-lane drums",
        _ => "4-lane drums",
    };
}
