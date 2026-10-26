using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charts;
using ChartShared.Core.Generation.Neural;

namespace ChartShared.Core.Tests.Generation;

[TestClass]
public sealed class BeatPipelineTests
{
    private static EditableChart Chart(params long[] ticks)
    {
        var chart = EditableChart.CreateNew(new NewChartSettings(
            "Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.Guitar }, DrumsVariant.FourLane));
        new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert,
            ticks.Select(t => new EditableNote(t, EditableNote.PlainLength, 0))).Apply(chart);
        return chart;
    }

    [TestMethod]
    public void BeatsAreSplitByTheTimeSignaturesWith48thPatterns()
    {
        // Beat 0 plays "1 &" (ticks 0 and 240 of a 480-tick beat), beat 1 rests, beat 2 plays "1".
        var frames = BeatPipeline.Extract(Chart(0, 240, 960), ChartInstrument.Guitar, ChartDifficulty.Expert);

        Assert.HasCount(3, frames);
        Assert.AreEqual(3, frames[0].SourcePatternId); // 1 &
        Assert.AreEqual(BeatPattern.EmptyId, frames[1].SourcePatternId);
        Assert.AreEqual(1, frames[2].SourcePatternId); // 1
        CollectionAssert.AreEqual(new[] { 0, 24 }, frames[0].SourceOffsets48);
    }

    [TestMethod]
    public void EmptyBeatsCountAsContext()
    {
        var frames = BeatPipeline.Extract(Chart(0, 1440), ChartInstrument.Guitar, ChartDifficulty.Expert);

        // Beats 0, 1, 2, 3: only 0 and 3 play.
        Assert.HasCount(4, frames);
        Assert.AreEqual(1, frames[0].SourcePatternId);
        Assert.AreEqual(BeatPattern.EmptyId, frames[1].SourcePatternId);
        Assert.AreEqual(BeatPattern.EmptyId, frames[2].SourcePatternId);
        Assert.AreEqual(1, frames[3].SourcePatternId);
    }

    [TestMethod]
    public void TierBucketTreatsMissingTierAsItsOwnValue()
    {
        Assert.AreEqual(7, BeatPipeline.TierBucketOf(null));
        Assert.AreEqual(3, BeatPipeline.TierBucketOf(3));
        Assert.AreEqual(6, BeatPipeline.TierBucketOf(99));
        Assert.AreEqual(0, BeatPipeline.TierBucketOf(-5));
    }

    [TestMethod]
    public void GapBucketsGrowWithTheGap()
    {
        int quarter = BeatPipeline.GapBucketOf(480, 480);
        int eighth = BeatPipeline.GapBucketOf(240, 480);

        Assert.IsTrue(eighth < quarter);
        Assert.AreEqual(0, BeatPipeline.GapBucketOf(0, 480));
    }
}
