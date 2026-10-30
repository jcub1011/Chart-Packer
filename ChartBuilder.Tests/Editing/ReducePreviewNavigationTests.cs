using ChartBuilder.Services;
using ChartShared.Core.Charting.Editing;
using ChartShared.UI.Controls;
using ChartShared.UI.Controls.Highway;
using ChartShared.UI.ViewModels;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// The Reduce dialog's preview: what each filter shows, how the highways mark a draft's changes, and going from change to
/// change.
/// </summary>
[TestClass]
public sealed class ReducePreviewNavigationTests
{
    private static readonly double[] Stops = [1, 2.5, 4];

    [TestMethod]
    public void TheNextStopIsAfterTheOneTheSongIsAt()
    {
        Assert.AreEqual(0, ReductionPreviewViewModel.StopAfter(Stops, 0));
        Assert.AreEqual(1, ReductionPreviewViewModel.StopAfter(Stops, 1), "On a stop, the next is the one after it.");
        Assert.AreEqual(1, ReductionPreviewViewModel.StopAfter(Stops, 1.001), "Just past a stop, it doesn't stick on it.");
        Assert.AreEqual(-1, ReductionPreviewViewModel.StopAfter(Stops, 4));
    }

    [TestMethod]
    public void TheStopBeforeIsBeforeTheOneTheSongIsAt()
    {
        Assert.AreEqual(2, ReductionPreviewViewModel.StopBefore(Stops, 5));
        Assert.AreEqual(0, ReductionPreviewViewModel.StopBefore(Stops, 2.5), "On a stop, the one before is the one before it.");
        Assert.AreEqual(-1, ReductionPreviewViewModel.StopBefore(Stops, 1));
        Assert.AreEqual(-1, ReductionPreviewViewModel.StopBefore([], 1));
    }

    [TestMethod]
    public void EachFilterShowsItsKindsOfChange()
    {
        Assert.AreEqual(ReductionChanges.Removed | ReductionChanges.DoubleKickDropped, ReductionPreviewViewModel.MaskOf(ReductionChangeCategories.Removals));
        Assert.AreEqual(ReductionChanges.Reshaped | ReductionChanges.Added, ReductionPreviewViewModel.MaskOf(ReductionChangeCategories.Reshapes));
        Assert.AreEqual(ReductionChanges.SustainTrimmed, ReductionPreviewViewModel.MaskOf(ReductionChangeCategories.Sustains));
        Assert.AreEqual(ReductionChanges.HopoFlipped | ReductionChanges.ForcingChanged, ReductionPreviewViewModel.MaskOf(ReductionChangeCategories.Forcing));
        Assert.AreEqual(ReductionChanges.None, ReductionPreviewViewModel.MaskOf(ReductionChangeCategories.Findings));
        Assert.AreEqual(ReductionChanges.None, ReductionPreviewViewModel.MaskOf(ReductionChangeCategories.Default) & ReductionChanges.Removed, "Removals aren't shown at first.");
    }

    [TestMethod]
    public void ADifficultyThatIsADraftAndASourceIsMarkedAsBoth()
    {
        // Hard in Reduce all: drafted from Expert, and what Medium is drafted from.
        ReductionChanges[] asSource = [ReductionChanges.None, ReductionChanges.Removed, ReductionChanges.None];
        ReductionChanges[] asDraft = [ReductionChanges.Reshaped, ReductionChanges.Reshaped, ReductionChanges.None];

        var marks = ReductionPreviewViewModel.LaneMarks(3, asSource, asDraft, ReductionChanges.Removed | ReductionChanges.Reshaped);

        Assert.AreSequenceEqual([HighwayNoteMark.Flagged, HighwayNoteMark.Faded | HighwayNoteMark.Flagged, HighwayNoteMark.None], marks!.ToList());
    }

    [TestMethod]
    public void ChangesTheFiltersHideAreNotMarked()
    {
        ReductionChanges[] asSource = [ReductionChanges.Removed, ReductionChanges.None];

        Assert.IsNull(ReductionPreviewViewModel.LaneMarks(2, asSource, null, ReductionChanges.Reshaped));
        Assert.IsNull(ReductionPreviewViewModel.LaneMarks(2, null, null, ReductionChanges.Removed));
    }

    [TestMethod]
    public void AChangeIsMarkedAsWhatMattersMostAboutIt()
    {
        Assert.AreEqual(ChangeStripKind.Reshape, ReductionPreviewViewModel.StripKindOf(ReductionChanges.Reshaped | ReductionChanges.SustainTrimmed | ReductionChanges.HopoFlipped));
        Assert.AreEqual(ChangeStripKind.Forcing, ReductionPreviewViewModel.StripKindOf(ReductionChanges.HopoFlipped | ReductionChanges.Removed));
        Assert.AreEqual(ChangeStripKind.Sustain, ReductionPreviewViewModel.StripKindOf(ReductionChanges.SustainTrimmed));
        Assert.AreEqual(ChangeStripKind.Removal, ReductionPreviewViewModel.StripKindOf(ReductionChanges.DoubleKickDropped));
    }

    [TestMethod]
    public void APressGoesToTheNearestMarkOnItsRowWithinReach()
    {
        ChangeStripMark[] marks = [new(1, 0, ChangeStripKind.Reshape), new(1.05, 1, ChangeStripKind.Removal), new(1.2, 0, ChangeStripKind.Sustain)];

        Assert.AreEqual(0, ChangeStripView.MarkNear(marks, 0, 1.08, 0.1));
        Assert.AreEqual(2, ChangeStripView.MarkNear(marks, 0, 1.15, 0.1));
        Assert.AreEqual(1, ChangeStripView.MarkNear(marks, 1, 1, 0.1));
        Assert.AreEqual(-1, ChangeStripView.MarkNear(marks, 0, 1.5, 0.1));
        Assert.AreEqual(-1, ChangeStripView.MarkNear(marks, 2, 1, 0.1));
    }
}
