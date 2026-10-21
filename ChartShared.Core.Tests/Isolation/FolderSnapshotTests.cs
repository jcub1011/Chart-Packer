namespace ChartShared.Core.Tests.Isolation;

/// <summary>Proves <see cref="UserConfigGuard"/> would notice a change, using temp folders only.</summary>
[TestClass]
public sealed class FolderSnapshotTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-snapshot-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public void UnchangedFolderHasNoDiff()
    {
        Write("settings.json", "{}");
        Write("logs/app.log", "hello");

        Assert.IsEmpty(FolderSnapshot.Take(_root.FullName).DiffTo(FolderSnapshot.Take(_root.FullName)));
    }

    [TestMethod]
    public void DetectsModifiedFile()
    {
        Write("settings.json", """{ "LibraryPaneWidth": 320 }""");
        var before = FolderSnapshot.Take(_root.FullName);

        // Same length, so only the content hash (or timestamp) can catch it.
        Write("settings.json", """{ "LibraryPaneWidth": 480 }""");

        Assert.AreSequenceEqual(["'settings.json' was modified"], before.DiffTo(FolderSnapshot.Take(_root.FullName)).ToList());
    }

    [TestMethod]
    public void DetectsCreatedAndDeletedFiles()
    {
        Write("old.json", "{}");
        var before = FolderSnapshot.Take(_root.FullName);

        File.Delete(Path.Combine(_root.FullName, "old.json"));
        Write("new.json", "{}");

        Assert.AreSequenceEqual(
            ["'old.json' was deleted", "'new.json' was created"], before.DiffTo(FolderSnapshot.Take(_root.FullName)).ToList(), Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    public void DetectsFolderCreatedWhereNoneExisted()
    {
        string missing = Path.Combine(_root.FullName, "ChartPacker");
        var before = FolderSnapshot.Take(missing);

        Directory.CreateDirectory(missing);
        File.WriteAllText(Path.Combine(missing, "settings.json"), "{}");

        Assert.AreSequenceEqual(
            ["folder was created", "'settings.json' was created"], before.DiffTo(FolderSnapshot.Take(missing)).ToList(), Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    public void ChangedPathsNameEachFileAndNullForTheFolder()
    {
        string missing = Path.Combine(_root.FullName, "ChartPacker");
        var before = FolderSnapshot.Take(missing);

        Directory.CreateDirectory(Path.Combine(missing, "Recovery"));
        File.WriteAllText(Path.Combine(missing, "Recovery", "song.json"), "{}");

        Assert.AreSequenceEqual(
            [null, Path.Combine("Recovery", "song.json")], before.ChangedPaths(FolderSnapshot.Take(missing)).ToList(), Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    public void OnlyTheAppsOwnFilesChangedWhileOneRanArePutDownToIt()
    {
        string recovered = Path.Combine("Recovery", "song.json");

        Assert.IsTrue(UserConfigGuard.IsTheAppsOwn(["builder-settings.json", "settings.json", recovered], appRan: true));
        Assert.IsFalse(UserConfigGuard.IsTheAppsOwn(["builder-settings.json"], appRan: false), "No app ran, so a test changed it.");
        Assert.IsFalse(UserConfigGuard.IsTheAppsOwn(["builder-settings.json", "stray.json"], appRan: true), "No app keeps stray.json.");
        Assert.IsFalse(UserConfigGuard.IsTheAppsOwn([null], appRan: true), "No app creates or deletes the folder itself.");
        Assert.IsFalse(UserConfigGuard.IsTheAppsOwn([], appRan: true));
    }

    private void Write(string relative, string content)
    {
        string path = Path.Combine(_root.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
