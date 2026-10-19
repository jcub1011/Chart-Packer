using ChartShared.Core.Input;

namespace ChartBuilder.Tests.InstrumentInput;

/// <summary>The presses the calibration tool's input-device test counts (charter-spec C11 item 2).</summary>
[TestClass]
public sealed class PressDetectorTests
{
    private static readonly InputDeviceId Guitar = new(InputDeviceKind.Controller, "guitar");
    private static readonly InputDeviceId Kit = new(InputDeviceKind.Midi, "kit");

    [TestMethod]
    public void AButtonPressedIsOnePressAndLettingGoIsNone()
    {
        var detector = new PressDetector(Guitar);

        Assert.IsTrue(detector.Feed(Button(0, 1, 1.0)));
        Assert.IsFalse(detector.Feed(Button(0, 0, 1.1)));
        Assert.IsTrue(detector.Feed(Button(0, 1, 1.2)));
    }

    [TestMethod]
    public void AKeyRepeatingWhileHeldPressesOnce()
    {
        var detector = new PressDetector(InputDeviceId.Keyboard);
        var key = new InputControl(InputControlKind.Key, 32);

        Assert.IsTrue(detector.Feed(new RawInputEvent(InputDeviceId.Keyboard, key, 1, 1.0)));
        Assert.IsFalse(detector.Feed(new RawInputEvent(InputDeviceId.Keyboard, key, 1, 1.05)));
        Assert.IsFalse(detector.Feed(new RawInputEvent(InputDeviceId.Keyboard, key, 1, 1.1)));
    }

    [TestMethod]
    public void TwoButtonsHeldTogetherPressOnceEach()
    {
        var detector = new PressDetector(Guitar);

        Assert.IsTrue(detector.Feed(Button(0, 1, 1.0)));
        Assert.IsTrue(detector.Feed(Button(1, 1, 1.0)));
    }

    [TestMethod]
    public void AStrumOnTheHatPressesWhicheverWayItsPushed()
    {
        var detector = new PressDetector(Guitar);

        Assert.IsTrue(detector.Feed(Hat(HatDirections.Down, 1.0)));
        Assert.IsFalse(detector.Feed(Hat(HatDirections.Down | HatDirections.Left, 1.05)));
        Assert.IsFalse(detector.Feed(Hat(0, 1.1)));
        Assert.IsTrue(detector.Feed(Hat(HatDirections.Up, 1.2)));
    }

    [TestMethod]
    public void AMidiNotePressesAtTheDevicesSoftestVelocityAndEveryStrikeCounts()
    {
        var detector = new PressDetector(Kit, minVelocity: 20);

        Assert.IsFalse(detector.Feed(Note(38, 19, 1.0)));
        Assert.IsTrue(detector.Feed(Note(38, 20, 1.1)));
        Assert.IsTrue(detector.Feed(Note(38, 90, 1.2)));
        Assert.IsFalse(detector.Feed(Note(38, 0, 1.3)));
    }

    [TestMethod]
    public void AxesMidiControlsAndOtherDevicesNeverPress()
    {
        var detector = new PressDetector(Guitar);

        Assert.IsFalse(detector.Feed(new RawInputEvent(Guitar, new InputControl(InputControlKind.Axis, 2), 1, 1.0)));
        Assert.IsFalse(detector.Feed(new RawInputEvent(Guitar, new InputControl(InputControlKind.MidiControl, 4), 1, 1.0)));
        Assert.IsFalse(detector.Feed(new RawInputEvent(Kit, new InputControl(InputControlKind.Button, 0), 1, 1.0)));
        Assert.IsFalse(detector.Feed(new RawInputEvent(InputDeviceId.Keyboard, new InputControl(InputControlKind.Key, 1), 1, 1.0)));
    }

    private static RawInputEvent Button(int index, float value, double time) => new(Guitar, new InputControl(InputControlKind.Button, index), value, time);

    private static RawInputEvent Hat(int value, double time) => new(Guitar, new InputControl(InputControlKind.Hat, 0), value, time);

    private static RawInputEvent Note(int note, int velocity, double time) => new(Kit, new InputControl(InputControlKind.MidiNote, note), velocity / 127f, time);
}
