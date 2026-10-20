using ChartBuilder.Controls;
using ChartBuilder.Services;
using ChartBuilder.ViewModels;
using ChartShared.Core.Charting.Editing;
using ChartShared.UI.Controls.Highway;

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
        Assert.AreEqual(0, ReducePreviewViewModel.StopAfter(Stops, 0));
        Assert.AreEqual(1, ReducePreviewViewModel.StopAfter(Stops, 1), "On a stop, the next is the one after it.");
        Assert.AreEqual(1, ReducePreviewViewModel.StopAfter(Stops, 1.001), "Just past a stop, it doesn't stick on it.");
        Assert.AreEqual(-1, ReducePreviewViewModel.StopAfter(Stops, 4));
    }

    [TestMethod]
    public void TheStopBeforeIsBeforeTheOneTheSongIsAt()
    {
        Assert.AreEqual(2, ReducePreviewViewModel.StopBefore(Stops, 5));
        Assert.AreEqual(0, ReducePreviewViewModel.StopBefore(Stops, 2.5), "On a stop, the one before is the one before it.");
        Assert.AreEqual(-1, ReducePreviewViewModel.StopBefore(Stops, 1));
        Assert.AreEqual(-1, ReducePreviewViewModel.StopBefore([], 1));
    }

    [TestMethod]
    public void EachFilterShowsItsKindsOfChange()
    {
        Assert.AreEqual(ReductionChanges.Removed | ReductionChanges.DoubleKickDropped, ReducePreviewViewModel.MaskOf(ReduceChangeCategories.Removals));
        Assert.AreEqual(ReductionChanges.Reshaped | ReductionChanges.Added, ReducePreviewViewModel.MaskOf(ReduceChangeCategories.Reshapes));
        Assert.AreEqual(ReductionChanges.SustainTrimmed, ReducePreviewViewModel.MaskOf(ReduceChangeCategories.Sustains));
        Assert.AreEqual(ReductionChanges.HopoFlipped | ReductionChanges.ForcingChanged, ReducePreviewViewModel.MaskOf(ReduceChangeCategories.Forcing));
        Assert.AreEqual(ReductionChanges.None, ReducePreviewViewModel.MaskOf(ReduceChangeCategories.Findings));
        Assert.AreEqual(ReductionChanges.None, ReducePreviewViewModel.MaskOf(ReduceChangeCategories.Default) & ReductionChanges.Removed, "Removals aren't shown at first.");
    }

    [TestMethod]
    public void ADifficultyThatIsADraftAndASourceIsMarkedAsBoth()
    {
        // Hard in Reduce all: drafted from Expert, and what Medium is drafted from.
        ReductionChanges[] asSource = [ReductionChanges.None, ReductionChanges.Removed, ReductionChanges.None];
        ReductionChanges[] asDraft = [ReductionChanges.Reshaped, ReductionChanges.Reshaped, ReductionChanges.None];

        var marks = ReducePreviewViewModel.LaneMarks(3, asSource, asDraft, ReductionChanges.Removed | ReductionChanges.Reshaped);

        Assert.AreSequenceEqual([HighwayNoteMark.Flagged, HighwayNoteMark.Faded | HighwayNoteMark.Flagged, HighwayNoteMark.None], marks!.ToList());
    }

    [TestMethod]
    public void ChangesTheFiltersHideAreNotMarked()
    {
        ReductionChanges[] asSource = [ReductionChanges.Removed, ReductionChanges.None];

        Assert.IsNull(ReducePreviewViewModel.LaneMarks(2, asSource, null, ReductionChanges.Reshaped));
        Assert.IsNull(ReducePreviewViewModel.LaneMarks(2, null, null, ReductionChanges.Removed));
    }

    [TestMethod]
    public void AChangeIsMarkedAsWhatMattersMostAboutIt()
    {
        Assert.AreEqual(ChangeStripKind.Reshape, ReducePreviewViewModel.StripKindOf(ReductionChanges.Reshaped | ReductionChanges.SustainTrimmed | ReductionChanges.HopoFlipped));
        Assert.AreEqual(ChangeStripKind.Forcing, ReducePreviewViewModel.StripKindOf(ReductionChanges.HopoFlipped | ReductionChanges.Removed));
        Assert.AreEqual(ChangeStripKind.Sustain, ReducePreviewViewModel.StripKindOf(ReductionChanges.SustainTrimmed));
        Assert.AreEqual(ChangeStripKind.Removal, ReducePreviewViewModel.StripKindOf(ReductionChanges.DoubleKickDropped));
    }

    [TestMethod]
    public void TheLookAheadIsInHalfSecondStepsFromHalfASecondToTen()
    {
        Assert.AreEqual(1.5, BuilderSettings.SnapReducePreviewLookAhead(1.4));
        Assert.AreEqual(8.0, BuilderSettings.SnapReducePreviewLookAhead(8.2));
        Assert.AreEqual(0.5, BuilderSettings.SnapReducePreviewLookAhead(0));
        Assert.AreEqual(10.0, BuilderSettings.SnapReducePreviewLookAhead(30));
        Assert.AreEqual(1.5, BuilderSettings.SnapReducePreviewLookAhead(double.NaN), "A setting that isn't a number is the default.");
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
