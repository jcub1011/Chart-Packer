using System.Globalization;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Layout;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Editing;

/// <summary>
/// A synchronized region: a stretch of one track, or of one vocal part, whose edits are made again in every passage of
/// it that matched it exactly when it was drawn, as one undoable step with the edit (the Sync tool). There's one at a
/// time, and the session owns it (<see cref="EditorSession.Sync"/>).
/// </summary>
/// <remarks>
/// <para>The passages are found as the region is drawn, moved or resized, and never again between: one made to match
/// later isn't synced. A region is kept however few notes it's drawn over, even none; with nothing matching it, it's
/// drawn hashed in yellow (<see cref="IsUnmatched"/>) and syncs nothing. A region is never shorter than a 32nd note
/// (<see cref="MinLength"/>).</para>
/// <para>An edit is copied as what it leaves in the region, not as the command that made it, so every way of editing is
/// synced alike. A passage found is synced only while it holds what the region holds: one edited by hand stops being
/// synced, rather than being written over, as soon as it's edited, and is synced again once it matches again, as when
/// that edit is undone.</para>
/// <para>On a vocal part a passage holds its notes, by offset, length and pitch, and the lyrics on their ticks, by text;
/// its phrases, star power and other events are neither matched nor copied, as copying and pasting the notes leaves them.</para>
/// </remarks>
public sealed class SyncRegion
{
    private const string NoMatches = "No other passage of this lane matches the region exactly, so there's nothing to synchronize.";
    private const string Synchronizing = "Synchronizing {0} passage(s) with the region.";
    private const string StoppedSyncing = "{0} passage(s) no longer match the region, so they aren't synchronized. Undo the edit to synchronize them again.";
    private const string TrackGone = "The synchronized region's lane is gone, so the region was cleared.";
    private const string ResolutionChanged = "The chart's resolution changed, so the synchronized region was cleared.";

    private readonly EditorSession _session;
    private readonly List<long> _found = [];
    private readonly List<long> _matches = [];
    private int _resolution;

    internal SyncRegion(EditorSession session) => _session = session;

    /// <summary>How short a region may be: one 32nd note's worth of ticks at <paramref name="resolution"/>.</summary>
    public static long MinLength(int resolution) => Math.Max(1, resolution / 8);

    /// <summary>Whether a region is drawn.</summary>
    public bool IsActive => Track is not null || Vocal is not null;

    /// <summary>The highway's track the region is drawn on; null with no region, or one on a vocal part.</summary>
    public ChartTrackKey? Track { get; private set; }

    /// <summary>The vocal part the region is drawn on; null with no region, or one on a highway.</summary>
    public VocalTrack? Vocal { get; private set; }

    /// <summary>The lane the region is drawn on: <see cref="Track"/>'s highway or <see cref="Vocal"/>'s part; null with no region.</summary>
    public LanePart? Part => Track is { } key ? LanePart.Highway(key) : Vocal is { } vocal ? LanePart.Vocals(vocal) : null;

    /// <summary>Whether the region is drawn on <paramref name="track"/>'s highway.</summary>
    public bool IsOn(ChartTrackKey track) => Track == track;

    /// <summary>Whether the region is drawn on the vocal part <paramref name="track"/>.</summary>
    public bool IsOn(VocalTrack track) => Vocal == track;

    /// <summary>Whether the region is drawn on <paramref name="part"/>'s lane.</summary>
    public bool IsOn(LanePart part) => part.Track is { } key ? IsOn(key) : part.Vocal is { } vocal && IsOn(vocal);

    /// <summary>The region's ticks, from its start up to, not including, its end; null with no region.</summary>
    public (long From, long To)? Span { get; private set; }

    /// <summary>
    /// Whether nothing matched the region as it was last drawn, moved or resized, which draws it hashed in yellow. It
    /// says nothing of matches lost since, as passages were edited.
    /// </summary>
    public bool IsUnmatched { get; private set; }

    /// <summary>Where each passage found as the region was last drawn, moved or resized starts, earliest first; each is as long as the region.</summary>
    public IReadOnlyList<long> Found => _found;

    /// <summary>
    /// Where each passage found that's synced with the region starts, earliest first: those that hold what the region
    /// holds now.
    /// </summary>
    public IReadOnlyList<long> Matches => _matches;

    /// <summary>The region being dragged out with the Sync tool, on its lane, from its start up to, not including, its end; null when none is.</summary>
    public (LanePart Part, long From, long To)? Draft
    {
        get;
        internal set
        {
            if (field == value)
                return;
            field = value;
            _session.RaiseState();
        }
    }

    /// <summary>
    /// Draws the region over <paramref name="from"/> up to, not including, <paramref name="to"/> of <paramref name="track"/>,
    /// in place of any drawn, and finds the passages that match it. A stretch shorter than a 32nd note is drawn one 32nd
    /// long; one with nothing matching it is kept all the same, hashed in yellow.
    /// </summary>
    /// <returns>Whether it was drawn.</returns>
    public bool Define(ChartTrackKey track, long from, long to) => Define(LanePart.Highway(track), from, to);

    /// <summary>
    /// Draws the region over <paramref name="from"/> up to, not including, <paramref name="to"/> of the vocal part
    /// <paramref name="track"/>, as <see cref="Define(ChartTrackKey, long, long)"/> does a highway's.
    /// </summary>
    /// <returns>Whether it was drawn.</returns>
    public bool Define(VocalTrack track, long from, long to) => Define(LanePart.Vocals(track), from, to);

    /// <summary>Draws the region over <paramref name="from"/> up to, not including, <paramref name="to"/> of <paramref name="part"/>'s lane.</summary>
    /// <returns>Whether it was drawn.</returns>
    internal bool Define(LanePart part, long from, long to)
    {
        Clear();
        Track = part.Track;
        Vocal = part.Vocal;
        if (Set(from, to))
            return true;
        Track = null;
        Vocal = null;
        return false;
    }

    /// <summary>
    /// Moves or resizes the region to <paramref name="from"/> up to, not including, <paramref name="to"/>, and finds the
    /// passages that match it there again, in place of those found before. A stretch shorter than a 32nd note is drawn
    /// one 32nd long.
    /// </summary>
    /// <returns>Whether it was redrawn.</returns>
    public bool Redefine(long from, long to) => Set(from, to);

    /// <summary>
    /// Makes the passage the region's matched passage at <paramref name="start"/> the region itself, so syncing goes on
    /// from it, and finds the passages that match it there again.
    /// </summary>
    /// <returns>Whether it was redrawn.</returns>
    public bool MakeSource(long start) => Span is var (from, to) && Set(start, start + (to - from));

    /// <summary>
    /// Moves or resizes the region to <paramref name="from"/> up to, not including, <paramref name="to"/> as a gesture
    /// drags, finding the passages that match it there again, without saying anything; <see cref="Redefine"/> says what
    /// was found as the gesture ends.
    /// </summary>
    internal void DragTo(long from, long to) => Set(from, to, say: false);

    // Puts the region over from..to of its lane and finds the passages that match it there again, keeping it however
    // few notes it holds; says what was found, unless say is off. False when there's no region there to redraw.
    private bool Set(long from, long to, bool say = true)
    {
        if (to <= from || from < 0)
            return false;
        to = Math.Max(to, from + MinLength(_session.Chart.Resolution));
        List<long>? matches = null;
        if (Track is { } key && _session.Chart.PartOf(key.Instrument) is { } part)
            matches = Matching(key, part, from, to);
        else if (Vocal is { } vocal && _session.VocalContentOf(vocal) is { } content)
            matches = VocalMatching(content, from, to);
        if (matches is null)
            return false;

        Span = (from, to);
        _found.Clear();
        _found.AddRange(matches);
        _matches.Clear();
        _matches.AddRange(matches);
        IsUnmatched = matches.Count == 0;
        _resolution = _session.Chart.Resolution;
        if (say)
            _session.Status = IsUnmatched
                ? NoMatches
                : string.Format(CultureInfo.InvariantCulture, Synchronizing, matches.Count);
        _session.RaiseState();
        return true;
    }

    /// <summary>
    /// The passages of <paramref name="track"/> that match what it holds from <paramref name="from"/> up to, not including,
    /// <paramref name="to"/>, as the region would find them, earliest first, without drawing a region. Empty when the track
    /// is gone or holds no notes there.
    /// </summary>
    public IReadOnlyList<long> Find(ChartTrackKey track, long from, long to)
    {
        if (_session.Chart.PartOf(track.Instrument) is not { } part || from < 0)
            return [];
        return Matching(track, part, from, Math.Max(to, from + MinLength(_session.Chart.Resolution)));
    }

    /// <summary>
    /// The passages of the vocal part <paramref name="track"/> that match what it holds from <paramref name="from"/> up
    /// to, not including, <paramref name="to"/>, as <see cref="Find(ChartTrackKey, long, long)"/> finds a highway's. Empty
    /// when the part is gone, can't be read, or holds no notes there.
    /// </summary>
    public IReadOnlyList<long> Find(VocalTrack track, long from, long to)
    {
        if (_session.VocalContentOf(track) is not { } content || from < 0)
            return [];
        return VocalMatching(content, from, Math.Max(to, from + MinLength(_session.Chart.Resolution)));
    }

    // The passages matching the track's notes from..to; none when it holds none there.
    private List<long> Matching(ChartTrackKey key, EditablePart part, long from, long to)
    {
        var track = part[key.Difficulty];
        var region = PatternAt(part, track, from, to - from);
        return region.Notes.Count == 0 ? [] : FindMatches(part, track, from, to - from, region);
    }

    /// <summary>Takes the region away, syncing nothing more.</summary>
    public void Clear()
    {
        if (!IsActive)
            return;
        Track = null;
        Vocal = null;
        Span = null;
        IsUnmatched = false;
        _found.Clear();
        _matches.Clear();
        _session.RaiseState();
    }

    /// <summary>
    /// <paramref name="command"/>, and with it the copies of what it does to the region in each passage synced, as one
    /// command; <paramref name="command"/> alone when it leaves the region as it was. The chart is left as it was.
    /// </summary>
    /// <exception cref="InvalidOperationException">The command is refused, as it would be done alone.</exception>
    /// <exception cref="ArgumentException">The command is refused, as it would be done alone.</exception>
    internal IChartCommand WithCopies(IChartCommand command)
    {
        if (Vocal is { } vocal)
            return WithVocalCopies(vocal, command);
        if (Track is not { } key || Span is not var (from, to) || _session.Chart.PartOf(key.Instrument) is not { } part)
            return command;
        var chart = _session.Chart;
        long length = to - from;
        var track = part[key.Difficulty];
        var before = PatternAt(part, track, from, length);
        var matched = _found.Where(m => PatternAt(part, track, m, length).SameAs(before)).ToList();

        command.Apply(chart);
        var applied = new List<IChartCommand>();
        try
        {
            // The command may have taken the part away, which ends the region once it's done.
            if (chart.PartOf(key.Instrument) is not { } after)
                return command;
            var region = PatternAt(after, after[key.Difficulty], from, length);
            if (region.SameAs(before))
                return command;

            foreach (long start in matched)
            {
                if (CopyTo(after, key, region, start, length) is not { } copy)
                    continue;

                // A passage the copy is refused in is left as it is, and no longer matches.
                try
                {
                    copy.Apply(chart);
                    applied.Add(copy);
                }
                catch (Exception e) when (EditorSession.IsRefusal(e))
                {
                }
            }

            return applied.Count == 0 ? command : new CompositeCommand([command, .. applied]);
        }
        finally
        {
            // Taken back, to be done again as one step.
            for (int i = applied.Count - 1; i >= 0; i--)
                applied[i].Revert(chart);
            command.Revert(chart);
        }
    }

    /// <summary>
    /// Takes the region away when its lane is gone, or the chart's ticks were scaled to another resolution; otherwise
    /// works out again which passages found match it, saying so when an edit has left some not matching.
    /// </summary>
    internal void OnChanged()
    {
        if (Span is not var (from, to))
            return;
        long length = to - from;
        List<long> matches;
        if (Track is { } key && _session.Chart.PartOf(key.Instrument) is { } part)
        {
            if (_session.Chart.Resolution != _resolution)
            {
                Cleared(ResolutionChanged);
                return;
            }

            var track = part[key.Difficulty];
            var region = PatternAt(part, track, from, length);
            matches = _found.Where(m => PatternAt(part, track, m, length).SameAs(region)).ToList();
        }
        else if (Vocal is { } vocal && _session.VocalContentOf(vocal) is { } content)
        {
            if (_session.Chart.Resolution != _resolution)
            {
                Cleared(ResolutionChanged);
                return;
            }

            var region = VocalPatternAt(content, from, length);
            matches = _found.Where(m => VocalPatternAt(content, m, length).SameAs(region)).ToList();
        }
        else
        {
            Cleared(TrackGone);
            return;
        }

        if (matches.SequenceEqual(_matches))
            return;
        int lost = _matches.Except(matches).Count();
        _matches.Clear();
        _matches.AddRange(matches);
        if (lost > 0)
            _session.Status = string.Format(CultureInfo.InvariantCulture, StoppedSyncing, lost);
        _session.RaiseState();
    }

    // Takes the region away, saying why.
    private void Cleared(string why)
    {
        Clear();
        _session.Status = why;
    }

    // What the region holds now copied into the passage at start: its notes, the chords' markings, and on Pro drums
    // whether each pad is a tom. Null when the passage holds it already.
    private IChartCommand? CopyTo(EditablePart part, ChartTrackKey key, Pattern region, long start, long length)
    {
        var track = part[key.Difficulty];
        if (PatternAt(part, track, start, length).SameAs(region))
            return null;

        var notes = region.Notes.Select(n => new EditableNote(start + n.Offset, n.Length, n.Lane, n.Dynamics, n.DoubleKick)).ToList();
        var flags = region.Flags.Select(f => f with { Tick = start + f.Tick }).ToList();
        var plan = NoteMerge.Plan(track, notes, flags, (start, start + length - 1));
        var commands = new List<IChartCommand>();
        if (!plan.IsEmpty || plan.Flags.Count > 0)
            commands.Add(new ReplaceNotes(key.Instrument, key.Difficulty, plan.Remove, plan.Add, plan.Flags));

        if (IsPro(part))
        {
            var toms = new List<(int Pad, long Tick)>();
            var cymbals = new List<(int Pad, long Tick)>();
            foreach (var note in region.Notes)
            {
                long tick = start + note.Offset;
                if (IsPad(note.Lane) && DrumCymbals.IsTom(part, note.Lane, tick) != note.IsTom)
                    (note.IsTom ? toms : cymbals).Add((note.Lane, tick));
            }

            if (toms.Count > 0)
                commands.Add(new SetToms(toms.Distinct(), tom: true, key.Difficulty));
            if (cymbals.Count > 0)
                commands.Add(new SetToms(cymbals.Distinct(), tom: false, key.Difficulty));
        }

        return commands.Count switch
        {
            0 => null,
            1 => commands[0],
            _ => new CompositeCommand(commands),
        };
    }

    // The passages of the track matching the region, earliest first: none overlapping the region or each other, each with
    // exactly the region's notes, markings and toms, and nothing else, from its start up to, not including, its end.
    private List<long> FindMatches(EditablePart part, EditableTrack track, long from, long length, Pattern region)
    {
        var first = region.Notes[0];
        var matches = new List<long>();
        long free = 0;
        foreach (var note in track.Notes)
        {
            long start = note.Tick - first.Offset;
            if (start < free || note.Lane != first.Lane || note.Length != first.Length || note.Dynamics != first.Dynamics || note.DoubleKick != first.DoubleKick)
                continue;
            if (start < from + length && start + length > from)
                continue;
            if (!PatternAt(part, track, start, length).SameAs(region))
                continue;
            matches.Add(start);
            free = start + length;
        }

        return matches;
    }

    // What the track holds from start up to, not including, start + length, by each note's offset from start.
    private Pattern PatternAt(EditablePart part, EditableTrack track, long start, long length)
    {
        bool pro = IsPro(part);
        var notes = new List<PatternNote>();
        foreach (var note in track.Notes.Between(start, start + length - 1))
        {
            bool tom = pro && IsPad(note.Lane) && DrumCymbals.IsTom(part, note.Lane, note.Tick);
            notes.Add(new PatternNote(note.Tick - start, note.Length, note.Lane, note.Dynamics, note.DoubleKick, tom));
        }

        var flags = new List<FlaggedChord>();
        foreach (long offset in notes.Select(static n => n.Offset).Distinct())
        {
            var marked = track.FlagsAt(start + offset);
            if (marked != ChordFlags.None)
                flags.Add(new FlaggedChord(offset, marked));
        }

        return new Pattern(notes, flags);
    }

    private bool IsPro(EditablePart part) => part.Kind == ChartInstrumentKind.Drums && _session.IsProDrums;

    private static bool IsPad(int lane) => lane is >= TomMarker.FirstPad and <= TomMarker.LastPad;

    // The vocal part's version of WithCopies: the region's notes and lyrics, as the command leaves them, put in each
    // passage synced in place of its own, as one more change of the part done with the command.
    private IChartCommand WithVocalCopies(VocalTrack vocal, IChartCommand command)
    {
        if (Span is not var (from, to) || _session.VocalContentOf(vocal) is not { } content)
            return command;
        var chart = _session.Chart;
        long length = to - from;
        var before = VocalPatternAt(content, from, length);
        var matched = _found.Where(m => VocalPatternAt(content, m, length).SameAs(before)).ToList();

        command.Apply(chart);
        IChartCommand? copy = null;
        try
        {
            // The command may have taken the part away, or left it unreadable, which ends the region once it's done.
            if (_session.VocalContentOf(vocal) is not { } after)
                return command;
            var region = VocalPatternAt(after, from, length);
            if (region.SameAs(before))
                return command;

            var starts = matched.Where(m => !VocalPatternAt(after, m, length).SameAs(region)).ToList();
            if (starts.Count == 0)
                return command;

            // A copy that's refused leaves the passages as they are, so they no longer match.
            try
            {
                var copied = VocalCopiesInto(after, region, starts, length);
                if (!ReferenceEquals(copied, after))
                {
                    copy = new ChangeVocals(vocal, copied);
                    copy.Apply(chart);
                }
            }
            catch (Exception e) when (EditorSession.IsRefusal(e))
            {
                copy = null;
            }

            return copy is null ? command : new CompositeCommand([command, copy]);
        }
        finally
        {
            // Taken back, to be done again as one step.
            copy?.Revert(chart);
            command.Revert(chart);
        }
    }

    // The passages of the vocal part matching what it holds from..to; none when it holds no notes there.
    private static List<long> VocalMatching(VocalTrackContent content, long from, long to)
    {
        long length = to - from;
        var region = VocalPatternAt(content, from, length);
        if (region.Notes.Count == 0)
            return [];

        // As a highway's: anchored on the region's first note, none overlapping the region or each other.
        var first = region.Notes[0];
        var matches = new List<long>();
        long free = 0;
        foreach (var note in content.Notes)
        {
            long start = note.Tick - first.Tick;
            if (start < free || note.Pitch != first.Pitch || note.Length != first.Length)
                continue;
            if (start < from + length && start + length > from)
                continue;
            if (!VocalPatternAt(content, start, length).SameAs(region))
                continue;
            matches.Add(start);
            free = start + length;
        }

        return matches;
    }

    // The part with each passage at starts holding the region's notes and lyrics, and only those, in place of its own.
    private static VocalTrackContent VocalCopiesInto(VocalTrackContent content, VocalPattern region, List<long> starts, long length)
    {
        bool InAPassage(long tick) => starts.Any(s => tick >= s && tick < s + length);
        var notes = content.Notes.Where(n => !InAPassage(n.Tick))
            .Concat(starts.SelectMany(s => region.Notes.Select(n => n with { Tick = s + n.Tick })));
        var lyrics = content.Lyrics.Where(l => !InAPassage(l.Tick))
            .Concat(starts.SelectMany(s => region.Lyrics.Select(l => l with { Tick = s + l.Tick })));
        return content.With(notes: notes, lyrics: lyrics);
    }

    // What the vocal part holds from start up to, not including, start + length: the notes starting there and the
    // lyrics on its ticks, each by its offset from start.
    private static VocalPattern VocalPatternAt(VocalTrackContent content, long start, long length)
    {
        long end = start + length;
        var notes = new List<VocalNoteEvent>();
        for (int i = FirstAtOrAfter(content.Notes, static n => n.Tick, start); i < content.Notes.Count && content.Notes[i].Tick < end; i++)
            notes.Add(content.Notes[i] with { Tick = content.Notes[i].Tick - start });
        var lyrics = new List<VocalLyricEvent>();
        for (int i = FirstAtOrAfter(content.Lyrics, static l => l.Tick, start); i < content.Lyrics.Count && content.Lyrics[i].Tick < end; i++)
            lyrics.Add(content.Lyrics[i] with { Tick = content.Lyrics[i].Tick - start });
        return new VocalPattern(notes, lyrics);
    }

    // The index of the first item of the list, in order of tickOf, at or after tick; the count when there's none.
    private static int FirstAtOrAfter<T>(IReadOnlyList<T> items, Func<T, long> tickOf, long tick)
    {
        int low = 0, high = items.Count;
        while (low < high)
        {
            int mid = (low + high) >>> 1;
            if (tickOf(items[mid]) < tick)
                low = mid + 1;
            else
                high = mid;
        }

        return low;
    }

    // A note of a passage, by its offset from the passage's start.
    private readonly record struct PatternNote(long Offset, long Length, int Lane, NoteDynamics Dynamics, bool DoubleKick, bool IsTom);

    // What a passage holds: its notes in the track's order, and its chords' markings, each by its offset.
    private sealed record Pattern(IReadOnlyList<PatternNote> Notes, IReadOnlyList<FlaggedChord> Flags)
    {
        public bool SameAs(Pattern other) => Notes.SequenceEqual(other.Notes) && Flags.SequenceEqual(other.Flags);
    }

    // What a passage of a vocal part holds: its notes and lyrics in the part's order, each ticked by its offset from the
    // passage's start. A note's velocity, which the editor doesn't set, is copied with it but not matched.
    private sealed record VocalPattern(IReadOnlyList<VocalNoteEvent> Notes, IReadOnlyList<VocalLyricEvent> Lyrics)
    {
        public bool SameAs(VocalPattern other) =>
            Notes.Count == other.Notes.Count
            && Notes.Zip(other.Notes).All(static p => p.First.Tick == p.Second.Tick && p.First.Length == p.Second.Length && p.First.Pitch == p.Second.Pitch)
            && Lyrics.SequenceEqual(other.Lyrics);
    }
}

/// <summary>
/// Which part of a synchronized region under the pointer is grabbed: an end, to resize it; its body, to move it; or a
/// passage synced with it, to make the region's source.
/// </summary>
public enum SyncGrip
{
    Start,
    End,
    Body,
    Match,
}

/// <summary>
/// What a press takes hold of on the synchronized region (<see cref="EditorTools.SyncHitOf(ChartPoint)"/>): which part,
/// and the tick that part begins at — a matched passage's first tick, or the region's own start or end.
/// </summary>
public readonly record struct SyncHit(SyncGrip Grip, long Start);
