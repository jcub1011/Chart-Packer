using ChartShared.Core.Input;

namespace ChartBuilder.Tests.InstrumentInput;

/// <summary>Bind taking the next control pressed (charter-spec C19 item 2).</summary>
[TestClass]
public sealed class BindingDetectorTests
{
    private static readonly InputDeviceId Pad = new(InputDeviceKind.Controller, "pad");
    private static readonly InputDeviceId Kit = new(InputDeviceKind.Midi, "kit");

    [TestMethod]
    public void NothingIsTakenInItsFirstMomentAndAControlPressedThenOnlyOnceItsPressedAgain()
    {
        var detector = new BindingDetector(10, new Dictionary<(InputDeviceId, InputControl), float>());

        Assert.IsNull(detector.Feed(Button(3, 1, 10.05)));
        Assert.IsNull(detector.Feed(Button(3, 1, 10.3)));
        Assert.IsNull(detector.Feed(Button(3, 0, 10.4)));

        Assert.AreEqual(new DetectedControl(Pad, new InputControl(InputControlKind.Button, 3), 0), detector.Feed(Button(3, 1, 10.5)));
    }

    [TestMethod]
    public void AControlHeldWhenItStartsIsTakenOnlyOnceLetGoAndPressedAgain()
    {
        var held = new Dictionary<(InputDeviceId, InputControl), float> { [(Pad, new InputControl(InputControlKind.Button, 1))] = 1 };
        var detector = new BindingDetector(10, held);

        Assert.IsNull(detector.Feed(Button(1, 1, 11)));
        Assert.IsNull(detector.Feed(Button(1, 0, 11.1)));
        Assert.IsNotNull(detector.Feed(Button(1, 1, 11.2)));
    }

    [TestMethod]
    public void AnAxisIsTakenWhenItMovesHalfWayFromWhereItRestsTowardsWhereItWent()
    {
        var resting = new Dictionary<(InputDeviceId, InputControl), float> { [(Pad, new InputControl(InputControlKind.Axis, 2))] = -1 };
        var detector = new BindingDetector(10, resting);

        Assert.IsNull(detector.Feed(Axis(2, -0.6f, 11)));
        var found = detector.Feed(Axis(2, -0.4f, 11.1));

        Assert.AreEqual(new DetectedControl(Pad, new InputControl(InputControlKind.Axis, 2, 1), -1), found);
    }

    [TestMethod]
    public void AHatIsTakenInTheDirectionPushed()
    {
        var detector = new BindingDetector(10, new Dictionary<(InputDeviceId, InputControl), float>());

        var found = detector.Feed(new RawInputEvent(Pad, new InputControl(InputControlKind.Hat, 0), HatDirections.Down, 11));

        Assert.AreEqual(new InputControl(InputControlKind.Hat, 0, HatDirections.Down), found!.Value.Control);
    }

    [TestMethod]
    public void AMidiNoteSofterThanTheFloorIsntTaken()
    {
        var detector = new BindingDetector(10, new Dictionary<(InputDeviceId, InputControl), float>(), minVelocity: 30);

        Assert.IsNull(detector.Feed(Note(38, 10, 11)));
        Assert.AreEqual(new InputControl(InputControlKind.MidiNote, 38), detector.Feed(Note(38, 90, 11.1))!.Value.Control);
    }

    private static RawInputEvent Button(int index, float value, double time) => new(Pad, new InputControl(InputControlKind.Button, index), value, time);

    private static RawInputEvent Axis(int index, float value, double time) => new(Pad, new InputControl(InputControlKind.Axis, index), value, time);

    private static RawInputEvent Note(int note, int velocity, double time) => new(Kit, new InputControl(InputControlKind.MidiNote, note), velocity / 127f, time);
}
