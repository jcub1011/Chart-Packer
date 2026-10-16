using ChartShared.Core.Input;

namespace ChartShared.Core.Charting.Playtest;

/// <summary>
/// A performance kept as its timed actions (charter-spec C19 item 8). The engines are deterministic, so judging the actions
/// again gives what was judged live, or, after the chart changes, what the same playing would give now. Saving one and a
/// view for replaying it are later work; this is where they start.
/// </summary>
public sealed class PerformanceLog : IActionSink
{
    private readonly List<TimedAction> _actions = [];

    /// <summary>The actions, in the order they were handled.</summary>
    public IReadOnlyList<TimedAction> Actions => _actions;

    /// <inheritdoc/>
    public void Handle(in TimedAction action) => _actions.Add(action);

    public void Clear() => _actions.Clear();

    /// <summary>Judges the actions again on <paramref name="engine"/>, from <paramref name="start"/> to <paramref name="end"/> in the song.</summary>
    /// <returns>What it judged, in order.</returns>
    public List<JudgeEvent> Replay(IPlaytestEngine engine, double start, double end, double speed)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var output = new List<JudgeEvent>();
        engine.Reset(start, speed, static _ => false);
        foreach (var action in _actions)
            engine.Input(action, output);
        engine.Advance(end, speed, output);
        return output;
    }
}
