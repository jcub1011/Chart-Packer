using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Tests.Charts;

[TestClass]
public sealed class TempoMapTests
{
    // 120 BPM for four beats, then 60 BPM.
    private static readonly TempoMap Map = new(480, [new TempoChange(0, 500_000), new TempoChange(1920, 1_000_000)], []);

    [TestMethod]
    public void TicksBecomeSecondsAcrossTempoChanges()
    {
        Assert.AreEqual(0, Map.SecondsAt(0), 1e-9);
        Assert.AreEqual(2, Map.SecondsAt(1920), 1e-9);
        Assert.AreEqual(3, Map.SecondsAt(2400), 1e-9);
    }

    [TestMethod]
    public void SecondsBecomeTicksAcrossTempoChanges()
    {
        Assert.AreEqual(960, Map.TickAt(1), 1e-6);
        Assert.AreEqual(2400, Map.TickAt(3), 1e-6);
        Assert.AreEqual(-480, Map.TickAt(-0.5), 1e-6);
    }

    [TestMethod]
    public void MissingChangesDefaultTo120BpmIn44()
    {
        var map = new TempoMap(192, [], []);

        Assert.AreEqual(0.5, map.SecondsAt(192), 1e-9);
        CollectionAssert.AreEqual(new[] { new TimeSignature(0, 4, 4) }, map.TimeSignatures.ToArray());
    }

    [TestMethod]
    public void ShiftMovesBySongTimeToTheNearestTickAndNotBeforeTheStart()
    {
        Assert.AreEqual(1920 + 240, Map.Shift(1920, TimeSpan.FromMilliseconds(500)));
        Assert.AreEqual(1920 - 480, Map.Shift(1920, TimeSpan.FromMilliseconds(-500)));
        Assert.AreEqual(0, Map.Shift(100, TimeSpan.FromSeconds(-1)));
        Assert.AreEqual(1, Map.Shift(0, TimeSpan.FromMilliseconds(1)));
    }

    [TestMethod]
    public void BeatLinesFollowTheTimeSignature()
    {
        var map = new TempoMap(100, [], [new TimeSignature(0, 3, 4), new TimeSignature(300, 2, 8)]);

        var lines = map.BeatLines(0, 500).ToList();

        CollectionAssert.AreEqual(
            new[] { new BeatLine(0, true), new BeatLine(100, false), new BeatLine(200, false), new BeatLine(300, true), new BeatLine(350, false), new BeatLine(400, true), new BeatLine(450, false), new BeatLine(500, true) },
            lines);
        Assert.AreEqual(new BeatLine(400, true), map.BeatLines(390, 420).Single());
    }
}
