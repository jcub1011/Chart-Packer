using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Editing;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// Find (Ctrl+F): the passages of a lane that match the selected notes, found as the Sync tool finds them, which the
/// cursor goes to one at a time (F3, Shift+F3); and the navigation keys beside it, Home, End and Go to measure.
/// </summary>
[TestClass]
public sealed class EditorFindTests
{
    private const long Measure = 1920;

    private static readonly EditableNote[] Riff = [new(0, 1, 0), new(480, 1, 1), new(960, 1, 2)];

    // A guitar session with the riff in each of the measures, the first measure's riff selected.
    private static EditorSession WithRiffs(params int[] measures)
    {
        var session = Of(Guitar());
        foreach (int measure in measures)
        {
            foreach (var note in Riff)
                Expert(session).Notes.Add(note with { Tick = (measure * Measure) + note.Tick });
        }

        session.Selection.Select(GuitarExpert, Expert(session).Notes.Between(0, Measure - 1).ToArray());
        return session;
    }

    [TestMethod]
    public void FindingGoesToTheFirstPassageAfterTheCursor()
    {
        var session = WithRiffs(0, 2, 4);

        session.Find.Start();

        Assert.AreSequenceEqual([0L, 2 * Measure, 4 * Measure], session.Find.Matches.ToList(), "The notes searched for are a match too");
        Assert.AreEqual(1, session.Find.Current);
        Assert.AreEqual(2 * Measure, session.CursorTick);
    }

    [TestMethod]
    public void NextAndPreviousWrapAround()
    {
        var session = WithRiffs(0, 2, 4);
        session.Find.Start();

        session.Find.Step(1);
        Assert.AreEqual(4 * Measure, session.CursorTick);

        session.Find.Step(1);
        Assert.AreEqual(0, session.CursorTick, "Past the last passage, the first, which is the notes searched for");

        session.Find.Step(-1);
        Assert.AreEqual(4 * Measure, session.CursorTick, "Before the first passage, the last");
    }

    [TestMethod]
    public void FindingAgainGoesToTheNextPassage()
    {
        var session = WithRiffs(0, 2, 4);
        session.Find.Start();

        session.Find.Start();

        Assert.AreEqual(4 * Measure, session.CursorTick);
    }

    [TestMethod]
    public void NothingSelectedFindsNothingAndSaysSo()
    {
        var session = WithRiffs(0, 2);
        session.Selection.Clear();

        session.Find.Start();

        Assert.IsFalse(session.Find.HasMatches);
        Assert.IsNotNull(session.Status);
    }

    [TestMethod]
    public void NoOtherPassageMatchingFindsNothing()
    {
        var session = WithRiffs(0);

        session.Find.Start();

        Assert.IsFalse(session.Find.HasMatches);
        Assert.AreEqual(0, session.CursorTick);
        Assert.IsNotNull(session.Status, "It says nothing else matched");
    }

    [TestMethod]
    public void ASingleTapIsFoundWhereverItRepeats()
    {
        var session = Of(Guitar());
        foreach (long measure in new long[] { 0, 2, 4 })
            Expert(session).Notes.Add(new EditableNote(measure * Measure, 1, 0));
        session.Selection.Select(GuitarExpert, Expert(session).Notes.Between(0, Measure - 1).ToArray());

        session.Find.Start();

        Assert.AreSequenceEqual([0L, 2 * Measure, 4 * Measure], session.Find.Matches.ToList());
    }

    [TestMethod]
    public void SteppingVisitsEachPassageOnceAround()
    {
        var session = WithRiffs(0, 2, 4, 6);
        session.Find.Start();

        var visited = new List<long> { session.CursorTick };
        for (int i = 0; i < 3; i++)
        {
            session.Find.Step(1);
            visited.Add(session.CursorTick);
        }

        Assert.AreSequenceEqual([2 * Measure, 4 * Measure, 6 * Measure, 0L], visited);
    }

    [TestMethod]
    public void ChangingTheSelectionForgetsThePassagesFound()
    {
        var session = WithRiffs(0, 2, 4);
        session.Find.Start();

        session.Selection.Clear();

        Assert.IsFalse(session.Find.HasMatches);
    }

    [TestMethod]
    public void FindingMatchesWhatSyncMatches()
    {
        var session = WithRiffs(0, 2, 4);
        session.Find.Start();

        Assert.IsTrue(session.Sync.Define(GuitarExpert, 0, Measure));
        Assert.AreSequenceEqual(session.Sync.Matches.ToList(), session.Find.Matches.Where(m => m != 0).ToList());
    }

    [TestMethod]
    public void ChartEndIsWhereTheLastNoteLetsGo()
    {
        var session = Of(Guitar());
        Expert(session).Notes.Add(new EditableNote(100, 1, 0));
        Expert(session).Notes.Add(new EditableNote(200, 1000, 1));
        Expert(session).Notes.Add(new EditableNote(300, 1, 2));

        session.Tools.GoToEnd();
        Assert.AreEqual(1200, session.ChartEndTick);
        Assert.AreEqual(1200, session.CursorTick);

        session.Tools.GoToStart();
        Assert.AreEqual(0, session.CursorTick);
    }

    [TestMethod]
    public void GoingToAMeasureStartsItsMeasureLine()
    {
        var session = Of(Guitar());
        long resolution = session.TempoMap.Resolution;

        session.Tools.GoToMeasure(3);

        Assert.AreEqual(2 * resolution * 4, session.CursorTick);
        Assert.AreEqual(3, MeasureBeat.At(session.TempoMap, session.CursorTick).Measure);
    }
}
