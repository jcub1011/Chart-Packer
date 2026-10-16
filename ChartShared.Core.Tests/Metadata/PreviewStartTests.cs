using ChartShared.Core.Metadata;

namespace ChartShared.Core.Tests.Metadata;

[TestClass]
public sealed class PreviewStartTests
{
    [TestMethod]
    public void ParsesMilliseconds() =>
        Assert.AreEqual(TimeSpan.FromMilliseconds(45_500), PreviewStart.Parse("45500"));

    [TestMethod]
    public void ZeroIsAValidStart() =>
        Assert.AreEqual(TimeSpan.Zero, PreviewStart.Parse("0"));

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("  ")]
    [DataRow("-1")]
    [DataRow("1:30")]
    [DataRow("55.5")]
    [DataRow("soon")]
    [DataRow("9223372036854775807")]
    [DataRow("99999999999999999999")]
    public void InvalidValuesGiveNothing(string? value) =>
        Assert.IsNull(PreviewStart.Parse(value));

    [TestMethod]
    public void TakesTheFirstKeyIgnoringCase() =>
        Assert.AreEqual(
            TimeSpan.FromMilliseconds(10),
            PreviewStart.From([KeyValuePair.Create("name", "x"), KeyValuePair.Create("Preview_Start_Time", "10"), KeyValuePair.Create("preview_start_time", "20")]));

    [TestMethod]
    public void AnInvalidFirstKeyIsNotSkipped() =>
        Assert.IsNull(PreviewStart.From([KeyValuePair.Create("preview_start_time", "x"), KeyValuePair.Create("preview_start_time", "20")]));

    [TestMethod]
    public void NoKeyGivesNothing() =>
        Assert.IsNull(PreviewStart.From([KeyValuePair.Create("preview", "1000 2000")]));

    [TestMethod]
    public void ResolveKeepsAStartInsideTheSong() =>
        Assert.AreEqual(TimeSpan.FromSeconds(30), PreviewStart.Resolve(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(3)));

    [TestMethod]
    public void ResolveStartsAtTheBeginningWithoutAStart() =>
        Assert.AreEqual(TimeSpan.Zero, PreviewStart.Resolve(null, TimeSpan.FromMinutes(3)));

    [TestMethod]
    [DataRow(180)]
    [DataRow(181)]
    public void ResolveStartsAtTheBeginningWhenTheStartIsNotInsideTheSong(int seconds) =>
        Assert.AreEqual(TimeSpan.Zero, PreviewStart.Resolve(TimeSpan.FromSeconds(seconds), TimeSpan.FromMinutes(3)));

    [TestMethod]
    public void ResolveTrustsTheStartWhenTheLengthIsUnknown() =>
        Assert.AreEqual(TimeSpan.FromSeconds(30), PreviewStart.Resolve(TimeSpan.FromSeconds(30), TimeSpan.Zero));
}
