using ChartBuilder.ViewModels;
using ChartShared.Core.Merging;

namespace ChartBuilder.Tests.Editing;

/// <summary>The sign between an audio alignment's offset and the video's start.</summary>
[TestClass]
public sealed class VideoStartAlignmentTests
{
    [TestMethod]
    public void AVideoMovedLaterNeedsASmallerStart()
    {
        // The video's music is 500 ms late, so 500 ms of its start is skipped.
        var estimate = new AlignmentEstimate(AlignmentMethod.Audio, OffsetMs: -500);

        Assert.AreEqual(500m, VideoStartViewModel.StartMsFor(estimate));
    }

    [TestMethod]
    public void AVideoMovedEarlierHoldsItsFirstFrame()
    {
        var estimate = new AlignmentEstimate(AlignmentMethod.Audio, OffsetMs: 750);

        Assert.AreEqual(-750m, VideoStartViewModel.StartMsFor(estimate));
    }
}
