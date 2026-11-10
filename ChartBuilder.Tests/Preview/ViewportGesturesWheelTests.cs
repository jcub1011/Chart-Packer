using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using ChartShared.UI.Controls;
using ChartShared.UI.Controls.Highway;

namespace ChartBuilder.Tests.Preview;

/// <summary>The wheel on the shared gestures, which both the editor's lanes and Chart Packer's highway scroll and zoom with.</summary>
[TestClass]
public sealed class ViewportGesturesWheelTests
{
    private sealed class Host : IViewportGestureHost
    {
        public List<int> Scrolled { get; } = [];

        public List<int> Zoomed { get; } = [];

        public double Now => 0;

        public double SecondsPerPixel => 1;

        public bool IsTimeAxisVertical => true;

        public bool WheelUpIsLater { get; init; } = true;

        public double FineFactor => 1;

        public void ScrubStarted() { }

        public void ScrubTo(double seconds) { }

        public void ScrubEnded() { }

        public void Scroll(int steps) => Scrolled.Add(steps);

        public void Zoom(int steps) => Zoomed.Add(steps);

        public double? OverviewTimeAt(Point at) => null;
    }

    private static ViewportGestures Gestures(Host host) =>
        new(new Border(), host, new ScrubBinding(MouseButton.Left), () => false);

    [TestMethod]
    public void ANotchScrollsOneStepTowardLaterWhenTurnedUpAndEarlierWhenTurnedDown()
    {
        var host = new Host();
        var gestures = Gestures(host);

        Assert.IsTrue(gestures.Wheel(new Vector(0, 1), KeyModifiers.None));
        Assert.IsTrue(gestures.Wheel(new Vector(0, -1), KeyModifiers.None));

        CollectionAssert.AreEqual(new[] { 1, -1 }, host.Scrolled);
        Assert.IsEmpty(host.Zoomed);
    }

    [TestMethod]
    public void AHighResolutionTurnStillMovesASingleStep()
    {
        var host = new Host();
        var gestures = Gestures(host);

        gestures.Wheel(new Vector(0, 3), KeyModifiers.None);

        CollectionAssert.AreEqual(new[] { 1 }, host.Scrolled);
    }

    [TestMethod]
    public void PartsOfANotchAddUpAcrossEvents()
    {
        var host = new Host();
        var gestures = Gestures(host);

        gestures.Wheel(new Vector(0, 0.4), KeyModifiers.None);
        gestures.Wheel(new Vector(0, 0.4), KeyModifiers.None);
        Assert.IsEmpty(host.Scrolled);

        gestures.Wheel(new Vector(0, 0.4), KeyModifiers.None);
        CollectionAssert.AreEqual(new[] { 1 }, host.Scrolled);
    }

    [TestMethod]
    public void ShiftScrollsFourTimesAsFarAndAcceptsASidewaysTurn()
    {
        var host = new Host();
        var gestures = Gestures(host);

        gestures.Wheel(new Vector(0, 1), KeyModifiers.Shift);
        gestures.Wheel(new Vector(-1, 0), KeyModifiers.Shift);

        CollectionAssert.AreEqual(new[] { WheelSteps.FastScrollSteps, -WheelSteps.FastScrollSteps }, host.Scrolled);
    }

    [TestMethod]
    public void AWheelThatScrollsEarlierWhenTurnedUpScrollsTheOtherWay()
    {
        var host = new Host { WheelUpIsLater = false };
        var gestures = Gestures(host);

        gestures.Wheel(new Vector(0, 1), KeyModifiers.None);

        CollectionAssert.AreEqual(new[] { -1 }, host.Scrolled);
    }

    [TestMethod]
    public void CtrlZoomsInsteadOfScrolling()
    {
        var host = new Host();
        var gestures = Gestures(host);

        gestures.Wheel(new Vector(0, 1), KeyModifiers.Control);
        gestures.Wheel(new Vector(0, -1), KeyModifiers.Control);

        CollectionAssert.AreEqual(new[] { 1, -1 }, host.Zoomed);
        Assert.IsEmpty(host.Scrolled);
    }

    [TestMethod]
    public void ATurnOfNoneIsNotTakenSoItReachesWhatIsAbove()
    {
        var host = new Host();
        var gestures = Gestures(host);

        Assert.IsFalse(gestures.Wheel(new Vector(0, 0), KeyModifiers.None));
        Assert.IsFalse(gestures.Wheel(new Vector(1, 0), KeyModifiers.None), "A sideways turn without Shift isn't a scroll.");
        Assert.IsFalse(gestures.Wheel(new Vector(0, 0), KeyModifiers.Control));
        Assert.IsEmpty(host.Scrolled);
        Assert.IsEmpty(host.Zoomed);
    }
}
