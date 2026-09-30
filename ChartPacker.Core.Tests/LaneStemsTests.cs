using ChartPacker.Core.Charts;
using ChartPacker.Core.Scanning;

namespace ChartPacker.Core.Tests;

[TestClass]
public sealed class LaneStemsTests
{
    [TestMethod]
    public void AnInstrumentIsDrawnOverItsOwnStem()
    {
        Assert.AreEqual("guitar", LaneStems.Pick(LaneStems.For(ChartInstrument.Guitar), ["song", "guitar", "bass"], null));
        Assert.AreEqual("keys", LaneStems.Pick(LaneStems.For(ChartInstrument.Keys), ["song", "keys"], null));
    }

    [TestMethod]
    public void WithoutAStemOfItsOwnTheSongIsUsed()
    {
        Assert.AreEqual("song", LaneStems.Pick(LaneStems.For(ChartInstrument.Keys), ["song", "guitar"], null));
    }

    [TestMethod]
    public void WithNeitherThereIsNothingToDraw()
    {
        Assert.IsNull(LaneStems.Pick(LaneStems.For(ChartInstrument.Keys), ["guitar", "drums"], null));
        Assert.IsNull(LaneStems.Pick(LaneStems.For(ChartInstrument.Guitar), [], null));
    }

    [TestMethod]
    public void BassFallsBackToRhythmBeforeTheSong()
    {
        Assert.AreEqual("bass", LaneStems.Pick(LaneStems.For(ChartInstrument.Bass), ["song", "rhythm", "bass"], null));
        Assert.AreEqual("rhythm", LaneStems.Pick(LaneStems.For(ChartInstrument.Bass), ["song", "rhythm"], null));
    }

    [TestMethod]
    public void DrumsFallBackToTheirSplitStems()
    {
        Assert.AreEqual("drums", LaneStems.Pick(LaneStems.For(ChartInstrument.Drums), ["song", "drums_1", "drums"], null));
        Assert.AreEqual("drums_1", LaneStems.Pick(LaneStems.For(ChartInstrument.Drums), ["song", "drums_2", "drums_1"], null));
    }

    [TestMethod]
    public void VocalsPreferTheLead()
    {
        Assert.AreEqual("vocals_1", LaneStems.Pick(LaneStems.Vocals, ["song", "vocals_2", "vocals_1"], null));
        Assert.AreEqual("vocals", LaneStems.Pick(LaneStems.Vocals, ["vocals_1", "vocals"], null));
    }

    [TestMethod]
    public void AChosenStemIsUsedWhenTheSongHasIt()
    {
        Assert.AreEqual("drums", LaneStems.Pick(LaneStems.For(ChartInstrument.Guitar), ["song", "guitar", "drums"], "drums"));
    }

    [TestMethod]
    public void AChosenStemTheSongNoLongerHasFallsBackToTheLanesOwn()
    {
        Assert.AreEqual("guitar", LaneStems.Pick(LaneStems.For(ChartInstrument.Guitar), ["song", "guitar"], "drums"));
    }

    [TestMethod]
    public void ThePreviewIsNeverPicked()
    {
        Assert.IsNull(LaneStems.Pick(LaneStems.For(ChartInstrument.Guitar), ["preview"], null));
        Assert.IsNull(LaneStems.Pick(LaneStems.For(ChartInstrument.Guitar), ["preview"], "preview"));
    }
}
