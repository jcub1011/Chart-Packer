using ChartShared.Core.Charting;
using ChartShared.Core.Charts;
using ChartShared.Core.Input;

namespace ChartBuilder.Tests.InstrumentInput;

/// <summary>Where a kit's pads land on each drum layout (charter-spec C19 item 6, §7.6).</summary>
[TestClass]
public sealed class DrumPadsTests
{
    [TestMethod]
    public void OnProDrumsEachColoursTomAndCymbalLandOnItsLaneAndOrangeIsAGreenCymbal()
    {
        Assert.AreEqual(new DrumPad(ChartNote.OpenLane, false), DrumPads.Of(InstrumentAction.Kick, DrumsVariant.Pro));
        Assert.AreEqual(new DrumPad(0, false), DrumPads.Of(InstrumentAction.RedPad, DrumsVariant.Pro));
        Assert.AreEqual(new DrumPad(1, true), DrumPads.Of(InstrumentAction.YellowCymbal, DrumsVariant.Pro));
        Assert.AreEqual(new DrumPad(2, false), DrumPads.Of(InstrumentAction.BlueTom, DrumsVariant.Pro));
        Assert.AreEqual(new DrumPad(3, true), DrumPads.Of(InstrumentAction.OrangePad, DrumsVariant.Pro));
        Assert.IsNull(DrumPads.Of(InstrumentAction.Green, DrumsVariant.Pro));
    }

    [TestMethod]
    public void OnFiveLaneDrumsAFiveLaneKitPlaysItsOwnLanesAndAFourLaneKitsAsProTurnsInto5Lane()
    {
        int?[] lanes =
        [
            .. new[]
            {
                InstrumentAction.RedPad, InstrumentAction.YellowCymbal, InstrumentAction.BlueTom, InstrumentAction.OrangePad, InstrumentAction.GreenTom,
                InstrumentAction.YellowTom, InstrumentAction.BlueCymbal, InstrumentAction.GreenCymbal,
            }.Select(a => DrumPads.Of(a, DrumsVariant.FiveLane)?.Lane),
        ];

        Assert.AreSequenceEqual([0, 1, 2, 3, 4, 2, 3, 3], lanes);
    }

    [TestMethod]
    public void RecordModePlacesAPadInTheEditorsLaneForIt()
    {
        Assert.AreEqual(0, DrumPads.ShownLaneOf(InstrumentAction.Kick, DrumsVariant.Pro));
        Assert.AreEqual(1, DrumPads.ShownLaneOf(InstrumentAction.RedPad, DrumsVariant.Pro));
        Assert.AreEqual(4, DrumPads.ShownLaneOf(InstrumentAction.GreenCymbal, DrumsVariant.Pro));
        Assert.AreEqual(4, DrumPads.ShownLaneOf(InstrumentAction.OrangePad, DrumsVariant.FiveLane));
        Assert.AreEqual(5, DrumPads.ShownLaneOf(InstrumentAction.GreenTom, DrumsVariant.FiveLane));
    }

    [TestMethod]
    public void AColourIsSplitOnlyWhenBothItsTomAndItsCymbalAreBound()
    {
        Assert.AreEqual(CymbalSplit.None, DrumPads.SplitOf(InputPresets.KeyboardDrums()));

        var kit = new InputDeviceInfo(new InputDeviceId(InputDeviceKind.Midi, "Kit"), "Kit");
        Assert.AreEqual(CymbalSplit.Yellow | CymbalSplit.Blue | CymbalSplit.Green, DrumPads.SplitOf(InputPresets.GeneralMidiDrums(kit)));
    }
}
