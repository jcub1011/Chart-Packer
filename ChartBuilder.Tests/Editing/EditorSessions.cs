using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charting.Editing;
using ChartPacker.Core.Charts;

namespace ChartBuilder.Tests.Editing;

/// <summary>Editor sessions over test charts, and points in their lanes.</summary>
internal static class EditorSessions
{
    public static readonly ChartTrackKey GuitarExpert = new(ChartInstrument.Guitar, ChartDifficulty.Expert);

    public static readonly ChartTrackKey DrumsExpert = new(ChartInstrument.Drums, ChartDifficulty.Expert);

    public static readonly ChartTrackKey GhlExpert = new(ChartInstrument.GhlGuitar, ChartDifficulty.Expert);

    /// <summary>An empty drums chart of <paramref name="variant"/> at 480 ticks per beat, 120 BPM in 4/4.</summary>
    public static EditableChart Drums(DrumsVariant variant = DrumsVariant.Pro) =>
        EditableChart.CreateNew(new NewChartSettings("Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.Drums }, variant));

    /// <summary>An empty GHL guitar chart at 480 ticks per beat, 120 BPM in 4/4.</summary>
    public static EditableChart Ghl() =>
        EditableChart.CreateNew(new NewChartSettings("Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.GhlGuitar }, DrumsVariant.Pro));

    /// <summary>An empty Lead guitar chart at 480 ticks per beat, 120 BPM in 4/4.</summary>
    public static EditableChart Guitar() =>
        EditableChart.CreateNew(new NewChartSettings("Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.Guitar }, DrumsVariant.Pro));

    public static EditorSession Of(EditableChart chart, TimeSpan delay = default, IReadOnlyList<KeyValuePair<string, string>>? songIni = null, ChartTrackKey? focused = null)
    {
        var session = new EditorSession(new ChartUndoStack(chart), delay, songIni ?? []);
        session.Focused = focused ?? GuitarExpert;
        return session;
    }

    /// <summary>The point at <paramref name="tick"/> in <paramref name="lane"/> of <paramref name="key"/>'s lane, in <paramref name="view"/>.</summary>
    public static ChartPoint At(EditorSession session, double tick, int? lane, LaneView view = LaneView.Highway, ChartTrackKey? key = null, double tolerance = 10)
    {
        var track = key ?? GuitarExpert;
        return new ChartPoint(track, tick, lane, tolerance, session.SlotsOf(track.Instrument, view));
    }

    /// <summary>The point at <paramref name="tick"/> in the phrase rail's row for <paramref name="row"/> beside <paramref name="key"/>'s lane, in <paramref name="view"/>.</summary>
    public static ChartPoint RailAt(EditorSession session, double tick, RailKind row, LaneView view = LaneView.Highway, ChartTrackKey? key = null, double tolerance = 10) =>
        At(session, tick, null, view, key, tolerance) with { Rail = row };

    /// <summary>Clicks at a point: a press and a release in one place.</summary>
    public static void Click(EditorSession session, ChartPoint point, EditorModifiers modifiers = EditorModifiers.None)
    {
        session.Tools.Press(point, modifiers);
        session.Tools.Release(point);
    }

    /// <summary>Drags from one point to another through a point between them.</summary>
    public static void DragFromTo(EditorSession session, ChartPoint start, ChartPoint end, EditorModifiers modifiers = EditorModifiers.None)
    {
        session.Tools.Press(start, modifiers);
        session.Tools.Drag(start with { Tick = (start.Tick + end.Tick) / 2 });
        session.Tools.Drag(end);
        session.Tools.Release(end);
    }

    public static EditableTrack Expert(EditorSession session) => session.Chart.PartOf(ChartInstrument.Guitar)![ChartDifficulty.Expert];
}
