using ChartPacker.Audio.Playback;

namespace ChartPacker.Core.Tests.Audio;

[TestClass]
public sealed class PositionMapTests
{
    [TestMethod]
    public void AfterAResetItStandsForWhereItWasReset()
    {
        var map = new PositionMap();
        map.Reset(500, 1000);

        Assert.AreEqual(1000, map.SourceAt(500));
        Assert.AreEqual(1000, map.SourceAt(800), "Nothing has been written past it yet.");
    }

    [TestMethod]
    public void EachSpeedHasItsOwnSegment()
    {
        var map = new PositionMap();
        map.Reset(0, 1000);
        map.Add(0, 100, 1000, 1100);
        map.Add(100, 100, 1100, 1200);
        map.Add(200, 100, 1200, 1400);

        Assert.AreEqual(1050, map.SourceAt(50));
        Assert.AreEqual(1150, map.SourceAt(150));
        Assert.AreEqual(1300, map.SourceAt(250));
        Assert.AreEqual(1400, map.SourceAt(1000), "Past what's been written, where it ends.");
    }

    [TestMethod]
    public void TheOldestIsForgottenOnceItIsFull()
    {
        var map = new PositionMap();
        map.Reset(0, 0);

        // Every chunk at another speed, so each is a segment of its own.
        long source = 0;
        for (int i = 0; i < 100; i++)
        {
            long next = source + 10 + (i % 2);
            map.Add(i * 10, 10, source, next);
            source = next;
        }

        Assert.AreEqual(map.SourceAt(360), map.SourceAt(0), "Before what's kept, where that starts.");
        Assert.AreEqual(source, map.SourceAt(1000));
    }
}
