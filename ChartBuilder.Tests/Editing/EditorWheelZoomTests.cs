using ChartBuilder.Controls.Editing;

namespace ChartBuilder.Tests.Editing;

/// <summary>Ctrl+wheel zoom in the editor lanes: half a second per notch, at most one step per wheel event.</summary>
[TestClass]
public sealed class EditorWheelZoomTests
{
    [TestMethod]
    public void OneNotchStepsEachWay()
    {
        double carry = 0;
        Assert.AreEqual(1, EditorCanvas.TakeZoomStep(ref carry, 1));
        Assert.AreEqual(0, carry, 1e-9);
        Assert.AreEqual(-1, EditorCanvas.TakeZoomStep(ref carry, -1));
        Assert.AreEqual(0, carry, 1e-9);
    }

    [TestMethod]
    public void AMouseThatReportsSeveralUnitsPerNotchStillStepsOnce()
    {
        double carry = 0;
        Assert.AreEqual(1, EditorCanvas.TakeZoomStep(ref carry, 3));
        Assert.AreEqual(0, carry, 1e-9, "Whole notches are consumed.");
        Assert.AreEqual(-1, EditorCanvas.TakeZoomStep(ref carry, -4));
        Assert.AreEqual(0, carry, 1e-9);
    }

    [TestMethod]
    public void FractionalTouchpadTurnsAddUpToSteps()
    {
        double carry = 0;
        Assert.AreEqual(0, EditorCanvas.TakeZoomStep(ref carry, 0.4));
        Assert.AreEqual(0, EditorCanvas.TakeZoomStep(ref carry, 0.4));
        Assert.AreEqual(1, EditorCanvas.TakeZoomStep(ref carry, 0.4));
        Assert.AreEqual(0.2, carry, 1e-9);
    }

    [TestMethod]
    public void TurningBackStartsOver()
    {
        double carry = 0.6;
        Assert.AreEqual(-1, EditorCanvas.TakeZoomStep(ref carry, -1));
        Assert.AreEqual(0, carry, 1e-9);
    }

    [TestMethod]
    public void NoTurnTakesNoStep()
    {
        double carry = 0;
        Assert.AreEqual(0, EditorCanvas.TakeZoomStep(ref carry, 0));
    }
}
