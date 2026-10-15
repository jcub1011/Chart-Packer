using ChartPacker.Core.Diagnostics;

namespace ChartPacker.Core.Tests.Diagnostics;

[TestClass]
public sealed class SessionMarkerTests
{
    private static readonly DateTimeOffset Earlier = new(2026, 10, 14, 9, 30, 0, TimeSpan.FromHours(-4));
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 10, 0, 0, TimeSpan.FromHours(-4));

    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-session-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public void AFirstStartFindsNothing()
    {
        var marker = Begin(processId: 100);

        Assert.IsEmpty(marker.Unclean);
        Assert.IsTrue(File.Exists(Path.Combine(_root.FullName, "app.100.running")));
    }

    [TestMethod]
    public void EndRemovesTheMarker()
    {
        var marker = Begin(processId: 100);

        marker.End();
        marker.End();

        Assert.IsEmpty(_root.GetFiles());
        Assert.IsEmpty(Begin(processId: 200).Unclean);
    }

    [TestMethod]
    public void ASessionThatNeverEndedIsFoundOnce()
    {
        SessionMarker.Begin(_root.FullName, "app", "1.2.0", Earlier, 100, _ => false);

        var marker = Begin(processId: 200);

        var unclean = Assert.ContainsSingle(marker.Unclean);
        Assert.AreEqual(new UncleanSession(Earlier, "1.2.0", 100), unclean);
        Assert.IsFalse(File.Exists(Path.Combine(_root.FullName, "app.100.running")));
        marker.End();
        Assert.IsEmpty(Begin(processId: 300).Unclean);
    }

    [TestMethod]
    public void ASessionStillRunningIsLeftAlone()
    {
        SessionMarker.Begin(_root.FullName, "app", "1.2.0", Earlier, 100, _ => false);

        var marker = Begin(processId: 200, isRunning: s => s.ProcessId == 100);

        Assert.IsEmpty(marker.Unclean);
        Assert.IsTrue(File.Exists(Path.Combine(_root.FullName, "app.100.running")));
    }

    [TestMethod]
    public void AnotherAppsMarkersAreLeftAlone()
    {
        SessionMarker.Begin(_root.FullName, "other", "1.0.0", Earlier, 100, _ => false);

        var marker = Begin(processId: 200);

        Assert.IsEmpty(marker.Unclean);
        Assert.IsTrue(File.Exists(Path.Combine(_root.FullName, "other.100.running")));
    }

    [TestMethod]
    public void UncleanSessionsComeOldestFirst()
    {
        // Two copies running at once, so the second doesn't take the first for a crash.
        SessionMarker.Begin(_root.FullName, "app", "1.1.0", Earlier.AddHours(2), 101, _ => false);
        SessionMarker.Begin(_root.FullName, "app", "1.0.0", Earlier, 102, _ => true);

        var marker = Begin(processId: 200);

        Assert.AreSequenceEqual(["1.0.0", "1.1.0"], marker.Unclean.Select(s => s.Version).ToArray());
    }

    [TestMethod]
    public void ADamagedMarkerStillCounts()
    {
        File.WriteAllText(Path.Combine(_root.FullName, "app.100.running"), "not a number");

        var marker = Begin(processId: 200);

        var unclean = Assert.ContainsSingle(marker.Unclean);
        Assert.AreEqual(0, unclean.ProcessId);
        Assert.AreEqual("unknown", unclean.Version);
    }

    [TestMethod]
    public void TheFolderIsCreated()
    {
        string folder = Path.Combine(_root.FullName, "logs", "deeper");

        var marker = SessionMarker.Begin(folder, "app", "1.0.0", Now, 100, _ => false);

        Assert.IsEmpty(marker.Unclean);
        Assert.IsTrue(File.Exists(Path.Combine(folder, "app.100.running")));
    }

    private SessionMarker Begin(int processId, Func<UncleanSession, bool>? isRunning = null) =>
        SessionMarker.Begin(_root.FullName, "app", "2.0.0", Now, processId, isRunning ?? (_ => false));
}
