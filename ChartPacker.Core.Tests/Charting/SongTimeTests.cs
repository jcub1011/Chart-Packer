using ChartPacker.Core.Charting;

namespace ChartPacker.Core.Tests.Charting;

[TestClass]
public sealed class SongTimeTests
{
    [TestMethod]
    [DataRow(0, "0:00")]
    [DataRow(5_000, "0:05")]
    [DataRow(185_000, "3:05")]
    [DataRow(3_599_999, "59:59")]
    [DataRow(3_600_000, "1:00:00")]
    [DataRow(3_905_000, "1:05:05")]
    [DataRow(36_000_000, "10:00:00")]
    public void TimesAreMinutesOrHours(long ms, string expected) =>
        Assert.AreEqual(expected, SongTime.Format(TimeSpan.FromMilliseconds(ms)));

    [TestMethod]
    [DataRow(185_290, 1, "3:05.2")]
    [DataRow(185_290, 2, "3:05.29")]
    [DataRow(0, 1, "0:00.0")]
    [DataRow(3_905_250, 1, "1:05:05.2")]
    public void FractionsAreCutOff(long ms, int decimals, string expected) =>
        Assert.AreEqual(expected, SongTime.Format(TimeSpan.FromMilliseconds(ms), decimals));

    [TestMethod]
    [DataRow(-1)]
    [DataRow(8)]
    public void DecimalsOutOfRangeThrow(int decimals) =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SongTime.Format(TimeSpan.Zero, decimals));
}
