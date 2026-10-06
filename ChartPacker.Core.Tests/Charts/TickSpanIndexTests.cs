using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Tests.Charts;

/// <summary>Finding the spans that cover a tick by looking them up rather than checking every one.</summary>
[TestClass]
public sealed class TickSpanIndexTests
{
    [TestMethod]
    public void ASpanCoversItsStartAndTheTicksBeforeItsEnd()
    {
        var index = TickSpanIndex.Of([new TickSpan(100, 200), new TickSpan(300, 300)]);

        Assert.AreSequenceEqual(
            [false, true, true, false, false, true, false],
            new long[] { 99, 100, 199, 200, 299, 300, 301 }.Select(index.Covers).ToList());
    }

    [TestMethod]
    public void OverlappingSpansAreEachCounted()
    {
        // The long first span still covers ticks past later, shorter ones.
        var index = TickSpanIndex.Of([new TickSpan(0, 1000), new TickSpan(100, 110), new TickSpan(100, 100), new TickSpan(500, 600)]);

        Assert.AreEqual(3, index.CountCovering(100));
        Assert.AreEqual(1, index.CountCovering(300));
        Assert.AreEqual(2, index.CountCovering(550));
        Assert.AreEqual(0, index.CountCovering(1000));
        Assert.AreEqual(0, TickSpanIndex.Of([]).CountCovering(0));
    }

    [TestMethod]
    public void TheItemsCoveringATickAreThoseWhoseSpansDo()
    {
        var index = new TickSpanIndex<string>(["a", "b", "c"], s => s switch
        {
            "a" => new TickSpan(0, 50),
            "b" => new TickSpan(40, 40),
            _ => new TickSpan(60, 70),
        });

        Assert.AreSequenceEqual(["b", "a"], index.Covering(40).ToList());
        Assert.IsEmpty(index.Covering(55));
    }

    [TestMethod]
    public void LookingUpFindsWhatCheckingEverySpanFinds()
    {
        var random = new Random(7);
        for (int round = 0; round < 50; round++)
        {
            var spans = Enumerable.Range(0, random.Next(0, 40))
                .Select(_ => random.Next(0, 500))
                .Select(start => new TickSpan(start, start + (random.Next(4) == 0 ? 0 : random.Next(1, 120))))
                .ToList();
            var index = TickSpanIndex.Of(spans);

            for (long tick = 0; tick < 650; tick++)
            {
                int expected = spans.Count(s => s.Covers(tick));
                Assert.AreEqual(expected, index.CountCovering(tick), $"round {round}, tick {tick}");
                Assert.AreEqual(expected > 0, index.Covers(tick), $"round {round}, tick {tick}");
            }
        }
    }
}
