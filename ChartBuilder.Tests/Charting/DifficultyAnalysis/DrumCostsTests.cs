using ChartShared.Core.Charting.Difficulty;

namespace ChartBuilder.Tests.Charting.DifficultyAnalysis;

/// <summary>Drums' kick and hands costs (docs/difficulty-model.md §14.1).</summary>
[TestClass]
public sealed class DrumCostsTests
{
    private const byte Red = 0b1, Yellow = 0b10, Blue = 0b100, Green = 0b1000;
    private const byte Kick = DrumPads.Kick;
    private static readonly DifficultyParameters P = DifficultyParameters.For(DifficultyInstrument.Drums);

    [TestMethod]
    public void SingleStrokesOnOnePadAlternateHands()
    {
        // Sixteenths 75 ms apart: each hand hits every 150 ms, past its limit's interval, so at its plain rate.
        var costs = Costs(P, Enumerable.Range(0, 3).Select(i => Note(i * 75.0, Red)));

        Assert.AreEqual(0, costs[0].Second);
        Assert.AreEqual(0, costs[1].Second);
        Assert.AreEqual(1000 / 150.0, costs[2].Second, 1e-9);
    }

    [TestMethod]
    public void KicksFasterThanTheDoubleKickIntervalCostMoreThanTheirRate()
    {
        var slow = Costs(P, [Note(0, Kick), Note(120, Kick)]);
        var fast = Costs(P, [Note(0, Kick), Note(80, Kick)]);

        Assert.AreEqual(1000 / 120.0, slow[1].First, 1e-9);
        Assert.AreEqual(1000 / 80.0 * Math.Pow(P.DoubleKickMs / 80, P.DoubleKickPower), fast[1].First, 1e-9);
    }

    [TestMethod]
    public void AKickBetweenTheHandsHitsCostsMoreThanOneWithThem()
    {
        var with = Costs(P, [Note(0, Kick | Red), Note(250, Kick | Red)]);
        var between = Costs(P, [Note(0, Kick), Note(150, Red), Note(250, Kick)]);

        Assert.AreEqual(with[1].First * (1 + P.KickIndependenceCost), between[2].First, 1e-9);
    }

    [TestMethod]
    public void MovingAroundTheKitIsHarderThanStayingOnOnePad()
    {
        byte[] around = [Red, Yellow, Blue, Green, Blue, Yellow];
        var still = DifficultyAnalyzer.Analyze([.. Enumerable.Range(0, 240).Select(i => Note(i * 100.0, Red, i * 0.25))], P);
        var moving = DifficultyAnalyzer.Analyze([.. Enumerable.Range(0, 240).Select(i => Note(i * 100.0, around[i % around.Length], i * 0.25))], P);

        Assert.IsGreaterThan(still.ChartAttributes.Note, moving.ChartAttributes.Note);
    }

    [TestMethod]
    public void ATomAndACymbalAreOnTheSameRowOnlyInTheFourLaneView()
    {
        var pro = DifficultyParameters.For(DifficultyInstrument.ProDrums);
        byte yellowCymbal = 0b1_0000;

        // Red, then the yellow cymbal: in the 4-lane view, read as the yellow tom.
        var fourLane = Costs(P, [Note(0, Red), Note(300, Red), Note(450, Yellow)]);
        var proView = Costs(pro, [Note(0, Red), Note(300, Red), Note(450, yellowCymbal)]);

        Assert.AreEqual(fourLane[2].Second + ((1000 / 450.0) * P.CymbalRowCost), proView[2].Second, 1e-9);
    }

    [TestMethod]
    public void HitsInARollLaneAreCountedNoFasterThanTheLanesFloor()
    {
        // A buzz roll, a hit every 40 ms: each hand every 80 ms, read as the lane's floor.
        var notes = Enumerable.Range(0, 200).Select(i => Note(i * 40.0, Red, i / 12.5)).ToList();
        var free = DifficultyAnalyzer.Analyze([.. notes.Select(n => n with { Kind = NoteKind.Roll })], P);
        var played = DifficultyAnalyzer.Analyze(notes, P);

        var costs = Costs(P, [.. notes.Take(3).Select(n => n with { Kind = NoteKind.Roll })]);
        Assert.AreEqual(1000 / P.LaneIntervalMs, costs[2].Second, 1e-9);
        Assert.IsGreaterThan(free.ChartAttributes.Note * 2, played.ChartAttributes.Note);
        Assert.AreEqual(0, free.ChartAttributes.Rhythm);
        Assert.AreEqual(0, free.ChartAttributes.Pattern);
    }

    private static DifficultyNote Note(double timeMs, int lanes, double beat = 0) => new(timeMs, beat, (byte)lanes, NoteKind.Strum, 0);

    // Each note's costs to the kick and the hands, in order.
    private static List<(double First, double Second)> Costs(DifficultyParameters p, IEnumerable<DifficultyNote> notes)
    {
        var costs = new DrumCosts(p);
        var result = new List<(double, double)>();
        double previous = double.NaN;
        foreach (var note in notes)
        {
            bool first = double.IsNaN(previous);
            double guarded = first ? double.PositiveInfinity : Math.Max(note.TimeMs - previous, p.MinIntervalMs);
            result.Add(costs.Cost(note, guarded, first));
            previous = note.TimeMs;
        }

        return result;
    }
}
