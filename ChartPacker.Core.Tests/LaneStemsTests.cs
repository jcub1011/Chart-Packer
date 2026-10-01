using ChartPacker.Core.Charts;
using ChartPacker.Core.Scanning;

namespace ChartPacker.Core.Tests;

[TestClass]
public sealed class LaneStemsTests
{
    [TestMethod]
    public void AnInstrumentIsDrawnOverItsOwnStem()
    {
        CollectionAssert.AreEqual(new[] { "guitar" }, Pick(ChartInstrument.Guitar, ["song", "guitar", "bass"]));
        CollectionAssert.AreEqual(new[] { "keys" }, Pick(ChartInstrument.Keys, ["song", "keys"]));
    }

    [TestMethod]
    public void WithoutAStemOfItsOwnTheSongIsUsed()
    {
        CollectionAssert.AreEqual(new[] { "song" }, Pick(ChartInstrument.Keys, ["song", "guitar"]));
    }

    [TestMethod]
    public void WithNeitherThereIsNothingToDraw()
    {
        Assert.AreEqual(0, Pick(ChartInstrument.Keys, ["guitar", "drums"]).Count);
        Assert.AreEqual(0, Pick(ChartInstrument.Guitar, []).Count);
    }

    [TestMethod]
    public void BassFallsBackToRhythmBeforeTheSong()
    {
        CollectionAssert.AreEqual(new[] { "bass" }, Pick(ChartInstrument.Bass, ["song", "rhythm", "bass"]));
        CollectionAssert.AreEqual(new[] { "rhythm" }, Pick(ChartInstrument.Bass, ["song", "rhythm"]));
    }

    [TestMethod]
    public void TheWholeDrumStemIsPreferredToTheSplitOnes()
    {
        CollectionAssert.AreEqual(new[] { "drums" }, Pick(ChartInstrument.Drums, ["song", "drums_1", "drums"]));
    }

    [TestMethod]
    public void SplitDrumStemsAreAllDrawnTogether()
    {
        CollectionAssert.AreEqual(new[] { "drums_1", "drums_2", "drums_3", "drums_4" }, Pick(ChartInstrument.Drums, ["song", "drums_4", "drums_2", "drums_1", "drums_3"]));
        CollectionAssert.AreEqual(new[] { "drums_1", "drums_3" }, Pick(ChartInstrument.Drums, ["drums_3", "drums_1"]));
    }

    [TestMethod]
    public void VocalsPreferTheLead()
    {
        CollectionAssert.AreEqual(new[] { "vocals_1" }, LaneStems.Pick(LaneStems.Vocals, ["song", "vocals_2", "vocals_1"], null).ToList());
        CollectionAssert.AreEqual(new[] { "vocals" }, LaneStems.Pick(LaneStems.Vocals, ["vocals_1", "vocals"], null).ToList());
    }

    [TestMethod]
    public void AChosenStemIsUsedWhenTheSongHasIt()
    {
        CollectionAssert.AreEqual(new[] { "drums" }, Pick(ChartInstrument.Guitar, ["song", "guitar", "drums"], "drums"));
    }

    [TestMethod]
    public void AChosenSplitStemIsDrawnOnItsOwn()
    {
        CollectionAssert.AreEqual(new[] { "drums_2" }, Pick(ChartInstrument.Drums, ["drums_1", "drums_2", "drums_3"], "drums_2"));
    }

    [TestMethod]
    public void AChosenStemTheSongNoLongerHasFallsBackToTheLanesOwn()
    {
        CollectionAssert.AreEqual(new[] { "guitar" }, Pick(ChartInstrument.Guitar, ["song", "guitar"], "drums"));
    }

    [TestMethod]
    public void ThePreviewIsNeverPicked()
    {
        Assert.AreEqual(0, Pick(ChartInstrument.Guitar, ["preview"]).Count);
        Assert.AreEqual(0, Pick(ChartInstrument.Guitar, ["preview"], "preview").Count);
    }

    private static List<string> Pick(ChartInstrument instrument, string[] available, string? chosen = null) =>
        [.. LaneStems.Pick(LaneStems.For(instrument), available, chosen)];
}
