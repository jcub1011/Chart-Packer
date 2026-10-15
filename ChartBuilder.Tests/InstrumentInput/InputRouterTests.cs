using ChartPacker.Core.Input;

namespace ChartBuilder.Tests.InstrumentInput;

/// <summary>Bindings turning what devices do into actions (charter-spec C19 item 2).</summary>
[TestClass]
public sealed class InputRouterTests
{
    private static readonly InputDeviceInfo Guitar = new(new InputDeviceId(InputDeviceKind.Controller, "guitar-guid"), "Guitar");
    private static readonly InputDeviceInfo Kit = new(new InputDeviceId(InputDeviceKind.Midi, "TD-17"), "TD-17");

    [TestMethod]
    public void AKeyPressesAndLetsGoOfItsActionAndARepeatIsPressedOnce()
    {
        var router = Router([InputBinding.OfKey(InstrumentAction.Green, "Digit1")]);

        var emitted = Feed(router, Key(1, 1, 0.5), Key(1, 1, 0.55), Key(1, 1, 0.6), Key(1, 0, 0.7));

        Assert.AreSequenceEqual([(InstrumentAction.Green, true, 0.5), (InstrumentAction.Green, false, 0.7)], emitted.Select(e => (e.Action, e.IsDown, e.Time)).ToList());
    }

    [TestMethod]
    public void AFretWithTwoControlsIsPressedOnceAndLetGoWithTheLast()
    {
        var router = Router([InputBinding.OfKey(InstrumentAction.Green, "Digit1"), Bind(InstrumentAction.Green, Guitar, new InputControl(InputControlKind.Button, 0))]);

        var emitted = Feed(router, Key(1, 1, 0.5), Button(0, 1, 0.6), Key(1, 0, 0.7), Button(0, 0, 0.8));

        Assert.AreSequenceEqual([(true, 0.5), (false, 0.8)], emitted.Select(e => (e.IsDown, e.Time)).ToList());
        Assert.IsFalse(router.IsDown(InstrumentAction.Green));
    }

    [TestMethod]
    public void TwoPedalsOnTheKickAreEachAHit()
    {
        var router = Router([InputBinding.OfKey(InstrumentAction.Kick, "Digit0"), InputBinding.OfKey(InstrumentAction.Kick, "Digit9")]);

        var emitted = Feed(router, Key(0, 1, 0.5), Key(9, 1, 0.6));

        Assert.AreEqual(2, emitted.Count(e => e.IsDown));
    }

    [TestMethod]
    public void AnAxisPressesPastItsThresholdFromWhereItRestsAndLetsGoInsideTheHysteresis()
    {
        var whammy = Bind(InstrumentAction.Whammy, Guitar, new InputControl(InputControlKind.Axis, 3, Direction: 1), rest: -1);
        var router = Router([whammy]);

        var emitted = Feed(router, Axis(3, -0.8f, 0.1), Axis(3, -0.4f, 0.2), Axis(3, -0.55f, 0.3), Axis(3, -0.7f, 0.4));

        // Down at 0.6 from rest; still down at 0.45 (above 0.35); up at 0.3.
        Assert.AreSequenceEqual([(true, 0.2), (false, 0.4)], emitted.Select(e => (e.IsDown, e.Time)).ToList());
    }

    [TestMethod]
    public void AHatPressesEachDirectionBound()
    {
        var router = Router(
        [
            Bind(InstrumentAction.StrumUp, Guitar, new InputControl(InputControlKind.Hat, 0, HatDirections.Up)),
            Bind(InstrumentAction.StrumDown, Guitar, new InputControl(InputControlKind.Hat, 0, HatDirections.Down)),
        ]);

        var emitted = Feed(router, Hat(HatDirections.Down, 0.1), Hat(0, 0.2), Hat(HatDirections.Up | HatDirections.Right, 0.3));

        Assert.AreSequenceEqual(
            [(InstrumentAction.StrumDown, true), (InstrumentAction.StrumDown, false), (InstrumentAction.StrumUp, true)],
            emitted.Select(e => (e.Action, e.IsDown)).ToList());
    }

    [TestMethod]
    public void AMidiNoteOnWithNoVelocityIsANoteOff()
    {
        var router = Router([Bind(InstrumentAction.RedPad, Kit, new InputControl(InputControlKind.MidiNote, 38))]);

        var emitted = Feed(router, Note(38, 100, 0.1), Note(38, 0, 0.15), Note(38, 100, 0.5));

        Assert.AreSequenceEqual([true, false, true], emitted.Select(e => e.IsDown).ToList());
    }

    [TestMethod]
    public void AMidiNoteStruckAgainBeforeItsNoteOffIsAnotherHit()
    {
        var router = Router([Bind(InstrumentAction.RedPad, Kit, new InputControl(InputControlKind.MidiNote, 38))]);

        var emitted = Feed(router, Note(38, 100, 0.1), Note(38, 100, 0.3));

        Assert.AreEqual(2, emitted.Count(e => e.IsDown));
    }

    [TestMethod]
    public void AMidiNoteBelowTheVelocityFloorOrRetriggeredIsIgnored()
    {
        var tuning = new DeviceTuning { Kind = InputDeviceKind.Midi, Key = Kit.Id.Key, MinVelocity = 20, RetriggerMs = 30 };
        var router = Router([Bind(InstrumentAction.RedPad, Kit, new InputControl(InputControlKind.MidiNote, 38))], [tuning]);

        var emitted = Feed(router, Note(38, 10, 0.1), Note(38, 100, 0.2), Note(38, 0, 0.21), Note(38, 90, 0.22), Note(38, 0, 0.23), Note(38, 90, 0.3));

        Assert.AreSequenceEqual([0.2, 0.3], emitted.Where(e => e.IsDown).Select(e => e.Time).ToList());
    }

    [TestMethod]
    public void AMidiNoteIsAnAccentOrAGhostByItsVelocity()
    {
        var router = Router([Bind(InstrumentAction.RedPad, Kit, new InputControl(InputControlKind.MidiNote, 38))]);

        var emitted = Feed(router, Note(38, 127, 0.1), Note(38, 80, 0.3), Note(38, 20, 0.5));

        Assert.AreSequenceEqual([HitStrength.Accent, HitStrength.Normal, HitStrength.Ghost], emitted.Where(e => e.IsDown).Select(e => e.Strength).ToList());
    }

    [TestMethod]
    public void TheDevicesOffsetIsTakenOffTheTime()
    {
        var tuning = new DeviceTuning { Kind = InputDeviceKind.Controller, Key = Guitar.Id.Key, OffsetMs = 20 };
        var router = Router([Bind(InstrumentAction.Green, Guitar, new InputControl(InputControlKind.Button, 0))], [tuning]);

        var emitted = Feed(router, Button(0, 1, 1.0));

        Assert.AreEqual(0.98, emitted.Single().Time, 1e-9);
    }

    [TestMethod]
    public void ABindingIsFoundByNameWhenItsDevicesKeyHasChangedAndNotAtAllWhenItsGone()
    {
        var binding = Bind(InstrumentAction.Green, Guitar, new InputControl(InputControlKind.Button, 0));
        var renamed = new InputDeviceInfo(new InputDeviceId(InputDeviceKind.Controller, "another-guid"), "Guitar");

        var found = new InputRouter([binding], [], [renamed], KeyIndex);
        var gone = new InputRouter([binding], [], [], KeyIndex);

        Assert.HasCount(1, Feed(found, new RawInputEvent(renamed.Id, new InputControl(InputControlKind.Button, 0), 1, 1)));
        Assert.IsEmpty(gone.Devices);
    }

    [TestMethod]
    public void ReleaseAllLetsGoOfEverythingHeld()
    {
        var router = Router([InputBinding.OfKey(InstrumentAction.Green, "Digit1"), Bind(InstrumentAction.Red, Guitar, new InputControl(InputControlKind.Button, 1))]);
        Feed(router, Key(1, 1, 0.1), Button(1, 1, 0.2));

        var emitted = new List<ActionEvent>();
        router.ReleaseAll(0.5, emitted);

        Assert.AreSequenceEqual([false, false], emitted.Select(e => e.IsDown).ToList());
        Assert.IsFalse(router.IsDown(InstrumentAction.Green));
    }

    private static InputRouter Router(List<InputBinding> bindings, List<DeviceTuning>? tunings = null) =>
        new(bindings, tunings ?? [], [Guitar, Kit], KeyIndex);

    // Keys by name as the app numbers them: DigitN is N.
    private static int? KeyIndex(string name) => name.StartsWith("Digit", StringComparison.Ordinal) ? int.Parse(name[5..]) : null;

    private static InputBinding Bind(InstrumentAction action, InputDeviceInfo device, InputControl control, float rest = 0) =>
        InputBinding.Of(action, device, control, rest: rest);

    private static List<ActionEvent> Feed(InputRouter router, params RawInputEvent[] inputs)
    {
        var emitted = new List<ActionEvent>();
        foreach (var input in inputs)
            router.Handle(input, emitted);
        return emitted;
    }

    private static RawInputEvent Key(int digit, float value, double time) => new(InputDeviceId.Keyboard, new InputControl(InputControlKind.Key, digit), value, time);

    private static RawInputEvent Button(int index, float value, double time) => new(Guitar.Id, new InputControl(InputControlKind.Button, index), value, time);

    private static RawInputEvent Axis(int index, float value, double time) => new(Guitar.Id, new InputControl(InputControlKind.Axis, index), value, time);

    private static RawInputEvent Hat(int value, double time) => new(Guitar.Id, new InputControl(InputControlKind.Hat, 0), value, time);

    private static RawInputEvent Note(int note, int velocity, double time) => new(Kit.Id, new InputControl(InputControlKind.MidiNote, note), velocity / 127f, time);
}
