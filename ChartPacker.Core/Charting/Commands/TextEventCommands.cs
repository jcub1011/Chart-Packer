using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting.Commands;

/// <summary>
/// Adds a text event to <c>EVENTS</c>, such as a section, or to a part's track. It goes after the events already at its
/// tick.
/// </summary>
public sealed class AddTextEvent : ChartCommand
{
    private int _index = -1;

    /// <param name="part">The part whose track it goes on, or null for <c>EVENTS</c>.</param>
    public AddTextEvent(ChartInstrument? part, TextEvent textEvent)
    {
        Part = part;
        Event = textEvent;
    }

    /// <summary>The part whose track it goes on, or null for <c>EVENTS</c>.</summary>
    public ChartInstrument? Part { get; }

    public TextEvent Event { get; }

    private protected override ChartChange Do(EditableChart chart)
    {
        var events = TextEdits.ListOf(chart, Part);
        events.Check(Event);
        _index = events.Place(Event);
        return TextEdits.Affected(Part, Event);
    }

    private protected override void Undo(EditableChart chart) => TextEdits.ListOf(chart, Part).RemoveAt(_index);
}

/// <summary>Removes a text event from <c>EVENTS</c> or a part's track: the first of its tick that's equal to it.</summary>
public sealed class RemoveTextEvent : ChartCommand
{
    private int _index = -1;

    /// <param name="part">The part whose track it's on, or null for <c>EVENTS</c>.</param>
    /// <param name="textEvent">The event, exactly as the track has it.</param>
    public RemoveTextEvent(ChartInstrument? part, TextEvent textEvent)
    {
        Part = part;
        Event = textEvent;
    }

    /// <summary>The part whose track it's on, or null for <c>EVENTS</c>.</summary>
    public ChartInstrument? Part { get; }

    public TextEvent Event { get; }

    private protected override ChartChange Do(EditableChart chart)
    {
        var events = TextEdits.ListOf(chart, Part);
        int index = events.IndexOf(Event);
        if (index < 0)
        {
            string track = Part is { } part ? $"{part}'s track" : "EVENTS";
            throw new InvalidOperationException(FormattableString.Invariant($"{track} has no text event \"{Event.Text}\" at tick {Event.Tick}."));
        }

        events.RemoveAt(index);
        _index = index;
        return TextEdits.Affected(Part, Event);
    }

    private protected override void Undo(EditableChart chart) => TextEdits.ListOf(chart, Part).Insert(_index, Event);
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

    /// <summary>What a change to <paramref name="textEvent"/> touches: its tick of the part, or the song's events.</summary>
    public static ChartChange Affected(ChartInstrument? part, TextEvent textEvent) =>
        part is { } instrument ? ChartChange.Of(instrument, null, textEvent.Tick, textEvent.Tick) : ChartChange.Of(SongChanges.Events);
}
