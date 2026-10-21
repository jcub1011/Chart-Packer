using ChartShared.Core.Charting.Saving;

namespace ChartShared.Core.Tests.Charting;

/// <summary>The temp folder the Validator's generated charts are kept in, and the sweep of those an app left behind.</summary>
[TestClass]
public sealed class GeneratedChartStashTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task TheSweepLeavesAStashStillOpenHoweverOld()
    {
        using var stash = GeneratedChartStash.Create();
        await stash.KeepAsync([1, 2, 3], TestContext.CancellationToken);
        Directory.SetLastWriteTimeUtc(stash.Folder, DateTime.UtcNow.AddDays(-2));

        GeneratedChartStash.SweepStale(TimeSpan.FromDays(1));

        Assert.IsTrue(Directory.Exists(stash.Folder));
    }

    [TestMethod]
    public void TheSweepDeletesAnOldStashNoLongerOpen()
    {
        string folder;
        using (var stash = GeneratedChartStash.Create())
            folder = stash.Folder;

        // As an app that didn't close cleanly leaves it: the folder and its lock file, which no one holds.
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, ".lock"), []);
        Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow.AddDays(-2));

        GeneratedChartStash.SweepStale(TimeSpan.FromDays(1));

        Assert.IsFalse(Directory.Exists(folder));
    }

    [TestMethod]
    public void DisposingDeletesTheFolder()
    {
        var stash = GeneratedChartStash.Create();

        stash.Dispose();

        Assert.IsFalse(Directory.Exists(stash.Folder));
    }
}
