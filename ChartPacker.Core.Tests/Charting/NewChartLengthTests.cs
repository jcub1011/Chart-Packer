using ChartPacker.Core.Charting;

namespace ChartPacker.Core.Tests.Charting;

[TestClass]
public sealed class NewChartLengthTests
{
    [TestMethod]
    [DataRow("3:30", 210_000)]
    [DataRow(" 0:05 ", 5_000)]
    [DataRow("1:02:03", 3_723_000)]
    [DataRow("210", 210_000)]
    [DataRow("90.5", 90_500)]
    [DataRow("2:05.25", 125_250)]
    [DataRow("75:00", 4_500_000)]
    public void LengthsAreRead(string text, long ms)
    {
        Assert.IsTrue(NewChartLength.TryParse(text, out var length));
        Assert.AreEqual(TimeSpan.FromMilliseconds(ms), length);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("0")]
    [DataRow("0:00")]
    [DataRow("3:60")]
    [DataRow("1:60:00")]
    [DataRow("-1:00")]
    [DataRow("a:30")]
    [DataRow("1:2:3:4")]
    [DataRow("24:00:00")]
    public void OtherTextIsNotALength(string? text) => Assert.IsFalse(NewChartLength.TryParse(text, out _));

    [TestMethod]
    public void FormattedLengthsReadBack()
    {
        foreach (var length in new[] { NewChartLength.Default, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(3723) })
        {
            Assert.IsTrue(NewChartLength.TryParse(NewChartLength.Format(length), out var read));
            Assert.AreEqual(length, read);
        }

        Assert.AreEqual("3:00", NewChartLength.Format(NewChartLength.Default));
        Assert.AreEqual("1:02:03", NewChartLength.Format(TimeSpan.FromSeconds(3723)));
    }
}
