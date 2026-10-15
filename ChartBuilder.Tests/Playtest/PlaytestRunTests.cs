using ChartPacker.Core.Charting.Playtest;
using ChartPacker.Core.Charts;
using static ChartBuilder.Tests.Playtest.Plays;
using static ChartPacker.Core.Input.InstrumentAction;

namespace ChartBuilder.Tests.Playtest;

/// <summary>A playtest's passes, marks and problems (charter-spec C19 item 4).</summary>
[TestClass]
public sealed class PlaytestRunTests
{
    [TestMethod]
    public void EachPassHasItsOwnStatsAndProblemsAddUpOverThePasses()
    {
        var run = Run(Note(1.0, 0), Note(2.0, 1));

        for (int pass = 0; pass < 2; pass++)
        {
            run.Start(0.5, 1, static _ => false, newPass: true);
            run.Handle(Down(Green, 0.9));
            run.Handle(Down(StrumDown, 1.0));
            run.Handle(Up(Green, 1.1));
            run.Advance(3, 1);
        }

        Assert.AreEqual(2, run.Passes);
        Assert.AreEqual((1, 1), (run.Pass.Hits, run.Pass.Misses));
        Assert.AreEqual((2, 2), (run.Total.Hits, run.Total.Misses));
        var problem = run.Problems.Single();
        Assert.AreEqual((TickAt(2.0), 1, JudgeKind.Miss, 2), (problem.Tick, problem.Lane, problem.Kind, problem.Count));
        Assert.AreEqual(JudgeKind.Hit, run.Marks[(TickAt(1.0), 0)].Kind);
        Assert.AreEqual(JudgeKind.Miss, run.Marks[(TickAt(2.0), 1)].Kind);
    }

    [TestMethod]
    public void AnInputArrivingLateFromASlowDeviceIsStillJudgedBeforeItsNoteIsMissed()
    {
        var run = Run(Note(1.0, 0));
        run.Start(0, 1, static _ => false, newPass: true);
        run.Handle(Down(Green, 0.5));

        // The song is at 1.09 when the strum from 1.06 arrives: past the note's window, but not the horizon behind it.
        run.Advance(1.09, 1);
        run.Handle(Down(StrumDown, 1.06));

        Assert.AreEqual((1, 0), (run.Pass.Hits, run.Pass.Misses));
    }

    [TestMethod]
    public void ANoteFixedAfterItWasMissedIsPlayedAfreshAndItsMarkDropped()
    {
        var run = Run(Note(1.0, 0), Note(2.0, 1));
        run.Start(0, 1, static _ => false, newPass: true);
        run.Advance(3, 1);
        Assert.HasCount(2, run.Marks);

        run.Replace(new FiveFretEngine(Frets(Note(1.0, 0), Note(2.0, 2)), new HitWindow(), new FretRules()), 0, 1, static _ => false);

        Assert.AreSequenceEqual([(TickAt(1.0), 0)], run.Marks.Keys.ToList());
        Assert.AreSequenceEqual([TickAt(1.0)], run.Problems.Select(p => p.Tick).ToList());
    }

    [TestMethod]
    public void AnOverstrumIsMarkedUntilItsPlaceIsPlayedAgain()
    {
        var run = Run(Note(3.0, 0));
        run.Start(0, 1, static _ => false, newPass: true);
        run.Handle(Down(StrumDown, 1.0));
        run.Advance(2, 1);
        Assert.HasCount(1, run.Strays);

        run.Start(0.5, 1, static _ => false, newPass: true);

        Assert.IsEmpty(run.Strays);
        Assert.AreEqual(JudgeKind.Overstrum, run.Problems.Single().Kind);
    }

    [TestMethod]
    public void APerformanceJudgedAgainIsJudgedAsItWasLive()
    {
        var chart = Frets(Note(1.0, 0), Note(1.2, 1, NoteFlags.Hopo), Note(1.5, 2), Note(2.0, ChartNote.OpenLane));
        var live = new FiveFretEngine(chart, new HitWindow(), new FretRules());
        var log = new PerformanceLog();
        var judged = new List<JudgeEvent>();
        live.Reset(0, 1, static _ => false);
        foreach (var action in new[] { Down(Green, 0.5), Down(StrumDown, 1.01), Down(Red, 1.19), Down(StrumDown, 1.6), Up(Green, 1.9), Up(Red, 1.9), Down(StrumDown, 2.02) })
        {
            log.Handle(action);
            live.Input(action, judged);
        }

        live.Advance(3, 1, judged);

        var again = log.Replay(new FiveFretEngine(chart, new HitWindow(), new FretRules()), 0, 3, 1);
        Assert.AreSequenceEqual(judged, again);
    }

    private static PlaytestRun Run(params ChartNote[] notes) => new(new FiveFretEngine(Frets(notes), new HitWindow(), new FretRules()));
}
