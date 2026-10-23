using ChartShared.Core.Calibration;

namespace ChartBuilder.Tests.Calibration;

/// <summary>The count-in both calibration tests show (charter-spec C11 items 1 and 2).</summary>
[TestClass]
public sealed class CalibrationCountInTests
{
    private const int Ready = 4;
    private const int Lead = 8;
    private const int Count = 16;

    [TestMethod]
    [DataRow(0.0)]
    [DataRow(3.99)]
    public void TheFirstBarGetsReady(double beats) =>
        Assert.AreEqual(new CountInStep(CountInPhase.GetReady, 0), CalibrationCountIn.At(beats, Ready, Lead, Count));

    [TestMethod]
    [DataRow(4.0, 4)]
    [DataRow(4.99, 4)]
    [DataRow(5.0, 3)]
    [DataRow(7.99, 1)]
    public void TheSecondBarCountsDownToOne(double beats, int number) =>
        Assert.AreEqual(new CountInStep(CountInPhase.CountIn, number), CalibrationCountIn.At(beats, Ready, Lead, Count));

    [TestMethod]
    [DataRow(8.0, 1)]
    [DataRow(8.49, 1)]
    [DataRow(8.5, 2)]
    [DataRow(9.0, 2)]
    [DataRow(Lead + Count - 1.0, Count)]
    [DataRow(Lead + Count + 3.0, Count)]
    public void ThenEachNoteFromHalfABeatBeforeIt(double beats, int number) =>
        Assert.AreEqual(new CountInStep(CountInPhase.Notes, number), CalibrationCountIn.At(beats, Ready, Lead, Count));
}
