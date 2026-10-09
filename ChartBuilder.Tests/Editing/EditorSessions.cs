using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charting.Editing;
using ChartPacker.Core.Charting.Layout;
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

    /// <summary>
    /// An empty Lead guitar chart, as <see cref="Guitar"/>, with the vocal part <paramref name="track"/> holding
    /// <paramref name="notes"/>, <paramref name="lyrics"/> and <paramref name="phrases"/>.
    /// </summary>
    public static EditableChart WithVocals(
        VocalNoteEvent[] notes, VocalLyricEvent[]? lyrics = null, VocalPhraseEvent[]? phrases = null, VocalTrack track = VocalTrack.Vocals, EditableChart? chart = null)
    {
        chart ??= Guitar();
        new AddVocalPart(track).Apply(chart);
        var content = VocalTrackContent.Of(chart.Passthrough[VocalTracks.IndexIn(chart, track)]);
        var edited = content.With(notes, lyrics ?? [], phrases ?? []);
        if (!ReferenceEquals(edited, content))
            new ChangeVocals(track, edited).Apply(chart);
        return chart;
    }

    /// <summary>The point at <paramref name="tick"/> on <paramref name="pitch"/>'s row of <paramref name="track"/>'s lane.</summary>
    public static VocalPoint VocalAt(double tick, int? pitch, VocalTrack track = VocalTrack.Vocals, double tolerance = 10) =>
        new(track, tick, pitch, tolerance);

    /// <summary>The point at <paramref name="tick"/> in the phrase strip of <paramref name="track"/>'s lane.</summary>
    public static VocalPoint PhraseStripAt(double tick, VocalTrack track = VocalTrack.Vocals, double tolerance = 10) =>
        new(track, tick, null, tolerance, InPhrases: true);

    /// <summary>Clicks at a point in a vocal part's lane.</summary>
    public static void Click(EditorSession session, VocalPoint point, EditorModifiers modifiers = EditorModifiers.None)
    {
        session.Tools.Press(point, modifiers);
        session.Tools.Release(point);
    }

    /// <summary>Drags from one point in a vocal part's lane to another through a point between them.</summary>
    public static void DragFromTo(EditorSession session, VocalPoint start, VocalPoint end, EditorModifiers modifiers = EditorModifiers.None)
    {
        session.Tools.Press(start, modifiers);
        session.Tools.Drag(start with { Tick = (start.Tick + end.Tick) / 2 });
        session.Tools.Drag(end);
        session.Tools.Release(end);
    }

    /// <summary>The vocal part <paramref name="track"/> as the session edits it now.</summary>
    public static VocalTrackContent Vocals(EditorSession session, VocalTrack track = VocalTrack.Vocals) => session.VocalContentOf(track)!;
}
