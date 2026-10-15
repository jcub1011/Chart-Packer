using ChartShared.Core.Charts;
using ChartShared.Core.Scanning;

namespace ChartShared.Core.Tests;

[TestClass]
public sealed class LaneStemsTests
{
    [TestMethod]
    public void AnInstrumentIsDrawnOverItsOwnStem()
    {
        Assert.AreSequenceEqual(["guitar"], Pick(ChartInstrument.Guitar, ["song", "guitar", "bass"]));
        Assert.AreSequenceEqual(["keys"], Pick(ChartInstrument.Keys, ["song", "keys"]));
    }

    [TestMethod]
    public void WithoutAStemOfItsOwnTheSongIsUsed()
    {
        Assert.AreSequenceEqual(["song"], Pick(ChartInstrument.Keys, ["song", "guitar"]));
    }

    [TestMethod]
    public void WithNeitherThereIsNothingToDraw()
    {
        Assert.IsEmpty(Pick(ChartInstrument.Keys, ["guitar", "drums"]));
        Assert.IsEmpty(Pick(ChartInstrument.Guitar, []));
    }

    [TestMethod]
    public void BassFallsBackToRhythmBeforeTheSong()
    {
        Assert.AreSequenceEqual(["bass"], Pick(ChartInstrument.Bass, ["song", "rhythm", "bass"]));
        Assert.AreSequenceEqual(["rhythm"], Pick(ChartInstrument.Bass, ["song", "rhythm"]));
    }

    [TestMethod]
    public void TheWholeDrumStemIsPreferredToTheSplitOnes()
    {
        Assert.AreSequenceEqual(["drums"], Pick(ChartInstrument.Drums, ["song", "drums_1", "drums"]));
    }

    [TestMethod]
    public void SplitDrumStemsAreAllDrawnTogether()
    {
        Assert.AreSequenceEqual(["drums_1", "drums_2", "drums_3", "drums_4"], Pick(ChartInstrument.Drums, ["song", "drums_4", "drums_2", "drums_1", "drums_3"]));
        Assert.AreSequenceEqual(["drums_1", "drums_3"], Pick(ChartInstrument.Drums, ["drums_3", "drums_1"]));
    }

    [TestMethod]
    public void VocalsPreferTheLead()
    {
        Assert.AreSequenceEqual(["vocals_1"], LaneStems.Pick(LaneStems.Vocals, ["song", "vocals_2", "vocals_1"], null).ToList());
        Assert.AreSequenceEqual(["vocals"], LaneStems.Pick(LaneStems.Vocals, ["vocals_1", "vocals"], null).ToList());
    }

    [TestMethod]
    public void AChosenStemIsUsedWhenTheSongHasIt()
    {
        Assert.AreSequenceEqual(["drums"], Pick(ChartInstrument.Guitar, ["song", "guitar", "drums"], "drums"));
    }

    [TestMethod]
    public void AChosenSplitStemIsDrawnOnItsOwn()
    {
        Assert.AreSequenceEqual(["drums_2"], Pick(ChartInstrument.Drums, ["drums_1", "drums_2", "drums_3"], "drums_2"));
    }

    [TestMethod]
    public void AChosenStemTheSongNoLongerHasFallsBackToTheLanesOwn()
    {
        Assert.AreSequenceEqual(["guitar"], Pick(ChartInstrument.Guitar, ["song", "guitar"], "drums"));
    }

    [TestMethod]
    public void ThePreviewIsNeverPicked()
    {
        Assert.IsEmpty(Pick(ChartInstrument.Guitar, ["preview"]));
        Assert.IsEmpty(Pick(ChartInstrument.Guitar, ["preview"], "preview"));
    }

    private static List<string> Pick(ChartInstrument instrument, string[] available, string? chosen = null) =>
        [.. LaneStems.Pick(LaneStems.For(instrument), available, chosen)];
}
