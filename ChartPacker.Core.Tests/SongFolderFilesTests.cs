using ChartPacker.Core.Metadata;
using ChartPacker.Core.Scanning;

namespace ChartPacker.Core.Tests;

[TestClass]
public sealed class SongFolderFilesTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-folderfiles-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public async Task AddsAreCopiedAndRemovesAreDeleted()
    {
        string folder = CreateFolder(("notes.chart", [1]), ("song.ogg", [2]), ("song.ini", [3]));
        string source = WriteLoose("guitar.ogg", [7, 7]);

        await SongFolderFiles.ApplyAsync(folder, ["SONG.ogg"], [new AddedFile("guitar.ogg", source, 2)], TestContext.CancellationToken);

        Assert.AreSequenceEqual(["guitar.ogg", "notes.chart", "song.ini"], FileNames(folder), Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
        Assert.AreSequenceEqual(new byte[] { 7, 7 }, File.ReadAllBytes(Path.Combine(folder, "guitar.ogg")));
        Assert.IsTrue(File.Exists(source));
    }

    [TestMethod]
    public async Task AnAddReplacesAFileOfTheSameName()
    {
        string folder = CreateFolder(("song.ogg", [1, 1, 1]));
        string source = WriteLoose("song.ogg", [9]);

        await SongFolderFiles.ApplyAsync(folder, [], [new AddedFile("song.ogg", source, 1)], TestContext.CancellationToken);

        Assert.AreSequenceEqual(["song.ogg"], FileNames(folder), Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
        Assert.AreSequenceEqual(new byte[] { 9 }, File.ReadAllBytes(Path.Combine(folder, "song.ogg")));
    }

    [TestMethod]
    public async Task AnAddCanBeAFileBeingRemoved()
    {
        string folder = CreateFolder(("old.ogg", [4, 5]));

        await SongFolderFiles.ApplyAsync(folder, ["old.ogg"], [new AddedFile("new.ogg", Path.Combine(folder, "old.ogg"), 2)], TestContext.CancellationToken);

        Assert.AreSequenceEqual(["new.ogg"], FileNames(folder), Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
        Assert.AreSequenceEqual(new byte[] { 4, 5 }, File.ReadAllBytes(Path.Combine(folder, "new.ogg")));
    }

    [TestMethod]
    public async Task ASubfolderFileCanBeRemovedAndItsFolderIsKept()
    {
        string folder = CreateFolder(("stems/drums.ogg", [1]), ("notes.chart", [2]));

        await SongFolderFiles.ApplyAsync(folder, ["stems/drums.ogg", "not-there.ogg"], [], TestContext.CancellationToken);

        Assert.IsFalse(File.Exists(Path.Combine(folder, "stems", "drums.ogg")));
        Assert.IsTrue(Directory.Exists(Path.Combine(folder, "stems")));
        Assert.AreSequenceEqual(["notes.chart"], FileNames(folder), Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    public async Task AnExistingFileNamedLikeATempFileIsLeftAlone()
    {
        string folder = CreateFolder(("song.ogg.tmp", [8]));
        string source = WriteLoose("song.ogg", [9]);

        await SongFolderFiles.ApplyAsync(folder, [], [new AddedFile("song.ogg", source, 1)], TestContext.CancellationToken);

        Assert.AreSequenceEqual(["song.ogg", "song.ogg.tmp"], FileNames(folder), Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
        Assert.AreSequenceEqual(new byte[] { 8 }, File.ReadAllBytes(Path.Combine(folder, "song.ogg.tmp")));
    }

    [TestMethod]
    [DataRow("song.ini")]
    [DataRow("SONG.INI")]
    public async Task RemovingTheSongIniIsRefused(string name)
    {
        string folder = CreateFolder(("song.ini", [1]), ("notes.chart", [2]));

        await Assert.ThrowsAsync<ArgumentException>(() => SongFolderFiles.ApplyAsync(folder, [name, "notes.chart"], [], TestContext.CancellationToken));

        Assert.AreSequenceEqual(["notes.chart", "song.ini"], FileNames(folder), Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    public async Task AddingASongIniIsRefused()
    {
        string folder = CreateFolder(("song.ini", [1]));
        string source = WriteLoose("song.ini", [9]);

        await Assert.ThrowsAsync<ArgumentException>(() => SongFolderFiles.ApplyAsync(folder, [], [new AddedFile("song.ini", source, 1)], TestContext.CancellationToken));

        Assert.AreSequenceEqual(new byte[] { 1 }, File.ReadAllBytes(Path.Combine(folder, "song.ini")));
    }

    [TestMethod]
    [DataRow("../outside.txt")]
    [DataRow("stems/../../outside.txt")]
    public async Task APathOutsideTheFolderIsRefused(string name)
    {
        string folder = CreateFolder(("notes.chart", [1]));
        string outside = WriteLoose("outside.txt", [5]);

        await Assert.ThrowsAsync<ArgumentException>(() => SongFolderFiles.ApplyAsync(folder, [name], [], TestContext.CancellationToken));

        Assert.IsTrue(File.Exists(outside));
        Assert.AreSequenceEqual(["notes.chart"], FileNames(folder), Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    public async Task AMissingSourceLeavesTheFolderAsItWas()
    {
        string folder = CreateFolder(("notes.chart", [1]), ("song.ogg", [2]));
        string good = WriteLoose("guitar.ogg", [7]);

        await Assert.ThrowsAsync<FileNotFoundException>(() => SongFolderFiles.ApplyAsync(
            folder,
            ["song.ogg"],
            [new AddedFile("guitar.ogg", good, 1), new AddedFile("drums.ogg", Path.Combine(_root.FullName, "missing.ogg"), 1)], TestContext.CancellationToken));

        Assert.AreSequenceEqual(["notes.chart", "song.ogg"], FileNames(folder), Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    public async Task ARemovalWithTheSameNameAsAnAddEndsWithTheAddedFile()
    {
        string folder = CreateFolder(("song.ogg", [1, 1, 1]));
        string source = WriteLoose("song.ogg", [9]);

        await SongFolderFiles.ApplyAsync(folder, ["song.ogg"], [new AddedFile("song.ogg", source, 1)], TestContext.CancellationToken);

        Assert.AreSequenceEqual(["song.ogg"], FileNames(folder), Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
        Assert.AreSequenceEqual(new byte[] { 9 }, File.ReadAllBytes(Path.Combine(folder, "song.ogg")));
    }

    [TestMethod]
    public async Task ARemovalThatOnlyDiffersFromAnAddInCaseEndsWithTheAddedFile()
    {
        string folder = CreateFolder(("Guitar.ogg", [1, 1, 1]));
        string source = WriteLoose("guitar.ogg", [9]);

        await SongFolderFiles.ApplyAsync(folder, ["Guitar.ogg"], [new AddedFile("guitar.ogg", source, 1)], TestContext.CancellationToken);

        Assert.AreSequenceEqual(["guitar.ogg"], FileNames(folder), Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
        Assert.AreSequenceEqual(new byte[] { 9 }, File.ReadAllBytes(Path.Combine(folder, "guitar.ogg")));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task ADeleteThatFailsAfterTheMovesLeavesTheOldAndNewFilesBoth()
    {
        // AC C2.4.
        // Only Windows refuses to delete a file that's open.
        string folder = CreateFolder(("notes.chart", [1]));
        string source = WriteLoose("notes.mid", [9]);
        string chart = Path.Combine(folder, "notes.chart");

        using (new FileStream(chart, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => SongFolderFiles.ApplyAsync(
                folder, ["notes.chart"], [new AddedFile("notes.mid", source, 1)], TestContext.CancellationToken));
        }

        Assert.AreSequenceEqual(["notes.chart", "notes.mid"], FileNames(folder), Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
        Assert.AreSequenceEqual(new byte[] { 9 }, File.ReadAllBytes(Path.Combine(folder, "notes.mid")));
        Assert.IsEmpty(Directory.GetFiles(folder, "*.tmp", SearchOption.AllDirectories));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task ADeleteThatFailsDoesNotStopTheOtherRemovals()
    {
        // Only Windows refuses to delete a file that's open.
        string folder = CreateFolder(("drums.ogg", [1]), ("bass.ogg", [2]), ("notes.mid", [3]));

        using (new FileStream(Path.Combine(folder, "drums.ogg"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => SongFolderFiles.ApplyAsync(
                folder, ["drums.ogg", "bass.ogg"], [], TestContext.CancellationToken));
        }

        Assert.AreSequenceEqual(["drums.ogg", "notes.mid"], FileNames(folder), Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
    }

    private string WriteLoose(string name, byte[] data)
    {
        string path = Path.Combine(_root.FullName, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static string[] FileNames(string folder) => Directory.GetFiles(folder).Select(Path.GetFileName).ToArray()!;

    private string CreateFolder(params (string Relative, byte[] Data)[] files)
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root.FullName, "Song")).FullName;
        foreach (var (relative, data) in files)
        {
            string path = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
        }

        return folder;
    }

    public TestContext TestContext { get; set; }
}
