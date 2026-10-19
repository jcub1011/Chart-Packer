using ChartShared.Core.Input;

namespace ChartBuilder.Tests.InstrumentInput;

/// <summary>What a control's value means to a binding: whether it's pressed, and how far it's moved (charter-spec C19 item 2).</summary>
[TestClass]
public sealed class ControlReadingTests
{
    [TestMethod]
    public void AnAxisRestingAtOneEndTravelsTheWholeRangeToTheOther()
    {
        Assert.AreEqual(0, ControlReading.Travel(InputControlKind.Axis, 1, -1, -1), 1e-6);
        Assert.AreEqual(0.5, ControlReading.Travel(InputControlKind.Axis, 1, -1, 0), 1e-6);
        Assert.AreEqual(1, ControlReading.Travel(InputControlKind.Axis, 1, -1, 1), 1e-6);
    }

    [TestMethod]
    public void AnAxisBoundTowardsItsLowEndTravelsDownFromItsRest()
    {
        Assert.AreEqual(1, ControlReading.Travel(InputControlKind.Axis, -1, 0, -1), 1e-6);
        Assert.AreEqual(0.25, ControlReading.Travel(InputControlKind.Axis, -1, 0, -0.25f), 1e-6);
    }

    [TestMethod]
    public void AMidiControlTravelsFromItsRestToTheTopOfItsRange()
    {
        Assert.AreEqual(0.5, ControlReading.Travel(InputControlKind.MidiControl, 1, 0, 0.5f), 1e-6);
        Assert.AreEqual(1, ControlReading.Travel(InputControlKind.MidiControl, 1, 0, 1), 1e-6);
    }

    [TestMethod]
    public void AnAxisBehindItsRestHasNotTravelled()
    {
        Assert.AreEqual(0, ControlReading.Travel(InputControlKind.Axis, 1, 0, -0.5f), 1e-6);
    }

    [TestMethod]
    public void AnAxisRestingAtTheEndItsBoundTowardsCantTravel()
    {
        Assert.AreEqual(0, ControlReading.Travel(InputControlKind.Axis, 1, 1, 1), 1e-6);
    }

    [TestMethod]
    public void OnlyAnAxisOrMidiControlTravels()
    {
        Assert.AreEqual(0, ControlReading.Travel(InputControlKind.Button, 0, 0, 1), 1e-6);
        Assert.AreEqual(0, ControlReading.Travel(InputControlKind.MidiNote, 0, 0, 1), 1e-6);
    }

    [TestMethod]
    public void AnAxisIsPressedPastItsThresholdAndLetGoOnlyInsideTheHysteresis()
    {
        // Rest −1, threshold 0.5: pressed at 0.5 from rest; once pressed, until it's back inside 0.35.
        Assert.IsFalse(ControlReading.IsPressed(InputControlKind.Axis, 1, -1, 0.5f, -0.6f, wasDown: false));
        Assert.IsTrue(ControlReading.IsPressed(InputControlKind.Axis, 1, -1, 0.5f, -0.5f, wasDown: false));
        Assert.IsTrue(ControlReading.IsPressed(InputControlKind.Axis, 1, -1, 0.5f, -0.6f, wasDown: true));
        Assert.IsFalse(ControlReading.IsPressed(InputControlKind.Axis, 1, -1, 0.5f, -0.7f, wasDown: true));
    }

    [TestMethod]
    public void AHatIsPressedInItsDirectionOnly()
    {
        Assert.IsTrue(ControlReading.IsPressed(InputControlKind.Hat, HatDirections.Up, 0, 0.5f, HatDirections.Up | HatDirections.Right, wasDown: false));
        Assert.IsFalse(ControlReading.IsPressed(InputControlKind.Hat, HatDirections.Up, 0, 0.5f, HatDirections.Down, wasDown: false));
    }

    [TestMethod]
    public void AButtonIsPressedFromHalfway()
    {
        Assert.IsTrue(ControlReading.IsPressed(InputControlKind.Button, 0, 0, 0.5f, 1, wasDown: false));
        Assert.IsFalse(ControlReading.IsPressed(InputControlKind.Button, 0, 0, 0.5f, 0, wasDown: true));
    }

    [TestMethod]
    public void ABindingIsFoundOnItsDeviceByNameWhenItsKeyChanged()
    {
        var binding = InputBinding.Of(InstrumentAction.Green, new InputDeviceInfo(new InputDeviceId(InputDeviceKind.Controller, "old-guid"), "Guitar"),
            new InputControl(InputControlKind.Button, 0), key: null, rest: 0);
        var now = new InputDeviceInfo(new InputDeviceId(InputDeviceKind.Controller, "new-guid"), "Guitar");

        Assert.AreEqual(now.Id, InputRouter.DeviceOf(binding, [now]));
        Assert.IsNull(InputRouter.DeviceOf(binding, []));
    }
}
