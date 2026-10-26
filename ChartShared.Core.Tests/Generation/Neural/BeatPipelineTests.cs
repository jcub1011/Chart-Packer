using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charts;
using ChartShared.Core.Generation.Neural;

namespace ChartShared.Core.Tests.Generation;

[TestClass]
public sealed class BeatPipelineTests
{
    private static EditableChart Chart(params long[] ticks) =>
        Chart([.. ticks.Select(static t => new EditableNote(t, EditableNote.PlainLength, 0))]);

    private static EditableChart Chart(EditableNote[] notes)
    {
        var chart = EditableChart.CreateNew(new NewChartSettings(
            "Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.Guitar }, DrumsVariant.FourLane));
        new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, notes).Apply(chart);
        return chart;
    }

    private static IReadOnlyList<BeatFrame> Extract(EditableChart chart) =>
        BeatPipeline.Extract(chart, ChartInstrument.Guitar, ChartDifficulty.Expert);

    // A measure of four quarter notes from tick from, on the lanes given.
    private static IEnumerable<EditableNote> Measure(long from, params int[] lanes) =>
        lanes.Select((lane, i) => new EditableNote(from + (i * 480L), EditableNote.PlainLength, lane));

    [TestMethod]
    public void BeatsAreSplitByTheTimeSignaturesWith48thPatterns()
    {
        // Beat 0 plays "1 &" (ticks 0 and 240 of a 480-tick beat), beat 1 rests, beat 2 plays "1".
        var frames = Extract(Chart(0, 240, 960));

        Assert.HasCount(3, frames);
        Assert.AreEqual(3, frames[0].SourcePatternId); // 1 &
        Assert.AreEqual(BeatPattern.EmptyId, frames[1].SourcePatternId);
        Assert.AreEqual(1, frames[2].SourcePatternId); // 1
        CollectionAssert.AreEqual(new[] { 0, 24 }, frames[0].SourceOffsets48);
    }

    [TestMethod]
    public void EmptyBeatsCountAsContext()
    {
        var frames = Extract(Chart(0, 1440));

        // Beats 0, 1, 2, 3: only 0 and 3 play.
        Assert.HasCount(4, frames);
        Assert.AreEqual(1, frames[0].SourcePatternId);
        Assert.AreEqual(BeatPattern.EmptyId, frames[1].SourcePatternId);
        Assert.AreEqual(BeatPattern.EmptyId, frames[2].SourcePatternId);
        Assert.AreEqual(1, frames[3].SourcePatternId);
    }

    [TestMethod]
    public void OnsetsATickOffTheGridAreOnIt()
    {
        var frames = Extract(Chart(0, 239));

        CollectionAssert.AreEqual(new[] { 0, 24 }, frames[0].SourceOffsets48);
        Assert.AreEqual(3, frames[0].SourcePatternId);
    }

    [TestMethod]
    public void RiffsAreMeasuresChartedTheSameNotJustPlayedTheSameRhythm()
    {
        // Measures 0 and 2 are the same riff; measure 1 has its rhythm on other frets.
        var frames = Extract(Chart([.. Measure(0, 0, 1, 2, 1), .. Measure(1920, 3, 4, 3, 2), .. Measure(3840, 0, 1, 2, 1)]));

        Assert.AreEqual(frames[0].RiffKey, frames[8].RiffKey);
        Assert.AreEqual(frames[1].RiffKey, frames[9].RiffKey);
        Assert.AreNotEqual(frames[0].RiffKey, frames[4].RiffKey, "Same rhythm on other frets is another riff (Rules' riffs, §3.5).");
        Assert.AreNotEqual(frames[0].RiffKey, frames[1].RiffKey, "Each beat of a measure is its own place in the riff.");
    }

    [TestMethod]
    public void RiffRepeatsCountMeasures()
    {
        var frames = Extract(Chart([.. Measure(0, 0, 1, 2, 1), .. Measure(1920, 0, 1, 2, 1), .. Measure(3840, 0, 1, 2, 1)]));

        CollectionAssert.AreEqual(new[] { 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2 }, frames.Select(static f => f.RiffRepeat).ToArray());
    }

    [TestMethod]
    public void BeatsKnowTheirTempoChordsAndTurns()
    {
        // At 120 BPM a beat is half a second. Beat 1 is a chord; beat 2 (red after yellow, before yellow) turns.
        var frames = Extract(Chart([
            new EditableNote(0, EditableNote.PlainLength, 0),
            new EditableNote(480, EditableNote.PlainLength, 2),
            new EditableNote(480, EditableNote.PlainLength, 4),
            new EditableNote(960, EditableNote.PlainLength, 1),
            new EditableNote(1440, EditableNote.PlainLength, 2),
        ]));

        Assert.AreEqual(0.5, frames[0].SecondsPerBeat, 1e-9);
        Assert.IsFalse(frames[0].HasChord);
        Assert.IsTrue(frames[1].HasChord);
        Assert.IsTrue(frames[2].MelodyTurn);
        Assert.IsFalse(frames[3].MelodyTurn);
    }

    [TestMethod]
    public void StarPowerBeatsNameTheirPhrase()
    {
        var chart = Chart(0, 480, 960, 1440, 1920, 2400);
        new AddPhrase(ChartInstrument.Guitar, new Phrase(PhraseKind.StarPower, 0, 960)).Apply(chart);
        new AddPhrase(ChartInstrument.Guitar, new Phrase(PhraseKind.StarPower, 1920, 960)).Apply(chart);

        var frames = Extract(chart);

        CollectionAssert.AreEqual(new[] { 0, 0, -1, -1, 1, 1 }, frames.Select(static f => f.StarPowerPhrase).ToArray());
        Assert.IsTrue(frames.All(static f => f.SoloPhrase == -1));
    }

    [TestMethod]
    public void GapBucketsGrowWithTheGap()
    {
        int quarter = BeatPipeline.GapBucketOf(480, 480);
        int eighth = BeatPipeline.GapBucketOf(240, 480);

        Assert.IsTrue(eighth < quarter);
        Assert.AreEqual(0, BeatPipeline.GapBucketOf(0, 480));
    }

    [TestMethod]
    public void FeaturesAreScaledIntoRange()
    {
        var frames = Extract(Chart(0, 240, 480));
        var features = new float[BeatFeatures.Count];

        BeatFeatures.Write(frames[0].Scalars, sourceRank: 7, useRank: true, features);

        Assert.IsTrue(features.All(static f => f is >= -1 and <= 2), "Each feature is roughly in [-1, 1].");
        Assert.AreEqual(0f, features[8], 1e-6, "120 BPM is the tempo feature's middle.");
        Assert.AreEqual(1f, features[12], 1e-6, "Rank 7 is the rank feature's top.");

        BeatFeatures.Write(frames[0].Scalars, sourceRank: 7, useRank: false, features);
        Assert.AreEqual(0f, features[12], "A pack without the rank reads none.");
    }
}
