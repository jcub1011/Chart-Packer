using ChartShared.Core.Launching;

namespace ChartShared.Core.Tests.Launching;

[TestClass]
public sealed class DifficultyReportArgsTests
{
    [TestMethod]
    public void OnlyTheCommandFirstAsksForTheReport()
    {
        Assert.IsTrue(DifficultyReportArgs.IsReport(["--difficulty-report", "Songs"]));
        Assert.IsFalse(DifficultyReportArgs.IsReport(["Songs", "--difficulty-report"]));
        Assert.IsFalse(DifficultyReportArgs.IsReport([]));
    }

    [TestMethod]
    public void AFolderAloneWritesTheDefaultFileHereAndLooksTheDefaultDepthDown()
    {
        var args = DifficultyReportArgs.Parse(["--difficulty-report", "Songs"]);

        Assert.IsNull(args.Problem);
        Assert.AreEqual(Path.GetFullPath("Songs"), args.SongsFolder);
        Assert.AreEqual(Path.GetFullPath(DifficultyReportArgs.DefaultFileName), args.OutputFile);
        Assert.AreEqual(DifficultyReportArgs.DefaultDepth, args.Depth);
    }

    [TestMethod]
    public void TheOutputAndDepthCanBeGivenInAnyOrder()
    {
        string output = Path.Combine("reports", "ratings.csv");
        var args = DifficultyReportArgs.Parse(["--difficulty-report", "--depth", "3", "Songs", "--out", output]);

        Assert.IsNull(args.Problem);
        Assert.AreEqual(Path.GetFullPath("Songs"), args.SongsFolder);
        Assert.AreEqual(Path.GetFullPath(output), args.OutputFile);
        Assert.AreEqual(3, args.Depth);
    }

    [TestMethod]
    [DataRow(new[] { "--difficulty-report" }, "No songs folder")]
    [DataRow(new[] { "--difficulty-report", "Songs", "--out" }, "--out needs a value")]
    [DataRow(new[] { "--difficulty-report", "Songs", "--depth", "-1" }, "--depth needs a whole number")]
    [DataRow(new[] { "--difficulty-report", "Songs", "--depth", "deep" }, "--depth needs a whole number")]
    [DataRow(new[] { "--difficulty-report", "Songs", "More songs" }, "Only one songs folder")]
    [DataRow(new[] { "--difficulty-report", "Songs", "--verbose" }, "Unknown option --verbose")]
    public void BadArgumentsSayWhatsWrong(string[] arguments, string problem)
    {
        var args = DifficultyReportArgs.Parse(arguments);

        Assert.IsNotNull(args.Problem);
        Assert.StartsWith(problem, args.Problem);
    }
}
