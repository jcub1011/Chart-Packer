using ChartShared.Core.Charts;
using ChartShared.UI.Controls.Highway;

namespace ChartBuilder.Tests.Preview;

/// <summary>A highway's notes marked out from the rest, as the Reduce dialog's preview marks what a draft changed.</summary>
[TestClass]
public sealed class HighwayLaneMarksTests
{
    private static readonly ChartNote[] Notes = [new(0, 0, 0, NoteFlags.None), new(480, 0, 1, NoteFlags.None)];
    private static readonly TempoMap Tempo = new(480, [new TempoChange(0, 500_000)], [new TimeSignature(0, 4, 4)]);

    [TestMethod]
    public void ALaneWithoutMarksHasNone()
    {
        var lane = new HighwayLane(ChartInstrumentKind.FiveFret, 5, "Expert", Notes, Tempo, TimeSpan.Zero);

        Assert.IsNull(lane.Marks);
    }

    [TestMethod]
    public void EachNoteHasItsMarkInOrder()
    {
        var lane = new HighwayLane(ChartInstrumentKind.FiveFret, 5, "Expert", Notes, Tempo, TimeSpan.Zero, [HighwayNoteMark.Faded, HighwayNoteMark.Flagged]);

        Assert.AreSequenceEqual([HighwayNoteMark.Faded, HighwayNoteMark.Flagged], lane.Marks!.ToList());
    }

    [TestMethod]
    public void AMarkForEachNoteIsNeeded() =>
        Assert.ThrowsExactly<ArgumentException>(() => new HighwayLane(ChartInstrumentKind.FiveFret, 5, "Expert", Notes, Tempo, TimeSpan.Zero, [HighwayNoteMark.Faded]));
}
