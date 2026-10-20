using ChartBuilder.Controls;
using ChartBuilder.Controls.Editing;
using ChartBuilder.Services;
using ChartShared.UI.Controls;

namespace ChartBuilder.Tests.Editing;

/// <summary>Wheel scrolling in the editor lanes: a grid step a notch, four while Shift is held, at most one notch per wheel event.</summary>
[TestClass]
public sealed class EditorWheelScrollTests
{
    [TestMethod]
    public void OneNotchStepsOneGridStepEachWay()
    {
        double carry = 0;
        Assert.AreEqual(1, WheelSteps.TakeScrollStep(ref carry, 1, fast: false));
        Assert.AreEqual(0, carry, 1e-9);
        Assert.AreEqual(-1, WheelSteps.TakeScrollStep(ref carry, -1, fast: false));
        Assert.AreEqual(0, carry, 1e-9);
    }

    [TestMethod]
    public void ShiftNotchStepsFourGridStepsEachWay()
    {
        double carry = 0;
        Assert.AreEqual(4, WheelSteps.TakeScrollStep(ref carry, 1, fast: true));
        Assert.AreEqual(0, carry, 1e-9);
        Assert.AreEqual(-4, WheelSteps.TakeScrollStep(ref carry, -1, fast: true));
        Assert.AreEqual(0, carry, 1e-9);
    }

    [TestMethod]
    public void AMouseThatReportsSeveralUnitsPerNotchStillStepsOnce()
    {
        double carry = 0;
        Assert.AreEqual(1, WheelSteps.TakeScrollStep(ref carry, 3, fast: false));
        Assert.AreEqual(0, carry, 1e-9, "Whole notches are consumed.");
        Assert.AreEqual(-4, WheelSteps.TakeScrollStep(ref carry, -4, fast: true));
        Assert.AreEqual(0, carry, 1e-9, "Shift multiplies the single notch taken, not the raw turn.");
    }

    [TestMethod]
    public void FractionalTouchpadTurnsAddUpToSteps()
    {
        double carry = 0;
        Assert.AreEqual(0, WheelSteps.TakeScrollStep(ref carry, 0.4, fast: false));
        Assert.AreEqual(0, WheelSteps.TakeScrollStep(ref carry, 0.4, fast: false));
        Assert.AreEqual(1, WheelSteps.TakeScrollStep(ref carry, 0.4, fast: false));
        Assert.AreEqual(0.2, carry, 1e-9);

        carry = 0;
        Assert.AreEqual(0, WheelSteps.TakeScrollStep(ref carry, 0.4, fast: true));
        Assert.AreEqual(0, WheelSteps.TakeScrollStep(ref carry, 0.4, fast: true));
        Assert.AreEqual(4, WheelSteps.TakeScrollStep(ref carry, 0.4, fast: true));
        Assert.AreEqual(0.2, carry, 1e-9);
    }

    [TestMethod]
    public void TurningBackStartsOver()
    {
        double carry = 0.6;
        Assert.AreEqual(-1, WheelSteps.TakeScrollStep(ref carry, -1, fast: false));
        Assert.AreEqual(0, carry, 1e-9);

        carry = 0.6;
        Assert.AreEqual(-4, WheelSteps.TakeScrollStep(ref carry, -1, fast: true));
        Assert.AreEqual(0, carry, 1e-9);
    }

    [TestMethod]
    public void WheelUpIsLaterKeepsTheTurn()
    {
        Assert.AreEqual(1, WheelSteps.TowardLater(1, wheelUpIsLater: true));
        Assert.AreEqual(-1, WheelSteps.TowardLater(-1, wheelUpIsLater: true));
    }

    [TestMethod]
    public void TheWheelTurnedUpIsLaterByDefault()
    {
        Assert.AreEqual(WheelScrollDirection.UpIsLater, new BuilderSettings().WheelScrollDirection);
        Assert.IsTrue(EditorCanvas.WheelUpIsLaterProperty.GetDefaultValue(typeof(HighwayEditorCanvas)), "Along the lanes, until bound.");
        Assert.IsTrue(EditorCanvas.WheelUpIsLaterProperty.GetDefaultValue(typeof(TimelineEditorCanvas)), "Along the lanes, until bound.");
        Assert.IsTrue(TimelineView.WheelUpIsLaterProperty.GetDefaultValue(typeof(TimelineView)), "On the timeline, until bound.");
    }

    [TestMethod]
    public void FlippedWheelDownIsLater()
    {
        Assert.AreEqual(-1, WheelSteps.TowardLater(1, wheelUpIsLater: false));
        Assert.AreEqual(1, WheelSteps.TowardLater(-1, wheelUpIsLater: false));

        double carry = 0;
        Assert.AreEqual(4, WheelSteps.TakeScrollStep(ref carry, WheelSteps.TowardLater(-1, wheelUpIsLater: false), fast: true));
    }

    [TestMethod]
    public void NoTurnTakesNoStep()
    {
        double carry = 0;
        Assert.AreEqual(0, WheelSteps.TakeScrollStep(ref carry, 0, fast: false));
        Assert.AreEqual(0, WheelSteps.TakeScrollStep(ref carry, 0, fast: true));
    }
}
