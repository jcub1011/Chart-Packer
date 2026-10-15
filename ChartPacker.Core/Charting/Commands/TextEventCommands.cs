using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting.Commands;

/// <summary>
/// Adds a text event to <c>EVENTS</c>, such as a section, or to a part's track. It goes after the events already at its
/// tick. A <c>[coda]</c> that becomes <c>EVENTS</c>' first makes the drum fills after it big rock endings
/// (<see cref="DrumFills"/>), in the same step.
/// </summary>
/// <param name="part">The part whose track it goes on, or null for <c>EVENTS</c>.</param>
public sealed class AddTextEvent(ChartInstrument? part, TextEvent textEvent) : ChartCommand
{
    private readonly EditLog _log = new();

    /// <summary>The part whose track it goes on, or null for <c>EVENTS</c>.</summary>
    public ChartInstrument? Part { get; } = part;

    public TextEvent Event { get; } = textEvent;

    private protected override ChartChange Do(EditableChart chart)
    {
        var events = TextEdits.ListOf(chart, Part);
        events.Check(Event);
        long? coda = Part is null ? DrumFills.FirstCoda(chart) : null;
        _log.Place(events, Event);
        return TextEdits.Affected(Part, Event).Merge(TextEdits.Reclassify(chart, Part, coda, _log));
    }

    private protected override void Undo(EditableChart chart) => _log.Undo();
}

/// <summary>
/// Removes a text event from <c>EVENTS</c> or a part's track: the first of its tick that's equal to it. Removing
/// <c>EVENTS</c>' first <c>[coda]</c> makes the big rock endings it decided drum fills again, in the same step.
/// </summary>
/// <param name="part">The part whose track it's on, or null for <c>EVENTS</c>.</param>
/// <param name="textEvent">The event, exactly as the track has it.</param>
public sealed class RemoveTextEvent(ChartInstrument? part, TextEvent textEvent) : ChartCommand
{
    private readonly EditLog _log = new();

    /// <summary>The part whose track it's on, or null for <c>EVENTS</c>.</summary>
    public ChartInstrument? Part { get; } = part;

    public TextEvent Event { get; } = textEvent;

    private protected override ChartChange Do(EditableChart chart)
    {
        var events = TextEdits.ListOf(chart, Part);
        int index = TextEdits.IndexOf(events, Part, Event);
        long? coda = Part is null ? DrumFills.FirstCoda(chart) : null;
        _log.RemoveAt(events, index);
        return TextEdits.Affected(Part, Event).Merge(TextEdits.Reclassify(chart, Part, coda, _log));
    }

    private protected override void Undo(EditableChart chart) => _log.Undo();
}

/// <summary>
/// Changes a text event of <c>EVENTS</c> or a part's track: its text, such as a section renamed, or its tick, or both.
/// Moved, it goes after the events already at its new tick. A change that moves <c>EVENTS</c>' first <c>[coda]</c> makes
/// the drum fills and big rock endings what it then decides, in the same step.
/// </summary>
/// <param name="part">The part whose track it's on, or null for <c>EVENTS</c>.</param>
/// <param name="from">The event, exactly as the track has it.</param>
/// <param name="to">What it becomes.</param>
public sealed class ChangeTextEvent(ChartInstrument? part, TextEvent from, TextEvent to) : ChartCommand
{
    private readonly EditLog _log = new();

    /// <summary>The part whose track it's on, or null for <c>EVENTS</c>.</summary>
    public ChartInstrument? Part { get; } = part;

    public TextEvent From { get; } = from;

    public TextEvent To { get; } = to;

    private protected override ChartChange Do(EditableChart chart)
    {
        var events = TextEdits.ListOf(chart, Part);
        int index = TextEdits.IndexOf(events, Part, From);
        events.Check(To);
        long? coda = Part is null ? DrumFills.FirstCoda(chart) : null;
        _log.RemoveAt(events, index);
        _log.Place(events, To);
        return TextEdits.Affected(Part, From)
            .Merge(TextEdits.Affected(Part, To))
            .Merge(TextEdits.Reclassify(chart, Part, coda, _log));
    }

    private protected override void Undo(EditableChart chart) => _log.Undo();
}

/// <summary>What the text event commands share.</summary>
internal static class TextEdits
{
    /// <summary>The text events of <paramref name="part"/>'s track, or of <c>EVENTS</c> when it's null.</summary>
    /// <exception cref="InvalidOperationException">The chart hasn't the part.</exception>
    public static TickList<TextEvent> ListOf(EditableChart chart, ChartInstrument? part)
    {
        if (part is not { } instrument)
            return chart.Events;
        return (chart.PartOf(instrument) ?? throw new InvalidOperationException($"The chart has no {instrument} part.")).TextEvents;
    }

    /// <summary>The index of the first event of <paramref name="events"/> equal to <paramref name="textEvent"/>.</summary>
    /// <exception cref="InvalidOperationException">There's none.</exception>
    public static int IndexOf(TickList<TextEvent> events, ChartInstrument? part, TextEvent textEvent)
    {
        int index = events.IndexOf(textEvent);
        if (index < 0)
        {
            string track = part is { } instrument ? $"{instrument}'s track" : "EVENTS";
            throw new InvalidOperationException(FormattableString.Invariant($"{track} has no text event \"{textEvent.Text}\" at tick {textEvent.Tick}."));
        }

        return index;
    }

    /// <summary>What a change to <paramref name="textEvent"/> touches: its tick of the part, or the song's events.</summary>
    public static ChartChange Affected(ChartInstrument? part, TextEvent textEvent) =>
        part is { } instrument ? ChartChange.Of(instrument, null, textEvent.Tick, textEvent.Tick) : ChartChange.Of(SongChanges.Events);

    /// <summary>
    /// After a change to <c>EVENTS</c> (when <paramref name="part"/> is null) that moved its first <c>[coda]</c> from
    /// <paramref name="coda"/>, makes the drums' fills and big rock endings what the games will read them as.
    /// </summary>
    public static ChartChange Reclassify(EditableChart chart, ChartInstrument? part, long? coda, EditLog log) =>
        part is null && DrumFills.FirstCoda(chart) != coda ? DrumFills.Reclassify(chart, log) : ChartChange.None;
}
