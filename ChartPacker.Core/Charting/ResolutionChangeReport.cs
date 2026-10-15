using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting;

/// <summary>Where a resolution change would move items in time.</summary>
public enum ResolutionChangeGroup
{
    /// <summary>The tempo and time-signature markers, and the tempo track's other events.</summary>
    TempoMap,

    /// <summary>The text and other events on <c>EVENTS</c>.</summary>
    Events,

    /// <summary>One part.</summary>
    Part,

    /// <summary>The tracks Chart Builder doesn't edit.</summary>
    Passthrough,
}

/// <summary>How many items of one group a resolution change would move in time.</summary>
/// <param name="Part">The part, for <see cref="ResolutionChangeGroup.Part"/>; null otherwise.</param>
public sealed record ResolutionChangeMoves(ResolutionChangeGroup Group, ChartInstrument? Part, int Moved);

/// <summary>
/// What changing a chart's resolution would do, worked out on a copy before anything is changed (charter-spec C1,
/// Changing the resolution): the items whose time would move, as their tick doesn't scale to a whole tick, and the items
/// that would be left out, as another lands on their place. Either is warned about before the change is made.
/// </summary>
public sealed class ResolutionChangeReport
{
    /// <summary>How far an item may move and still be taken as kept in time: a microsecond.</summary>
    public const double ToleranceSeconds = 1e-6;

    private ResolutionChangeReport(int from, int to, IReadOnlyList<ResolutionChangeMoves> moves, double largestMoveMs, IReadOnlyList<string> leftOut)
    {
        From = from;
        To = to;
        Moves = moves;
        LargestMoveMs = largestMoveMs;
        LeftOut = leftOut;
    }

    /// <summary>The resolution the chart has.</summary>
    public int From { get; }

    /// <summary>The resolution it would have.</summary>
    public int To { get; }

    /// <summary>The groups with items that would move, with how many: the tempo map, each part in order, <c>EVENTS</c>, then the passthrough tracks.</summary>
    public IReadOnlyList<ResolutionChangeMoves> Moves { get; }

    /// <summary>How many items would move in all.</summary>
    public int Moved => Moves.Sum(static m => m.Moved);

    /// <summary>The furthest any item would move, in milliseconds; 0 when none would.</summary>
    public double LargestMoveMs { get; }

    /// <summary>The items that would be left out, or read differently by the games, in words.</summary>
    public IReadOnlyList<string> LeftOut { get; }

    /// <summary>Whether the change would move anything in time or leave anything out, so the user is warned first.</summary>
    public bool HasWarning => Moves.Count > 0 || LeftOut.Count > 0;

    /// <summary>What changing <paramref name="chart"/>'s resolution to <paramref name="resolution"/> would do. The chart isn't changed.</summary>
    /// <exception cref="ChartFormatException">A passthrough track can't be re-ticked, so the change can't be made.</exception>
    public static ResolutionChangeReport Analyze(EditableChart chart, int resolution)
    {
        ArgumentNullException.ThrowIfNull(chart);
        if (chart.Resolution == resolution)
            return new ResolutionChangeReport(resolution, resolution, [], 0, []);

        var copy = chart.Clone();
        var command = new ChangeResolution(resolution);
        command.Apply(copy);

        var counter = new MoveCounter(chart.TempoMap.ToTempoMap(), copy.TempoMap.ToTempoMap());
        var moves = new List<ResolutionChangeMoves>();
        void Group(ResolutionChangeGroup group, ChartInstrument? part)
        {
            int moved = counter.Take();
            if (moved > 0)
                moves.Add(new ResolutionChangeMoves(group, part, moved));
        }

        // The markers are scaled rather than re-ticked; each is compared at the tick it's scaled to.
        foreach (var tempo in chart.TempoMap.Tempos)
            counter.Scaled(tempo.Tick, chart.Resolution, resolution);
        foreach (var signature in chart.TempoMap.TimeSignatures)
            counter.Scaled(signature.Tick, chart.Resolution, resolution);
        counter.Each(chart.TempoTrackRaw, static e => e.Tick);
        Group(ResolutionChangeGroup.TempoMap, null);

        foreach (var part in chart.Parts)
        {
            foreach (var track in part.Tracks)
            {
                counter.Spans(track.Notes, static n => n.Tick, static n => n.Length);
                counter.Each(track.FlaggedChords, static c => c.Tick);
                counter.Each(track.Mixes, static m => m.Tick);
            }

            counter.Spans(part.Phrases, static p => p.Tick, static p => p.Length);
            counter.Spans(part.TomMarkers, static m => m.Tick, static m => m.Length);
            counter.Each(part.TextEvents, static e => e.Tick);
            counter.Each(part.RawEvents, static e => e.Tick);
            Group(ResolutionChangeGroup.Part, part.Instrument);
        }

        counter.Each(chart.Events, static e => e.Tick);
        counter.Each(chart.EventsRaw, static e => e.Tick);
        Group(ResolutionChangeGroup.Events, null);

        foreach (var track in chart.Passthrough)
            track.Retick(tick => counter.Mapped(tick), out _);
        Group(ResolutionChangeGroup.Passthrough, null);

        return new ResolutionChangeReport(chart.Resolution, resolution, moves, counter.LargestSeconds * 1000, command.LeftOut);
    }

    /// <summary>Counts the items that move more than <see cref="ToleranceSeconds"/>, and the furthest any moves.</summary>
    private sealed class MoveCounter(TempoMap from, TempoMap to)
    {
        private readonly Func<long, long> _map = TickMaps.KeepTimes(from, to);
        private int _moved;

        public double LargestSeconds { get; private set; }

        /// <summary>How many have moved since the last call, starting the count again.</summary>
        public int Take()
        {
            int moved = _moved;
            _moved = 0;
            return moved;
        }

        public void Scaled(long tick, int fromResolution, int toResolution)
        {
            if (Moves(tick, ResolutionScaling.Scale(tick, fromResolution, toResolution)))
                _moved++;
        }

        public void Each<T>(TickList<T> items, Func<T, long> tickOf)
        {
            foreach (var item in items)
            {
                long tick = tickOf(item);
                if (Moves(tick, _map(tick)))
                    _moved++;
            }
        }

        public void Spans<T>(TickList<T> items, Func<T, long> tickOf, Func<T, long> lengthOf)
        {
            foreach (var item in items)
            {
                long tick = tickOf(item), end = tick + lengthOf(item);
                // Both ends are measured, so the largest move is right, but the item counts once.
                bool start = Moves(tick, _map(tick));
                bool finish = end != tick && Moves(end, _map(end));
                if (start || finish)
                    _moved++;
            }
        }

        /// <summary>The tick <paramref name="tick"/> maps to, counting it when it moves, for a passthrough track's events.</summary>
        public long Mapped(long tick)
        {
            long mapped = _map(tick);
            if (Moves(tick, mapped))
                _moved++;
            return mapped;
        }

        private bool Moves(long tick, long mapped)
        {
            double move = Math.Abs(to.SecondsAt(mapped) - from.SecondsAt(tick));
            if (move <= ToleranceSeconds)
                return false;
            LargestSeconds = Math.Max(LargestSeconds, move);
            return true;
        }
    }
}
