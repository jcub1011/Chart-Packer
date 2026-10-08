using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting.Editing;

/// <summary>What a row of the Sections tab is (charter-spec C8 items 3, 5 and 6).</summary>
public enum EventRowKind
{
    /// <summary>A section of <c>EVENTS</c>, shown by its name.</summary>
    Section,

    /// <summary>A song event of <c>EVENTS</c> (<see cref="SongEvents"/>), shown by what it is.</summary>
    SongEvent,

    /// <summary>Any other text event, of <c>EVENTS</c> or a part's track, shown as it's written.</summary>
    Text,
}

/// <summary>A row of the Sections tab: a text event, the track it's on, and what it is.</summary>
/// <param name="Part">The part whose track it's on, or null for <c>EVENTS</c>.</param>
/// <param name="Event">The event, exactly as the track has it.</param>
/// <param name="SectionName">A section's name; null for any other event.</param>
/// <param name="SongEvent">A song event's kind; null for any other event.</param>
public readonly record struct EventRow(ChartInstrument? Part, TextEvent Event, string? SectionName = null, SongEvent? SongEvent = null)
{
    public EventRowKind Kind => SectionName is not null ? EventRowKind.Section : SongEvent is not null ? EventRowKind.SongEvent : EventRowKind.Text;
}

/// <summary>
/// The rows of the Sections tab (charter-spec C8 items 3, 5 and 6): every text event of <c>EVENTS</c>, sections, song events
/// and free text alike, and below them the focused part's own text events, which are all free text. Events the model
/// doesn't know are listed as they're written, so none is out of sight.
/// </summary>
public static class EventRows
{
    /// <summary>
    /// The text events of <paramref name="part"/>'s track, or of <c>EVENTS</c> when it's null, in the track's order. A part
    /// the chart hasn't has none.
    /// </summary>
    public static IReadOnlyList<EventRow> Of(EditableChart chart, ChartInstrument? part)
    {
        ArgumentNullException.ThrowIfNull(chart);
        if (part is { } instrument)
        {
            if (chart.PartOf(instrument) is not { } owner)
                return [];
            var texts = new List<EventRow>(owner.TextEvents.Count);
            foreach (var e in owner.TextEvents)
                texts.Add(new EventRow(instrument, e));
            return texts;
        }

        var rows = new List<EventRow>(chart.Events.Count);
        foreach (var e in chart.Events)
        {
            if (SectionEvents.NameOf(e.Text) is { } name)
                rows.Add(new EventRow(null, e, SectionName: name));
            else if (SongEvents.KindOf(e.Text) is { } kind)
                rows.Add(new EventRow(null, e, SongEvent: kind));
            else
                rows.Add(new EventRow(null, e));
        }

        return rows;
    }
}
