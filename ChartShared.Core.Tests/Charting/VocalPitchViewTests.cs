using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Editing;

namespace ChartShared.Core.Tests.Charting;

/// <summary>The window of pitches a vocal lane's piano roll shows, which its keyboard scrolls and zooms.</summary>
[TestClass]
public sealed class VocalPitchViewTests
{
    [TestMethod]
    public void TheFullViewShowsEveryPitchAPartSings()
    {
        var view = VocalPitchView.Full;

        Assert.AreEqual(VocalTrackContent.HighestPitch, view.Top);
        Assert.AreEqual(VocalTrackContent.LowestPitch, view.Bottom);
        Assert.IsTrue(view.IsFull);
        Assert.AreSequenceEqual([.. Enumerable.Range(36, 49).Reverse()], [.. view.Slots.Slots]);
    }

    [TestMethod]
    public void AViewIsKeptWithinTheRangeAndNoNarrowerThanAnOctave()
    {
        Assert.AreEqual(VocalPitchView.Full, VocalPitchView.Of(200, 500));
        Assert.AreEqual(new VocalPitchView(47, VocalPitchView.MinSpan), VocalPitchView.Of(0, 1));
        Assert.AreEqual(new VocalPitchView(84, 12), VocalPitchView.Of(90, 12));
    }

    [TestMethod]
    public void ARowIsReadBackAsItsPitch()
    {
        var view = new VocalPitchView(72, 24);

        Assert.AreEqual(0, view.RowOf(72));
        Assert.AreEqual(23, view.RowOf(49));
        Assert.AreEqual(60, view.PitchAt(view.RowOf(60)));
        Assert.IsTrue(view.Contains(49));
        Assert.IsFalse(view.Contains(48));
        Assert.IsFalse(view.Contains(73));
    }

    [TestMethod]
    public void ScrollingMovesTheViewAndStopsAtTheEnds()
    {
        var view = new VocalPitchView(72, 24);

        Assert.AreEqual(new VocalPitchView(74, 24), view.Scrolled(2));
        Assert.AreEqual(new VocalPitchView(70, 24), view.Scrolled(-2));
        Assert.AreEqual(new VocalPitchView(84, 24), view.Scrolled(100));
        Assert.AreEqual(new VocalPitchView(59, 24), view.Scrolled(-100));
    }

    [TestMethod]
    public void ZoomingInNarrowsTheViewAroundThePitchUnderThePointer()
    {
        var view = VocalPitchView.Full;

        var zoomed = view.Zoomed(2, 60);

        Assert.AreEqual(view.Span - (2 * VocalPitchView.ZoomStep), zoomed.Span);
        Assert.IsTrue(zoomed.Contains(60), "The pitch zoomed on stays in view.");

        // It stays the same way down the view: the row it's on moves by no more than the rounding.
        double before = (double)view.RowOf(60) / view.Span, after = (double)zoomed.RowOf(60) / zoomed.Span;
        Assert.IsLessThan(1.0 / zoomed.Span, Math.Abs(before - after));
    }

    [TestMethod]
    public void ZoomingStopsAtAnOctaveAndAtEveryPitch()
    {
        Assert.AreEqual(VocalPitchView.MinSpan, VocalPitchView.Full.Zoomed(100, 60).Span);
        Assert.AreEqual(VocalPitchView.Full, VocalPitchView.Full.Zoomed(-1, 60));
        Assert.AreEqual(VocalPitchView.Full, new VocalPitchView(60, 12).Zoomed(-100, 60));
    }

    [TestMethod]
    public void ZoomingOutKeepsTheViewInRange()
    {
        var view = new VocalPitchView(84, 12);

        var zoomed = view.Zoomed(-3, 84);

        Assert.IsLessThanOrEqualTo(VocalTrackContent.HighestPitch, zoomed.Top);
        Assert.IsGreaterThanOrEqualTo(VocalTrackContent.LowestPitch, zoomed.Bottom);
        Assert.AreEqual(24, zoomed.Span);
    }

    [TestMethod]
    public void RevealingScrollsTheLeastThatShowsThePitchAndLeavesAShownOneAlone()
    {
        var view = new VocalPitchView(72, 24);

        Assert.AreEqual(view, view.Revealing(60));
        Assert.IsTrue(view.Revealing(80).Contains(80));
        Assert.AreEqual(view.Span, view.Revealing(80).Span);
        Assert.IsTrue(view.Revealing(40).Contains(40));
        Assert.AreEqual(view.Span, view.Revealing(40).Span);
    }

    [TestMethod]
    public void HiddenCountsTheNotesAboveAndBelowTheView()
    {
        var view = new VocalPitchView(72, 24);

        var (above, below) = view.Hidden([73, 84, 72, 60, 49, 48, 36, 36]);

        Assert.AreEqual(2, above);
        Assert.AreEqual(3, below);
    }

    [TestMethod]
    public void AViewSavedWithoutBothNumbersIsTheFullOne()
    {
        Assert.AreEqual(VocalPitchView.Full, VocalPitchView.FromSaved(null, 24));
        Assert.AreEqual(VocalPitchView.Full, VocalPitchView.FromSaved(72, null));
        Assert.AreEqual(new VocalPitchView(72, 24), VocalPitchView.FromSaved(72, 24));
    }

    [TestMethod]
    public void TheSlotsOfAViewAreMadeOnce()
    {
        var view = new VocalPitchView(72, 24);

        Assert.AreSame(view.Slots, new VocalPitchView(72, 24).Slots);
    }
}
