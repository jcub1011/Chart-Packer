using ChartBuilder.Tests.Charting;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Editing;
using ChartPacker.Core.Charts;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// The Tempo map tab (charter-spec C4): its rows, the edits its fields and buttons make, and Ctrl+Shift+↑ and
/// Ctrl+Shift+↓, as the tools carry them out.
/// </summary>
[TestClass]
public sealed class TempoMapTabTests
{
    // At 120 BPM and 480 ticks per beat, a second is 960 ticks; a measure of 4/4 is 1920.
    private const long Measure = 1920;
    private const long TenSeconds = 9_600;
    private const long ThirtySeconds = 28_800;

    private static readonly long Bpm140 = EditableTempoMap.MicrosecondsPerQuarterOf(140);

    // The rows.

    [TestMethod]
    public void ARowIsATickWithATempoOrATimeSignatureWithWhatsInForceThere()
    {
        var chart = Guitar();
        chart.TempoMap.Tempos.Add(new TempoChange(Measure, 400_000));
        chart.TempoMap.Anchors.Add(new TempoAnchor(Measure, 1_000_000));
        chart.TempoMap.TimeSignatures.Add(new TimeSignatureMarker(2 * Measure, 3, 4));

        var rows = TempoMapRows.Of(chart.TempoMap);

        CollectionAssert.AreEqual(new[] { 0L, Measure, 2 * Measure }, rows.Select(r => r.Tick).ToArray());
        Assert.IsTrue(rows[0].HasTempo && rows[0].HasSignature);
        Assert.IsTrue(rows[1].HasTempo);
        Assert.IsFalse(rows[1].HasSignature);
        Assert.IsTrue(rows[1].IsAnchored);
        Assert.AreEqual(new TimeSignature(0, 4, 4), rows[1].SignatureInForce);
        Assert.IsFalse(rows[2].HasTempo);
        Assert.AreEqual(400_000, rows[2].TempoInForce, "The tempo carried on from tick 1920.");
        Assert.AreEqual(new TimeSignatureMarker(2 * Measure, 3, 4), rows[2].Signature);
    }

    [TestMethod]
    public void ARowShowsTheLastMarkerAtItsTickAsTheGamesReadIt()
    {
        var chart = Guitar();
        chart.TempoMap.Tempos.Add(new TempoChange(Measure, 400_000));
        chart.TempoMap.Tempos.Add(new TempoChange(Measure, 300_000));

        var rows = TempoMapRows.Of(chart.TempoMap);

        Assert.HasCount(2, rows);
        Assert.AreEqual(300_000, rows[1].Tempo!.Value.MicrosecondsPerQuarter);
        Assert.AreEqual(300_000, rows[1].TempoInForce);
    }

    [TestMethod]
    public void TheSongsStartIsARowEvenWithNothingPlacedThere()
    {
        var chart = Guitar();
        chart.TempoMap.Tempos.RemoveAt(0);
        chart.TempoMap.TimeSignatures.RemoveAt(0);
        chart.TempoMap.Tempos.Add(new TempoChange(Measure, 400_000));

        var rows = TempoMapRows.Of(chart.TempoMap);

        Assert.HasCount(2, rows);
        Assert.AreEqual(0, rows[0].Tick);
        Assert.IsFalse(rows[0].HasTempo || rows[0].HasSignature);
        Assert.AreEqual(TempoMap.DefaultMicrosecondsPerQuarter, rows[0].TempoInForce);
        Assert.AreEqual(new TimeSignature(0, 4, 4), rows[0].SignatureInForce);
    }

    [TestMethod]
    [DataRow(0.0, 0)]
    [DataRow(1919.5, 0)]
    [DataRow(1920.0, 1)]
    [DataRow(1_000_000.0, 2)]
    public void TheRowInForceIsTheLastAtOrBeforeTheTick(double tick, int index)
    {
        var chart = Guitar();
        chart.TempoMap.Tempos.Add(new TempoChange(Measure, 400_000));
        chart.TempoMap.TimeSignatures.Add(new TimeSignatureMarker(2 * Measure, 3, 4));

        Assert.AreEqual(index, TempoMapRows.IndexAt(TempoMapRows.Of(chart.TempoMap), tick));
    }

    // The BPM field.

    [TestMethod]
    public void TypingABpmWhereThereIsNoMarkerAddsOne()
    {
        var chart = Guitar();

        var edit = TempoMapEdits.PlaceBpm(chart, Measure, Bpm140, TimeSpan.Zero, keepTimes: false);

        Assert.IsFalse(edit.IsRefused, edit.Refusal);
        CommandChecks.AppliesRevertsAndRedoes(chart, edit.Command!);
        Assert.AreEqual(new TempoChange(Measure, Bpm140), chart.TempoMap.Tempos.Single(t => t.Tick == Measure));
        Assert.AreEqual(500_000, chart.TempoMap.Tempos[0].MicrosecondsPerQuarter);
    }

    [TestMethod]
    public void TypingABpmWhereThereIsAMarkerChangesIt()
    {
        var chart = Guitar();
        chart.TempoMap.Tempos.Add(new TempoChange(Measure, 400_000));

        var edit = TempoMapEdits.PlaceBpm(chart, Measure, Bpm140, TimeSpan.Zero, keepTimes: false);
        CommandChecks.AppliesRevertsAndRedoes(chart, edit.Command!);

        Assert.HasCount(2, chart.TempoMap.Tempos);
        Assert.AreEqual(Bpm140, chart.TempoMap.Tempos[1].MicrosecondsPerQuarter);
    }

    [TestMethod]
    public void AddingATempoBeforeAnAnchorKeepsTheAnchorWhereItIs()
    {
        var chart = Anchored();

        var edit = TempoMapEdits.PlaceBpm(chart, 4_800, Bpm140, TimeSpan.Zero, keepTimes: false);
        CommandChecks.AppliesRevertsAndRedoes(chart, edit.Command!);

        Assert.AreEqual(Bpm140, chart.TempoMap.Tempos.Single(t => t.Tick == 4_800).MicrosecondsPerQuarter);
        Assert.AreEqual(30.0, chart.TempoMap.ToTempoMap().SecondsAt(ThirtySeconds), 0.001);
    }

    [TestMethod]
    public void ATempoTooFastOrSlowForMidiIsRefused()
    {
        var chart = Guitar();

        Assert.IsTrue(TempoMapEdits.PlaceBpm(chart, Measure, EditableTempoMap.MaxMicrosecondsPerQuarter + 1, TimeSpan.Zero, keepTimes: false).IsRefused);
        Assert.IsTrue(TempoMapEdits.PlaceBpm(chart, -1, Bpm140, TimeSpan.Zero, keepTimes: false).IsRefused);
    }

    // The time signature fields.

    [TestMethod]
    public void TypingATimeSignatureWhereThereIsNoneAddsOne()
    {
        var chart = Guitar();

        var edit = TempoMapEdits.PlaceSignature(chart, 2 * Measure, 3, 4);
        CommandChecks.AppliesRevertsAndRedoes(chart, edit.Command!);

        Assert.AreEqual(new TimeSignatureMarker(2 * Measure, 3, 4), chart.TempoMap.TimeSignatures.Single(s => s.Tick == 2 * Measure));
    }

    [TestMethod]
    public void TypingATimeSignatureWhereThereIsOneChangesItKeepingItsMetronomeBytes()
    {
        var chart = Guitar();
        chart.TempoMap.TimeSignatures.Add(new TimeSignatureMarker(2 * Measure, 3, 4, ClocksPerClick: 36));

        var edit = TempoMapEdits.PlaceSignature(chart, 2 * Measure, 6, 8);
        CommandChecks.AppliesRevertsAndRedoes(chart, edit.Command!);

        Assert.AreEqual(new TimeSignatureMarker(2 * Measure, 6, 8, ClocksPerClick: 36), chart.TempoMap.TimeSignatures.Single(s => s.Tick == 2 * Measure));
    }

    [TestMethod]
    [DataRow(0, 4)]
    [DataRow(65, 4)]
    [DataRow(4, 3)]
    [DataRow(4, 128)]
    [DataRow(4, 4)]
    public void ATimeSignatureTheEditorDoesntPlaceOrAlreadyThereIsRefused(int numerator, int denominator)
    {
        var chart = Guitar();

        var edit = TempoMapEdits.PlaceSignature(chart, 0, numerator, denominator);

        Assert.IsTrue(edit.IsRefused);
        Assert.IsFalse(string.IsNullOrWhiteSpace(edit.Refusal));
    }

    [TestMethod]
    public void EmptyingTheBeatsRemovesTheTimeSignatureEveryOneAtItsTick()
    {
        var chart = Guitar();
        chart.TempoMap.TimeSignatures.Add(new TimeSignatureMarker(2 * Measure, 3, 4));
        chart.TempoMap.TimeSignatures.Add(new TimeSignatureMarker(2 * Measure, 6, 8));

        var edit = TempoMapEdits.RemoveSignature(chart, 2 * Measure);
        CommandChecks.AppliesRevertsAndRedoes(chart, edit.Command!);

        Assert.IsTrue(chart.TempoMap.TimeSignatures.At(2 * Measure).IsEmpty);
        Assert.HasCount(1, chart.TempoMap.TimeSignatures);
    }

    [TestMethod]
    public void TheStartingTimeSignatureIsNotRemoved()
    {
        var chart = Guitar();

        Assert.IsTrue(TempoMapEdits.RemoveSignature(chart, 0).IsRefused);
        Assert.IsTrue(TempoMapEdits.RemoveSignature(chart, Measure).IsRefused, "There's none there.");
    }

    // Removing both.

    [TestMethod]
    public void RemovingBothTakesTheTempoItsAnchorAndTheTimeSignatureAsOneStep()
    {
        var chart = Guitar();
        chart.TempoMap.Tempos.Add(new TempoChange(Measure, 400_000));
        chart.TempoMap.Anchors.Add(new TempoAnchor(Measure, 1_000_000));
        chart.TempoMap.TimeSignatures.Add(new TimeSignatureMarker(Measure, 3, 4));

        var edit = TempoMapEdits.RemoveChange(chart, Measure, TimeSpan.Zero, keepTimes: false);
        CommandChecks.AppliesRevertsAndRedoes(chart, edit.Command!);

        Assert.HasCount(1, TempoMapRows.Of(chart.TempoMap));
        Assert.IsEmpty(chart.TempoMap.Anchors);
    }

    [TestMethod]
    public void TheSongsStartIsNotRemoved()
    {
        Assert.IsTrue(TempoMapEdits.RemoveChange(Guitar(), 0, TimeSpan.Zero, keepTimes: false).IsRefused);
    }

    // The tick field.

    [TestMethod]
    public void MovingAChangeMovesItsTempoAndTimeSignatureTogether()
    {
        var chart = Guitar();
        chart.TempoMap.Tempos.Add(new TempoChange(Measure, 400_000));
        chart.TempoMap.TimeSignatures.Add(new TimeSignatureMarker(Measure, 3, 4));

        var edit = TempoMapEdits.MoveChange(chart, Measure, 2 * Measure, TimeSpan.Zero, keepTimes: false);
        CommandChecks.AppliesRevertsAndRedoes(chart, edit.Command!);

        Assert.AreEqual(new TempoChange(2 * Measure, 400_000), chart.TempoMap.Tempos[1]);
        Assert.AreEqual(new TimeSignatureMarker(2 * Measure, 3, 4), chart.TempoMap.TimeSignatures[1]);
        Assert.IsTrue(chart.TempoMap.Tempos.At(Measure).IsEmpty && chart.TempoMap.TimeSignatures.At(Measure).IsEmpty);
    }

    [TestMethod]
    public void MovingATimeSignatureAloneMovesNothingElse()
    {
        var chart = Guitar();
        var track = chart.PartOf(ChartInstrument.Guitar)![ChartDifficulty.Expert];
        track.Notes.Add(new EditableNote(2_500, 0, 1));
        chart.TempoMap.TimeSignatures.Add(new TimeSignatureMarker(Measure, 3, 4));

        var edit = TempoMapEdits.MoveChange(chart, Measure, 2 * Measure, TimeSpan.Zero, keepTimes: true);
        CommandChecks.AppliesRevertsAndRedoes(chart, edit.Command!);

        Assert.AreEqual(new TimeSignatureMarker(2 * Measure, 3, 4), chart.TempoMap.TimeSignatures[1]);
        Assert.AreEqual(2_500, track.Notes.Single().Tick);
    }

    [TestMethod]
    public void AChangeIsNotMovedWhereItCantGo()
    {
        var chart = Anchored();
        chart.TempoMap.Tempos.Add(new TempoChange(4_800, 500_000));
        chart.TempoMap.TimeSignatures.Add(new TimeSignatureMarker(Measure, 3, 4));
        chart.TempoMap.TimeSignatures.Add(new TimeSignatureMarker(2 * Measure, 4, 4));
        var before = chart.Clone();

        Assert.IsTrue(TempoMapEdits.MoveChange(chart, 0, Measure, TimeSpan.Zero, false).IsRefused, "Off the song's start.");
        Assert.IsTrue(TempoMapEdits.MoveChange(chart, Measure, 0, TimeSpan.Zero, false).IsRefused, "Onto the song's start.");
        Assert.IsTrue(TempoMapEdits.MoveChange(chart, Measure, 2 * Measure, TimeSpan.Zero, false).IsRefused, "Onto another time signature.");
        Assert.IsTrue(TempoMapEdits.MoveChange(chart, 4_800, TenSeconds, TimeSpan.Zero, false).IsRefused, "Onto another tempo marker.");
        Assert.IsTrue(TempoMapEdits.MoveChange(chart, ThirtySeconds, ThirtySeconds + 960, TimeSpan.Zero, false).IsRefused, "An anchored marker.");
        Assert.IsTrue(TempoMapEdits.MoveChange(chart, TenSeconds, ThirtySeconds + 960, TimeSpan.Zero, false).IsRefused, "Past an anchor.");
        Assert.IsTrue(TempoMapEdits.MoveChange(chart, 3 * Measure, 4 * Measure, TimeSpan.Zero, false).IsRefused, "Nothing's there.");
        CommandChecks.AssertSame(before, chart, "after refusing");
    }

    // Where a time signature is added.

    [TestMethod]
    [DataRow(0L, 0L)]
    [DataRow(1000L, 0L)]
    [DataRow(1920L, 1920L)]
    [DataRow(3839L, 1920L)]
    [DataRow(3840L, 3840L)]
    [DataRow(5290L, 5280L)]
    public void AMeasureStartsAtItsTimeSignatureAndEveryMeasureAfter(long tick, long start)
    {
        // 4/4, then 3/4 from tick 3840, whose measures are 1440 ticks.
        var chart = Guitar();
        chart.TempoMap.TimeSignatures.Add(new TimeSignatureMarker(2 * Measure, 3, 4));

        Assert.AreEqual(start, MeasureBeat.MeasureStart(chart.TempoMap.ToTempoMap(), tick));
    }

    [TestMethod]
    public void AddTimeSignaturePutsItAtTheStartOfTheCursorsMeasure()
    {
        var session = Of(Guitar());
        session.MoveCursorTo((2 * Measure) + 960);

        long? tick = session.Tools.PlaceSignatureAtCursor(3, 4);

        Assert.AreEqual(2 * Measure, tick);
        Assert.AreEqual(new TimeSignatureMarker(2 * Measure, 3, 4), session.Chart.TempoMap.TimeSignatures[1]);
        Assert.IsTrue(session.UndoStack.CanUndo);
        session.Undo();
        Assert.HasCount(1, session.Chart.TempoMap.TimeSignatures);
    }

    [TestMethod]
    public void AddTimeSignatureChangesTheOneAtTheMeasuresStart()
    {
        var chart = Guitar();
        chart.TempoMap.TimeSignatures.Add(new TimeSignatureMarker(2 * Measure, 3, 4));
        var session = Of(chart);
        session.MoveCursorTo((2 * Measure) + 480);

        session.Tools.PlaceSignatureAtCursor(6, 8);

        Assert.AreEqual(new TimeSignatureMarker(2 * Measure, 6, 8), session.Chart.TempoMap.TimeSignatures[1]);
        Assert.HasCount(2, session.Chart.TempoMap.TimeSignatures);
    }

    [TestMethod]
    public void AddTempoChangeAddsAMarkerAtTheCursorAndSelectsIt()
    {
        var session = Of(Guitar());
        session.MoveCursorTo(Measure);

        long? tick = session.Tools.AddTempoChangeAtCursor();

        Assert.AreEqual(Measure, tick);
        Assert.AreEqual(new TempoChange(Measure, 500_000), session.Chart.TempoMap.Tempos[1]);
        CollectionAssert.AreEqual(new[] { Measure }, session.Selection.TempoTicks.ToArray());
        Assert.IsNull(session.Tools.AddTempoChangeAtCursor(), "There's a marker there now.");
        Assert.IsNotNull(session.Status);
    }

    // Ctrl+Shift+↑ and Ctrl+Shift+↓.

    [TestMethod]
    [DataRow(0L, 1, 1920L)]
    [DataRow(0L, 2, 3840L)]
    [DataRow(2000L, 1, 3840L)]
    [DataRow(2000L, -1, 1920L)]
    [DataRow(5000L, -2, 1920L)]
    [DataRow(5000L, -3, 0L)]
    public void TheCursorGoesToTheNextOrPreviousChange(long from, int steps, long to)
    {
        var session = Of(WithChanges());
        session.MoveCursorTo(from);

        session.Tools.StepCursorTempoChanges(steps);

        Assert.AreEqual(to, CursorAt(session));
        Assert.IsNull(session.Status);
    }

    [TestMethod]
    [DataRow(3840L, 1, "after")]
    [DataRow(0L, -1, "before")]
    [DataRow(1920L, 2, "after")]
    public void PastTheFirstOrLastChangeTheCursorStaysAndTheStatusSaysSo(long from, int steps, string way)
    {
        var session = Of(WithChanges());
        session.MoveCursorTo(from);

        session.Tools.StepCursorTempoChanges(steps);

        Assert.AreEqual(from, CursorAt(session));
        Assert.Contains(way, session.Status!);
    }

    /// <summary>A tempo change at 1920 and a time signature at 3840, after the song's start.</summary>
    private static EditableChart WithChanges()
    {
        var chart = Guitar();
        chart.TempoMap.Tempos.Add(new TempoChange(Measure, 500_000));
        chart.TempoMap.TimeSignatures.Add(new TimeSignatureMarker(2 * Measure, 3, 4));
        return chart;
    }

    /// <summary>Markers at 0 s and 10 s and an anchored one at 30 s, all at 120 BPM.</summary>
    private static EditableChart Anchored()
    {
        var chart = Guitar();
        chart.TempoMap.Tempos.Add(new TempoChange(TenSeconds, 500_000));
        chart.TempoMap.Tempos.Add(new TempoChange(ThirtySeconds, 500_000));
        chart.TempoMap.Anchors.Add(new TempoAnchor(ThirtySeconds, 30_000_000));
        return chart;
    }

    private static long CursorAt(EditorSession session) => (long)Math.Round(session.TickAt(session.Viewport.Cursor));
}
