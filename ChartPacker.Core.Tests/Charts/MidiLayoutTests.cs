using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Tests.Charts;

[TestClass]
public sealed class MidiLayoutTests
{
    // Offsets -2 to 7 from a difficulty's first pitch.
    private static readonly int[] Offsets = [.. Enumerable.Range(-2, 10)];

    [TestMethod]
    public void FiveFretLanesAreGreenToOrangeAndTheOpenBelowOnlyWithEnhancedOpens()
    {
        int?[] without = [.. Offsets.Select(o => MidiLayout.FretLaneOf(ChartInstrumentKind.FiveFret, o, enhancedOpens: false))];
        int?[] with = [.. Offsets.Select(o => MidiLayout.FretLaneOf(ChartInstrumentKind.FiveFret, o, enhancedOpens: true))];

        Assert.AreSequenceEqual([null, null, 0, 1, 2, 3, 4, null, null, null], without);
        Assert.AreSequenceEqual([null, ChartNote.OpenLane, 0, 1, 2, 3, 4, null, null, null], with);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SixFretLanesAreTheOpenThenWhiteThenBlack(bool enhancedOpens)
    {
        int?[] lanes = [.. Offsets.Select(o => MidiLayout.FretLaneOf(ChartInstrumentKind.SixFret, o, enhancedOpens))];

        Assert.AreSequenceEqual([null, null, ChartNote.OpenLane, 0, 1, 2, 3, 4, 5, null], lanes);
    }

    [TestMethod]
    public void DrumPadsAreTheDoubleKickToTheFifthPad()
    {
        int?[] pads = [.. Offsets.Select(MidiLayout.DrumPadOf)];

        Assert.AreSequenceEqual([null, -1, 0, 1, 2, 3, 4, 5, null, null], pads);
    }

    [TestMethod]
    public void ForceMarkersAreAboveTheTopLane()
    {
        Assert.AreEqual(5, MidiLayout.ForceHopoOffset(ChartInstrumentKind.FiveFret));
        Assert.AreEqual(6, MidiLayout.ForceStrumOffset(ChartInstrumentKind.FiveFret));
        Assert.AreEqual(7, MidiLayout.ForceHopoOffset(ChartInstrumentKind.SixFret));
        Assert.AreEqual(8, MidiLayout.ForceStrumOffset(ChartInstrumentKind.SixFret));

        // Expert's force strum is the highest pitch of the difficulty.
        Assert.AreEqual(ChartDifficulty.Expert, MidiLayout.DifficultyOf(ChartInstrumentKind.FiveFret, 102));
        Assert.IsNull(MidiLayout.DifficultyOf(ChartInstrumentKind.FiveFret, 103));
        Assert.AreEqual(ChartDifficulty.Expert, MidiLayout.DifficultyOf(ChartInstrumentKind.SixFret, 102));
        Assert.IsNull(MidiLayout.DifficultyOf(ChartInstrumentKind.SixFret, 103));
    }
}
