using ChartPacker.Core.Input;

namespace ChartBuilder.Tests.InstrumentInput;

/// <summary>MIDI messages as inputs, timed on the input clock (charter-spec C19 items 1 and 3).</summary>
[TestClass]
public sealed class MidiInputTests
{
    private static readonly InputDeviceId Kit = new(InputDeviceKind.Midi, "Kit");

    [TestMethod]
    public void NotesAndControlChangesOnAnyChannelAreInputsAndTheRestIsnt()
    {
        var on = MidiInput.Decode(0x00_64_26_99, Kit, 1);
        var off = MidiInput.Decode(0x00_40_26_89, Kit, 1);
        var pedal = MidiInput.Decode(0x00_7F_04_B9, Kit, 1);

        Assert.AreEqual(new InputControl(InputControlKind.MidiNote, 38), on!.Value.Control);
        Assert.AreEqual(100, on.Value.Velocity);
        Assert.AreEqual(0, off!.Value.Value);
        Assert.AreEqual(new InputControl(InputControlKind.MidiControl, 4), pedal!.Value.Control);
        Assert.AreEqual(1f, pedal.Value.Value);
        Assert.IsNull(MidiInput.Decode(0xF8, Kit, 1));
        Assert.IsNull(MidiInput.Decode(0x00_40_00_E0, Kit, 1));
    }

    [TestMethod]
    public void AStampIsPlacedFromWhenThePortStarted()
    {
        var anchor = new MidiTimestampAnchor();
        anchor.Start(50);

        Assert.AreEqual(51.25, anchor.TimeOf(1250, arrived: 51.252), 1e-9);
    }

    [TestMethod]
    public void AStampAfterItArrivedIsHeldBackAndSoAreTheNext()
    {
        var anchor = new MidiTimestampAnchor();
        anchor.Start(50);

        Assert.AreEqual(51.0, anchor.TimeOf(1010, arrived: 51.0), 1e-9);
        Assert.AreEqual(51.49, anchor.TimeOf(1500, arrived: 51.495), 1e-9);
    }

    [TestMethod]
    public void AStampLongBeforeItArrivedIsBroughtUp()
    {
        var anchor = new MidiTimestampAnchor();
        anchor.Start(50);

        Assert.AreEqual(51.95, anchor.TimeOf(1000, arrived: 52.0), 1e-9);
        Assert.AreEqual(52.95, anchor.TimeOf(2000, arrived: 52.951), 1e-9);
    }
}
