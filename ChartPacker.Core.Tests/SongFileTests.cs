using System.Security.Cryptography;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Sng;
using ChartPacker.Core.Tests.Sng;

namespace ChartPacker.Core.Tests;

[TestClass]
public sealed class SongFileTests
{
    private static readonly byte[] Data = [1, 2, 3, 4, 5, 6, 7, 8, 9];

    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-songfile-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public async Task FolderFileIsReadAndHashedIgnoringCase()
    {
        var entry = Folder(CreateFolder(("Video.MP4", Data), ("notes.chart", [1])));

        CollectionAssert.AreEqual(Data, await SongFile.ReadAsync(entry, "video.mp4", long.MaxValue));
        Assert.AreEqual(Hash(Data), await SongFile.HashAsync(entry, "video.mp4"));
        Assert.IsNull(await SongFile.ReadAsync(entry, "video.mp4", Data.Length - 1));
        Assert.IsNull(await SongFile.ReadAsync(entry, "background.png", long.MaxValue));
        Assert.IsNull(await SongFile.HashAsync(entry, "background.png"));
    }

    [TestMethod]
    public async Task SngFileIsUnmaskedWhenReadAndHashed()
    {
        var builder = new SngBuilder();
        builder.AddFile("notes.chart", Masked(builder, [5])).AddFile("video.webm", Masked(builder, Data));
        var entry = WriteSng(builder);

        CollectionAssert.AreEqual(Data, await SongFile.ReadAsync(entry, "VIDEO.webm", long.MaxValue));
        Assert.AreEqual(Hash(Data), await SongFile.HashAsync(entry, "video.webm"));
        Assert.IsNull(await SongFile.ReadAsync(entry, "video.webm", Data.Length - 1));
        Assert.IsNull(await SongFile.HashAsync(entry, "video.mp4"));
    }

    [TestMethod]
    public async Task FolderFileInASubfolderIsReadByItsStoredName()
    {
        var entry = Folder(CreateFolder(("Extras/Readme.TXT", Data), ("notes.chart", [1])));

        CollectionAssert.AreEqual(Data, await SongFile.ReadAsync(entry, "extras/readme.txt", long.MaxValue));
        await using (var stream = SongFile.OpenRead(entry, "EXTRAS/readme.txt"))
            Assert.AreEqual(Data.Length, stream!.Length);
        Assert.IsNull(SongFile.OpenRead(entry, "extras/missing.txt"));
        Assert.IsNull(SongFile.OpenRead(entry, "missing/readme.txt"));
    }

    [TestMethod]
    [DataRow("../outside.txt")]
    [DataRow("Song/../../outside.txt")]
    [DataRow("./notes.chart")]
    [DataRow("extras//readme.txt")]
    [DataRow("..\\outside.txt")]
    public async Task FolderNameLeadingOutsideTheSongFindsNothing(string name)
    {
        File.WriteAllBytes(Path.Combine(_root.FullName, "outside.txt"), Data);
        var entry = Folder(CreateFolder(("extras/readme.txt", Data), ("notes.chart", [1])));

        Assert.IsNull(SongFile.OpenRead(entry, name));
        Assert.IsNull(await SongFile.ReadAsync(entry, name, long.MaxValue));
    }

    [TestMethod]
    public void ListTopLevelLeavesOutSubfolders()
    {
        var folder = Folder(CreateFolder(("background.png", Data), ("extras/video.mp4", [1])));
        var builder = new SngBuilder();
        builder.AddFile("extras/video.mp4", Masked(builder, [1])).AddFile("video.mp4", Masked(builder, Data));
        var sng = WriteSng(builder);

        CollectionAssert.AreEqual(new[] { new StoredFile("background.png", Data.Length) }, SongFile.ListTopLevel(folder).ToArray());
        CollectionAssert.AreEqual(new[] { new StoredFile("video.mp4", Data.Length) }, SongFile.ListTopLevel(sng).ToArray());
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static byte[] Masked(SngBuilder builder, byte[] data)
    {
        byte[] copy = [.. data];
        SngMask.Apply(copy, builder.Mask);
        return copy;
    }

    private SongEntry WriteSng(SngBuilder builder)
    {
        string path = Path.Combine(_root.FullName, "Song.sng");
        File.WriteAllBytes(path, builder.Build());
        return new SongEntry(SongEntryKind.SngFile, path, "Song.sng", "Song");
    }

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

    private SongEntry Folder(string path) =>
        new(SongEntryKind.SongFolder, path, Path.GetRelativePath(_root.FullName, path), Path.GetFileName(path));
}
