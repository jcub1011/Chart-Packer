using ChartShared.UI.Controls;

namespace ChartBuilder.Tests.Editing;

/// <summary>Dragging a number box sideways: the value moves by whole steps of the step it's given.</summary>
[TestClass]
public sealed class DragScrubTests
{
    [TestMethod]
    public void AStepOfATenthGivesTenths()
    {
        Assert.AreEqual(120.3m, ScrubMath.DraggedValue(120m, 0.3, 0.1));
        Assert.AreEqual(119.7m, ScrubMath.DraggedValue(120m, -0.3, 0.1));
        Assert.AreEqual(120m, ScrubMath.DraggedValue(120m, 0.04, 0.1), "Less than half a step moves nothing.");
    }

    [TestMethod]
    public void TheWholeStepsAreCountedFromWhereTheDragBegan()
    {
        Assert.AreEqual(120.5m, ScrubMath.DraggedValue(120.4m, 0.1, 0.1), "A value off the step keeps its offset.");
        Assert.AreEqual(123m, ScrubMath.DraggedValue(120m, 3, 1), "A step of one still moves whole numbers.");
        Assert.AreEqual(120.5m, ScrubMath.DraggedValue(120m, 0.5, 0.5));
    }
}
