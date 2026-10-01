using ChartPacker.Core.Metadata;
using ChartPacker.Core.Packing;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Sng;

namespace ChartPacker.Core.Tests.Packing;

[TestClass]
public sealed class SongExportTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-songexport-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public async Task SngIsSavedAsAFolderAndLeftAlone()
    {
        string sng = await CreateSngAsync([KeyValuePair.Create("name", "Song")], ("notes.chart", [1, 2]), ("extras/readme.txt", [3]));
        byte[] before = File.ReadAllBytes(sng);
        string target = Path.Combine(_root.FullName, "out", "Artist - Song");

        var result = await SongExport.ExportFolderAsync(Sng(sng), null, target);

        Assert.IsTrue(result.Extracted);
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, File.ReadAllBytes(Path.Combine(target, "notes.chart")));
        CollectionAssert.AreEqual(new byte[] { 3 }, File.ReadAllBytes(Path.Combine(target, "extras", "readme.txt")));
        StringAssert.Contains(File.ReadAllText(Path.Combine(target, "song.ini")), "name = Song");
        CollectionAssert.AreEqual(before, File.ReadAllBytes(sng));
        AssertOnlyTarget(target);
    }

    [TestMethod]
    public async Task FolderIsCopiedWithItsSubfolders()
    {
        string folder = CreateFolder("Song", ("notes.mid", [1]), ("song.ini", "[song]\nname = Song\n"u8.ToArray()), ("stems/drums.ogg", [2, 2]));
        string target = Path.Combine(_root.FullName, "out", "Song");

        var result = await SongExport.ExportFolderAsync(FolderEntry(folder), null, target);

        Assert.IsTrue(result.Extracted);
        CollectionAssert.AreEquivalent(
            new[] { "notes.mid", "song.ini", "stems/drums.ogg" },
            SongFolderListing.List(target).Select(f => f.RelativePath).ToArray());
        CollectionAssert.AreEqual(new byte[] { 2, 2 }, File.ReadAllBytes(Path.Combine(target, "stems", "drums.ogg")));
        Assert.IsTrue(File.Exists(Path.Combine(folder, "notes.mid")));
        AssertOnlyTarget(target);
    }

    [TestMethod]
    public async Task TheDraftIsSavedToTheCopyOnly()
    {
        string folder = CreateFolder("Song", ("notes.mid", [1]), ("song.ini", "[song]\nname = Song\n"u8.ToArray()), ("video.mp4", [4]));
        string source = Path.Combine(_root.FullName, "guitar-source.ogg");
        File.WriteAllBytes(source, [7, 7]);
        var draft = new MetadataDraft();
        draft.Set("artist", "Band");
        draft.RemoveFile("video.mp4");
        draft.AddFile(new AddedFile("guitar.ogg", source, 2));
        string target = Path.Combine(_root.FullName, "out", "Band - Song");

        await SongExport.ExportFolderAsync(FolderEntry(folder), draft, target);

        CollectionAssert.AreEquivalent(new[] { "notes.mid", "song.ini", "guitar.ogg" }, Directory.GetFiles(target).Select(Path.GetFileName).ToArray());
        StringAssert.Contains(File.ReadAllText(Path.Combine(target, "song.ini")), "artist = Band");
        // The song and the staged file are as they were, so the draft can still be saved.
        Assert.IsTrue(File.Exists(Path.Combine(folder, "video.mp4")));
        Assert.DoesNotContain("Band", File.ReadAllText(Path.Combine(folder, "song.ini")));
        Assert.IsTrue(File.Exists(source));
    }

    [TestMethod]
    public async Task AnExistingTargetIsRefusedAndNothingIsLeftBehind()
    {
        string folder = CreateFolder("Song", ("notes.mid", [1]));
        string target = Directory.CreateDirectory(Path.Combine(_root.FullName, "out", "Song")).FullName;

        await Assert.ThrowsAsync<IOException>(() => SongExport.ExportFolderAsync(FolderEntry(folder), null, target));

        Assert.IsEmpty(Directory.GetFileSystemEntries(target));
        AssertOnlyTarget(target);
    }

    [TestMethod]
    public async Task AFailedDraftLeavesNoFolder()
    {
        string folder = CreateFolder("Song", ("notes.mid", [1]));
        var draft = new MetadataDraft();
        draft.AddFile(new AddedFile("guitar.ogg", Path.Combine(_root.FullName, "missing.ogg"), 2));
        string target = Path.Combine(_root.FullName, "out", "Song");

        await Assert.ThrowsAsync<IOException>(() => SongExport.ExportFolderAsync(FolderEntry(folder), draft, target));

        Assert.IsFalse(Directory.Exists(target));
        Assert.IsEmpty(Directory.GetFileSystemEntries(Path.Combine(_root.FullName, "out")));
    }

    /// <summary>The target's folder holds the target and nothing else: no staging folder was left.</summary>
    private static void AssertOnlyTarget(string target)
    {
        string parent = Path.GetDirectoryName(target)!;
        CollectionAssert.AreEqual(new[] { target }, Directory.GetFileSystemEntries(parent));
    }

    private async Task<string> CreateSngAsync(IReadOnlyList<KeyValuePair<string, string>> metadata, params (string Name, byte[] Data)[] files)
    {
        var sources = files.Select(f =>
        {
            string path = Path.Combine(_root.FullName, Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(path, f.Data);
            return new SngSourceFile(f.Name, path, f.Data.Length);
        }).ToList();
        string sng = Path.Combine(_root.FullName, "song.sng");
        await SngWriter.WriteAsync(sng, SngMask.Generate(), metadata, sources, overwrite: false);
        return sng;
    }

    private string CreateFolder(string name, params (string Relative, byte[] Data)[] files)
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root.FullName, name)).FullName;
        foreach (var (relative, data) in files)
        {
            string path = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
        }

        return folder;
    }

    private static SongEntry Sng(string path) => new(SongEntryKind.SngFile, path, Path.GetFileName(path), Path.GetFileNameWithoutExtension(path));

    private static SongEntry FolderEntry(string path) => new(SongEntryKind.SongFolder, path, Path.GetFileName(path), Path.GetFileName(path));
}
