using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charts;
using Serilog;

namespace ChartShared.Core.Charting;

/// <summary>An item that re-ticking moved onto another's place in a list that holds one per place, and so left out.</summary>
/// <param name="Kind">What it is: <see cref="ModelDifferenceKind.Note"/> or <see cref="ModelDifferenceKind.ChordFlags"/>.</param>
/// <param name="Part">The part it's in.</param>
/// <param name="Difficulty">The difficulty it's in.</param>
/// <param name="FromTick">Its tick before re-ticking.</param>
/// <param name="ToTick">The tick it moved to, where the item kept already was.</param>
/// <param name="Description">What was left out, and where, in words.</param>
public sealed record RetickCollision(ModelDifferenceKind Kind, ChartInstrument Part, ChartDifficulty Difficulty, long FromTick, long ToTick, string Description);

/// <summary>What re-ticking a chart did (charter-spec C4 item 3).</summary>
/// <param name="Moved">How many of the model's items changed tick or length.</param>
/// <param name="Collisions">The items left out because another was already at their new place, in the order met.</param>
/// <param name="PassthroughTracks">How many passthrough tracks were rewritten, an event of each having moved.</param>
/// <param name="PassthroughEvents">How many events of the passthrough tracks moved.</param>
public sealed record RetickReport(int Moved, IReadOnlyList<RetickCollision> Collisions, int PassthroughTracks, int PassthroughEvents);

/// <summary>
/// The lists and passthrough tracks of a chart as they were before <see cref="ChartRetick.Apply"/> changed them, to put
/// back exactly: re-ticking rounds, so re-ticking back wouldn't undo it.
/// </summary>
/// <remarks>
/// It holds a copy of only the lists that changed, and the passthrough tracks replaced, which are shared rather than
/// copied. It puts them back into the chart they came from, which must be as re-ticking left it (as undoing in order
/// ensures).
/// </remarks>
internal sealed class ChartTickSnapshot
{
    private readonly List<Action> _restores = [];

    /// <summary>Whether re-ticking changed nothing.</summary>
    public bool IsEmpty => _restores.Count == 0;

    /// <summary>Puts every list and passthrough track kept back as it was, the lists' items in their order.</summary>
    public void Restore()
    {
        // Backwards, so a passthrough track replaced twice ends as it first was.
        for (int i = _restores.Count - 1; i >= 0; i--)
            _restores[i]();
    }

    /// <summary>Keeps <paramref name="items"/>, the items <paramref name="list"/> has before it changes.</summary>
    internal void Keep<T>(TickList<T> list, T[] items) => _restores.Add(() => Refill(list, items));

    /// <summary>Keeps <paramref name="track"/>, the passthrough track at <paramref name="index"/> before it's replaced.</summary>
    internal void Keep(EditableChart chart, int index, PassthroughTrack track) => _restores.Add(() => chart.SetPassthrough(index, track));

    /// <summary>Empties <paramref name="list"/> and adds <paramref name="items"/>, in their order.</summary>
    internal static void Refill<T>(TickList<T> list, T[] items)
    {
        list.Clear();
        foreach (var item in items)
            list.Add(item);
    }
}

/// <summary>
/// Moves everything in a chart to the ticks a tempo change that keeps times gives it (charter-spec §4.2, C4 item 3):
/// every note's start and sustain end, chord marking, drum mix, phrase, tom marker, text and raw event (on the parts,
/// <c>EVENTS</c> and the tempo track), and the passthrough tracks' events.
/// </summary>
/// <remarks>
/// <para>The tempo map isn't touched: its markers are what changed, and the caller puts them where it wants them before
/// re-ticking with <see cref="TickMaps.KeepTimes"/> from the old map to the new. An anchor belongs to its tempo marker
/// and moves with that marker, so it's the caller's to place too. The tempo track's raw events (key signatures, markers,
/// text) are the chart's own and are re-ticked like any other event.</para>
/// <para>A note's start moves to the start's tick and its end to the end's; its length is what's between them. It's
/// never negative, as the map never decreases, and a note held for some ticks is held for none only when its start and
/// end land on one tick, which the new map makes less than a tick apart. Phrases and tom markers move the same way.</para>
/// <para>Items that land on one place in a list that holds one per place (two notes on one tick and lane, two chord
/// markings on one tick) keep the first in the list's order; each other is left out and reported, never lost unseen.
/// Items that land on one tick in other lists are all kept, in the order they were.</para>
/// <para>A drum fill moved onto or after <c>[coda]</c>'s tick becomes the big rock ending the games will read it as
/// (<see cref="DrumFills"/>).</para>
/// <para>Keeping ticks, the default, never re-ticks.</para>
/// </remarks>
internal static class ChartRetick
{
    private static ILogger Logger => Log.ForContext(typeof(ChartRetick));

    /// <summary>
    /// Re-ticks <paramref name="chart"/> in place by <paramref name="map"/>, all or nothing: when anything fails, the chart
    /// is put back as it was.
    /// </summary>
    /// <param name="map">The tick each tick moves to, as <see cref="TickMaps.KeepTimes"/> gives it: never decreasing, never negative.</param>
    /// <returns>
    /// What it did, and what it changed as it was before, for undoing exactly: <see cref="ChartTickSnapshot.Restore"/>
    /// puts it back (with the caller putting back the tempo map).
    /// </returns>
    /// <exception cref="ChartFormatException">A passthrough track can't be re-ticked (<see cref="PassthroughTrack.Retick"/>).</exception>
    internal static (RetickReport Report, ChartTickSnapshot Previous) Apply(EditableChart chart, Func<long, long> map)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(map);

        // The passthrough tracks first: the only step that can fail on a loaded chart, and it changes nothing until done.
        var tracks = new List<(int Index, PassthroughTrack Track, int Events)>();
        for (int i = 0; i < chart.Passthrough.Count; i++)
        {
            var track = chart.Passthrough[i].Retick(map, out int events);
            if (!ReferenceEquals(track, chart.Passthrough[i]))
                tracks.Add((i, track, events));
        }

        var previous = new ChartTickSnapshot();
        var collisions = new List<RetickCollision>();
        int moved = 0;
        try
        {
            moved += Move(chart.TempoTrackRaw, e => e.At(map(e.Tick)), previous);
            moved += Move(chart.Events, e => e with { Tick = map(e.Tick) }, previous);
            moved += Move(chart.EventsRaw, e => e.At(map(e.Tick)), previous);
            foreach (var part in chart.Parts)
                moved += MovePart(part, map, previous, collisions);

            // Rounding can move a drum fill onto [coda]'s tick, which makes it a big rock ending.
            if (chart.PartOf(ChartInstrument.Drums) is { } drums)
            {
                var phrases = drums.Phrases.AsSpan().ToArray();
                if (!DrumFills.Reclassify(chart, new EditLog()).IsEmpty)
                    previous.Keep(drums.Phrases, phrases);
            }

            foreach (var (index, track, _) in tracks)
                previous.Keep(chart, index, chart.SetPassthrough(index, track));
        }
        catch
        {
            previous.Restore();
            throw;
        }

        var report = new RetickReport(moved, collisions, tracks.Count, tracks.Sum(t => t.Events));
        LogReport(chart, report);
        return (report, previous);
    }

    private static int MovePart(EditablePart part, Func<long, long> map, ChartTickSnapshot previous, List<RetickCollision> collisions)
    {
        int moved = 0;
        foreach (var track in part.Tracks)
        {
            moved += Move(track.Notes, n => MoveSpan(n, n.Tick, n.Length, map, static (x, tick, length) => x with { Tick = tick, Length = length }), previous, (from, to) =>
            {
                string lane = to.Lane == ChartNote.OpenLane ? "open" : Invariant($"lane {to.Lane}");
                collisions.Add(new RetickCollision(ModelDifferenceKind.Note, part.Instrument, track.Difficulty, from.Tick, to.Tick, Invariant(
                    $"{part.Instrument} {track.Difficulty}: the {lane}{(to.DoubleKick ? " 2x kick" : "")} note at tick {from.Tick} moved to tick {to.Tick}, where another already is, so it was left out.")));
            });
            moved += Move(track.FlaggedChords, c => c with { Tick = map(c.Tick) }, previous, (from, to) =>
                collisions.Add(new RetickCollision(ModelDifferenceKind.ChordFlags, part.Instrument, track.Difficulty, from.Tick, to.Tick, Invariant(
                    $"{part.Instrument} {track.Difficulty}: the chord marking ({from.Flags}) at tick {from.Tick} moved to tick {to.Tick}, which is already marked, so it was left out."))));
            moved += Move(track.Mixes, m => m with { Tick = map(m.Tick) }, previous);
        }

        moved += Move(part.Phrases, p => MoveSpan(p, p.Tick, p.Length, map, static (x, tick, length) => x with { Tick = tick, Length = length }), previous);
        moved += Move(part.TomMarkers, m => MoveSpan(m, m.Tick, m.Length, map, static (x, tick, length) => x with { Tick = tick, Length = length }), previous);
        moved += Move(part.TextEvents, e => e with { Tick = map(e.Tick) }, previous);
        moved += Move(part.RawEvents, e => e.At(map(e.Tick)), previous);
        return moved;
    }

    /// <summary>An item from <paramref name="tick"/> for <paramref name="length"/>, its start and end each moved by <paramref name="map"/>.</summary>
    private static T MoveSpan<T>(T item, long tick, long length, Func<long, long> map, Func<T, long, long, T> with)
    {
        long start = map(tick);
        return with(item, start, Math.Max(0, map(tick + length) - start));
    }

    /// <summary>
    /// Moves each item of <paramref name="list"/> by <paramref name="move"/>, keeping the list as it was in
    /// <paramref name="previous"/> when any item changes. An item the list refuses, another being at its place, is left
    /// out and passed to <paramref name="refused"/>, as it was and as it would have been.
    /// </summary>
    /// <returns>How many items changed.</returns>
    private static int Move<T>(TickList<T> list, Func<T, T> move, ChartTickSnapshot previous, Action<T, T>? refused = null)
    {
        if (list.Count == 0)
            return 0;

        var items = list.AsSpan().ToArray();
        var moved = new T[items.Length];
        var comparer = EqualityComparer<T>.Default;
        int changed = 0;
        for (int i = 0; i < items.Length; i++)
        {
            moved[i] = move(items[i]);
            if (!comparer.Equals(moved[i], items[i]))
                changed++;
        }

        if (changed == 0)
            return 0;

        previous.Keep(list, items);
        list.Clear();
        for (int i = 0; i < moved.Length; i++)
        {
            if (!list.Add(moved[i]))
                refused?.Invoke(items[i], moved[i]);
        }

        return changed;
    }

    private static void LogReport(EditableChart chart, RetickReport report)
    {
        foreach (var c in report.Collisions)
            Logger.Debug("Re-ticking left out a {Kind} of {Part} {Difficulty} moved from tick {From} to {To}", c.Kind, c.Part, c.Difficulty, c.FromTick, c.ToTick);

        if (report.PassthroughTracks > 0)
        {
            Logger.Information(
                "Re-ticked the chart {Name} to keep times: {Moved} items moved, {Collisions} left out, and {Events} events of {Tracks} passthrough tracks moved",
                chart.Name, report.Moved, report.Collisions.Count, report.PassthroughEvents, report.PassthroughTracks);
        }
        else
        {
            Logger.Debug(
                "Re-ticked the chart {Name} to keep times: {Moved} items moved, {Collisions} left out, no passthrough track changed",
                chart.Name, report.Moved, report.Collisions.Count);
        }
    }

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);
}
