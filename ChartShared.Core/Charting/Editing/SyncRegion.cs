using System.Globalization;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Editing;

/// <summary>
/// A synchronized region: a stretch of one track whose edits are made again in every passage of the track that matched
/// it exactly when it was drawn, as one undoable step with the edit (the Sync tool). There's one at a time, and the
/// session owns it (<see cref="EditorSession.Sync"/>).
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
    public bool IsActive => Track is not null;

    /// <summary>The track the region is drawn on; null with no region.</summary>
    public ChartTrackKey? Track { get; private set; }

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

    /// <summary>The region being dragged out with the Sync tool, from its start up to, not including, its end; null when none is.</summary>
    public (ChartTrackKey Track, long From, long To)? Draft
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
    public bool Define(ChartTrackKey track, long from, long to)
    {
        Clear();
        Track = track;
        if (Set(from, to))
            return true;
        Track = null;
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

    // Puts the region over from..to of its track and finds the passages that match it there again, keeping it however
    // few notes it holds; says what was found, unless say is off. False when there's no region there to redraw.
    private bool Set(long from, long to, bool say = true)
    {
        if (Track is not { } key || _session.Chart.PartOf(key.Instrument) is not { } part || to <= from || from < 0)
            return false;
        to = Math.Max(to, from + MinLength(_session.Chart.Resolution));
        var track = part[key.Difficulty];
        var region = PatternAt(part, track, from, to - from);
        var matches = region.Notes.Count == 0 ? [] : FindMatches(part, track, from, to - from, region);
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

    /// <summary>Takes the region away, syncing nothing more.</summary>
    public void Clear()
    {
        if (!IsActive)
            return;
        Track = null;
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
        if (Track is not { } key || Span is not var (from, to))
            return;
        if (_session.Chart.PartOf(key.Instrument) is not { } part)
        {
            Clear();
            _session.Status = TrackGone;
            return;
        }

        if (_session.Chart.Resolution != _resolution)
        {
            Clear();
            _session.Status = ResolutionChanged;
            return;
        }

        var track = part[key.Difficulty];
        long length = to - from;
        var region = PatternAt(part, track, from, length);
        var matches = _found.Where(m => PatternAt(part, track, m, length).SameAs(region)).ToList();
        if (matches.SequenceEqual(_matches))
            return;
        int lost = _matches.Except(matches).Count();
        _matches.Clear();
        _matches.AddRange(matches);
        if (lost > 0)
            _session.Status = string.Format(CultureInfo.InvariantCulture, StoppedSyncing, lost);
        _session.RaiseState();
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

    // A note of a passage, by its offset from the passage's start.
    private readonly record struct PatternNote(long Offset, long Length, int Lane, NoteDynamics Dynamics, bool DoubleKick, bool IsTom);

    // What a passage holds: its notes in the track's order, and its chords' markings, each by its offset.
    private sealed record Pattern(IReadOnlyList<PatternNote> Notes, IReadOnlyList<FlaggedChord> Flags)
    {
        public bool SameAs(Pattern other) => Notes.SequenceEqual(other.Notes) && Flags.SequenceEqual(other.Flags);
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
/// What a press takes hold of on the synchronized region (<see cref="EditorTools.SyncHitOf"/>): which part, and the tick
/// that part begins at — a matched passage's first tick, or the region's own start or end.
/// </summary>
public readonly record struct SyncHit(SyncGrip Grip, long Start);
