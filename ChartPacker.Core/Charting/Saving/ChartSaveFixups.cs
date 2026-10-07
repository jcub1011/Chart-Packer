using ChartPacker.Core.Charting.Commands;

namespace ChartPacker.Core.Charting.Saving;

/// <summary>What a save puts right in the chart before it's written, each as an undoable step.</summary>
public static class ChartSaveFixups
{
    /// <summary>The text of the song's end event.</summary>
    public const string EndText = "[end]";

    /// <summary>
    /// The command that moves <c>[end]</c> to after the last note (charter-spec C8 item 5, CHR085), or adds it there when
    /// the chart has none; null when it's already at or after the last note's end, or the chart has no notes.
    /// </summary>
    /// <remarks>
    /// It goes to the first measure line after the last note's end, a sustain's included. It's a command, not a change
    /// made to what's written, so the model the editor holds is the one saved (AC C17.4).
    /// </remarks>
    public static IChartCommand? MoveEnd(EditableChart chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        if (LastNoteEnd(chart) is not { } last)
            return null;

        TextEvent? end = null;
        foreach (var e in chart.Events.AsSpan())
        {
            if (e.Text == EndText && (end is null || e.Tick > end.Value.Tick))
                end = e;
        }

        if (end is { } existing && existing.Tick >= last)
            return null;

        long target = EndTickAfter(chart, last);
        var add = new AddTextEvent(null, end is { } old ? old with { Tick = target } : TextEvent.Of(target, EndText));
        return end is { } moved ? new CompositeCommand([new RemoveTextEvent(null, moved), add]) : add;
    }

    /// <summary>
    /// Where the last note in any part ends: a sustain's end, or a plain note's tick. Null when the chart has no notes.
    /// </summary>
    public static long? LastNoteEnd(EditableChart chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        long? last = null;
        foreach (var part in chart.Parts)
        {
            foreach (var track in part.Tracks)
            {
                foreach (var note in track.Notes.AsSpan())
                {
                    long noteEnd = note.Tick + (note.Length > EditableNote.PlainLength ? note.Length : 0);
                    if (last is null || noteEnd > last)
                        last = noteEnd;
                }
            }
        }

        return last;
    }

    // The first measure line after the tick; or a measure of 4/4 on, past the tempo map's lines.
    private static long EndTickAfter(EditableChart chart, long tick)
    {
        var map = chart.TempoMap.ToTempoMap();
        long horizon = tick + (8L * chart.Resolution * 64);
        foreach (var line in map.BeatLines(tick + 1, horizon))
        {
            if (line.IsMeasure)
                return line.Tick;
        }

        return tick + (4L * chart.Resolution);
    }
}
