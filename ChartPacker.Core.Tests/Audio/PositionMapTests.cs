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

        // Every chunk at another speed, so each is a segment of its own; the first 36 are forgotten.
        long source = 0;
        int chunks = PositionMap.Capacity + 36;
        for (int i = 0; i < chunks; i++)
        {
            long next = source + 10 + (i % 2);
            map.Add(i * 10, 10, source, next);
            source = next;
        }

        Assert.AreEqual(map.SourceAt(360), map.SourceAt(0), "Before what's kept, where that starts.");
        Assert.AreNotEqual(map.SourceAt(360), map.SourceAt(370), "From there on, it's kept.");
        Assert.AreEqual(source, map.SourceAt(chunks * 10));
    }

    [TestMethod]
    public void OutputAtFindsTheFrameThatStandsForASourceFrame()
    {
        var map = new PositionMap();
        map.Reset(0, 1000);
        map.Add(0, 100, 1000, 1100);
        map.Add(100, 100, 1100, 1300);

        Assert.AreEqual(50, map.OutputAt(1050), 1e-9);
        Assert.AreEqual(150, map.OutputAt(1200), 1e-9);
        for (long output = 0; output <= 200; output += 7)
            Assert.AreEqual(output, map.OutputAt(map.SourceAt(output)), 1e-9);

        Assert.AreEqual(0, map.OutputAt(900), "Before what's kept, where that starts.");
        Assert.AreEqual(200, map.OutputAt(5000), "Past what's been written, where it ends.");
    }

    [TestMethod]
    public void OutputAtFindsTheFirstFrameWhenTheSourceStepsBack()
    {
        // Stretched, the grains played can step back a little: 1100 is played twice, first at 100.
        var map = new PositionMap();
        map.Reset(0, 1000);
        map.Add(0, 100, 1000, 1100);
        map.Add(100, 50, 1100, 1080);
        map.Add(150, 100, 1080, 1180);

        Assert.AreEqual(100, map.OutputAt(1100), 1e-9);
        Assert.AreEqual(90, map.OutputAt(1090), 1e-9);
        Assert.AreEqual(220, map.OutputAt(1150), 1e-9);
    }
}
