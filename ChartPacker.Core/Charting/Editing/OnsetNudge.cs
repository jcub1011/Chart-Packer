using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting.Editing;

/// <summary>A chord Nudge to onset moved: from its tick to the onset's, and how far that is in time.</summary>
/// <param name="Milliseconds">How far it moved in time: later is positive.</param>
public sealed record NudgeMove(long From, long To, double Milliseconds);

/// <summary>
/// What Nudge to onset will do to the notes picked (charter-spec C14 item 3): the chords it moves, as one undoable command,
/// and those it leaves.
/// </summary>
/// <param name="Command">The move, or <see langword="null"/> when nothing moves.</param>
/// <param name="Moved">Each chord moved, in time order.</param>
/// <param name="NoOnset">The ticks of chords with no onset within half a grid step, which stay.</param>
/// <param name="Blocked">The ticks of chords whose onset another chord nearer to it took, or a note that isn't moving is on.</param>
/// <param name="Notes">The notes picked, where they'll be once it's done.</param>
public sealed record NudgePlan(IChartCommand? Command, IReadOnlyList<NudgeMove> Moved, IReadOnlyList<long> NoOnset, IReadOnlyList<long> Blocked, IReadOnlyList<EditableNote> Notes)
{
    /// <summary>How many chords stayed: with no onset near, or blocked.</summary>
    public int Stayed => NoOnset.Count + Blocked.Count;
}

/// <summary>
/// Nudge to onset (charter-spec C14 item 3): moves notes to their nearest onsets, chord by chord, each by its own amount.
/// A chord with no onset within half a grid step stays where it is.
/// </summary>
public static class OnsetNudge
{
    /// <summary>What nudging <paramref name="notes"/> of <paramref name="key"/>'s track to <paramref name="onsets"/> would do.</summary>
    /// <param name="onsets">Audio seconds, ascending.</param>
    public static NudgePlan Plan(EditorSession session, ChartTrackKey key, IReadOnlyList<EditableNote> notes, IReadOnlyList<double> onsets)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(onsets);
        if (session.TrackOf(key) is not { } track || notes.Count == 0)
            return new NudgePlan(null, [], [], [], notes);

        var map = session.TempoMap;
        double delay = session.Delay.TotalSeconds;
        var noOnset = new List<long>();
        var blocked = new List<long>();

        // Each chord's onset, if one is near.
        var targets = new Dictionary<long, long>();
        foreach (long tick in notes.Select(static n => n.Tick).Distinct().Order())
        {
            if (OnsetSnap.Nearest(tick, onsets, map, delay, session.Snap) is not { } to)
                noOnset.Add(tick);
            else if (to != tick)
                targets[tick] = to;
        }

        // Two chords after one onset: the nearer has it.
        foreach (var contest in targets.GroupBy(static t => t.Value).Where(static g => g.Count() > 1).ToList())
        {
            long winner = contest.MinBy(t => Math.Abs(t.Key - t.Value)).Key;
            foreach (var (tick, _) in contest.Where(t => t.Key != winner))
            {
                targets.Remove(tick);
                blocked.Add(tick);
            }
        }

        // A chord can't land on a note that isn't moving with it, nor make an open note and frets one chord. Blocking one
        // leaves its notes where they are, which may block another, so it's checked until nothing more is.
        bool changed = true;
        while (changed)
        {
            changed = false;
            var moving = notes.Where(n => targets.ContainsKey(n.Tick)).ToHashSet();
            foreach (var (tick, to) in targets.ToList())
            {
                var chord = notes.Where(n => n.Tick == tick).ToList();
                var landed = chord.Select(n => n with { Tick = to }).ToList();
                bool lands = landed.Any(n => track.Notes.IndexOfPlace(n) is int at && at >= 0 && !moving.Contains(track.Notes[at]))
                    || EditorTools.PlacementRefusal(track, landed, moving) is not null;
                if (lands)
                {
                    targets.Remove(tick);
                    blocked.Add(tick);
                    changed = true;
                    break;
                }
            }
        }

        if (targets.Count == 0)
            return new NudgePlan(null, [], noOnset, [.. blocked.Order()], notes);

        var moves = notes.Where(n => targets.ContainsKey(n.Tick)).Select(n => (n, targets[n.Tick] - n.Tick)).ToList();
        var moved = targets.OrderBy(static t => t.Key)
            .Select(t => new NudgeMove(t.Key, t.Value, (session.AudioAt(t.Value) - session.AudioAt(t.Key)) * 1000))
            .ToList();
        var after = notes.Select(n => targets.TryGetValue(n.Tick, out long to) ? n with { Tick = to } : n).ToList();
        return new NudgePlan(new MoveNotes(key.Instrument, key.Difficulty, moves), moved, noOnset, [.. blocked.Order()], after);
    }
}
