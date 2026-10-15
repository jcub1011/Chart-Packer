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
/// <param name="IsPlaced">
/// Whether it's a song event the editor places itself (<see cref="SongEventPlacement"/>), which isn't in the model and can't
/// be moved, changed or deleted.
/// </param>
public readonly record struct EventRow(ChartInstrument? Part, TextEvent Event, string? SectionName = null, SongEvent? SongEvent = null, bool IsPlaced = false)
{
    public EventRowKind Kind => SectionName is not null ? EventRowKind.Section : SongEvent is not null ? EventRowKind.SongEvent : EventRowKind.Text;
}

/// <summary>
/// The rows of the Sections tab (charter-spec C8 items 3, 5 and 6): every text event of <c>EVENTS</c>, sections, song events
/// and free text alike, and below them the focused part's own text events, which are all free text. Events the model
/// doesn't know are listed as they're written, so none is out of sight. The song events the editor places are listed among
/// <c>EVENTS</c>' at their ticks.
/// </summary>
public static class EventRows
{
    /// <summary>
    /// The text events of <paramref name="part"/>'s track, or of <c>EVENTS</c> when it's null, in the track's order. A part
    /// the chart hasn't has none.
    /// </summary>
    /// <param name="marks">
    /// Where the placed song events are, listed in <c>EVENTS</c> after its events at their ticks, in place of any the model
    /// has; null to list only the model's.
    /// </param>
    public static IReadOnlyList<EventRow> Of(EditableChart chart, ChartInstrument? part, SongEventMarks? marks = null)
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

        // In tick order, and in the order they're written at one tick.
        List<EventRow> placed = marks is null ? [] :
            [.. SongEventPlacement.Placed
                .Select(kind => new EventRow(null, TextEvent.Of(marks.TickOf(kind), SongEvents.TextOf(kind)), SongEvent: kind, IsPlaced: true))
                .OrderBy(static row => row.Event.Tick)];

        var rows = new List<EventRow>(chart.Events.Count + placed.Count);
        int next = 0;
        foreach (var e in chart.Events)
        {
            while (next < placed.Count && placed[next].Event.Tick < e.Tick)
                rows.Add(placed[next++]);
            if (marks is not null && SongEventPlacement.IsPlaced(e))
                continue;
            if (SectionEvents.NameOf(e.Text) is { } name)
                rows.Add(new EventRow(null, e, SectionName: name));
            else if (SongEvents.KindOf(e.Text) is { } kind)
                rows.Add(new EventRow(null, e, SongEvent: kind));
            else
                rows.Add(new EventRow(null, e));
        }

        rows.AddRange(placed.Skip(next));
        return rows;
    }
}
