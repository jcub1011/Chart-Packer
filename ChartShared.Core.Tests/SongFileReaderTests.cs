using ChartShared.Core.Scanning;
using ChartShared.Core.Sng;
using ChartShared.Core.Tests.Sng;

namespace ChartShared.Core.Tests;

[TestClass]
public sealed class SongFileReaderTests
{
    private static readonly byte[] Data = [1, 2, 3, 4, 5, 6, 7, 8, 9];

    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-songfilereader-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public async Task AFolderIsListedOnceAndItsFilesReadIgnoringCase()
    {
        var entry = Folder(CreateFolder(("Song.ogg", Data), ("notes.mid", [7]), ("Extras/Readme.txt", [3])));
        using var files = SongFileReader.Open(entry, TestContext.CancellationToken);

        Assert.IsNull(files.Sng);
        Assert.AreSequenceEqual(SongFile.ListTopLevel(entry, TestContext.CancellationToken).OrderBy(f => f.Name), files.TopLevel.OrderBy(f => f.Name));
        Assert.AreSequenceEqual(Data, await files.ReadAsync("SONG.ogg", long.MaxValue, TestContext.CancellationToken));
        Assert.IsNull(await files.ReadAsync("song.ogg", Data.Length - 1, TestContext.CancellationToken));
        Assert.AreSequenceEqual(new byte[] { 3 }, await files.ReadAsync("extras/readme.txt", long.MaxValue, TestContext.CancellationToken));
        Assert.IsNull(await files.ReadAsync("guitar.ogg", long.MaxValue, TestContext.CancellationToken));
        Assert.AreSequenceEqual(Data, ReadAll(files.OpenRead("song.OGG")!));
        Assert.IsNull(files.OpenRead("guitar.ogg"));
    }

    [TestMethod]
    public async Task AnSngIsReadOnceAndItsFilesUnmaskedFromIt()
    {
        var builder = new SngBuilder().AddMetadata("name", "A");
        builder.AddFile("notes.mid", Masked(builder, [7])).AddFile("song.ogg", Masked(builder, Data));
        var entry = WriteSng(builder);
        using var files = SongFileReader.Open(entry, TestContext.CancellationToken);

        Assert.IsNotNull(files.Sng);
        Assert.AreEqual("A", files.Sng.Metadata.Single().Value);
        Assert.AreSequenceEqual(SongFile.ListTopLevel(entry, TestContext.CancellationToken), files.TopLevel);
        Assert.AreSequenceEqual(Data, await files.ReadAsync("SONG.ogg", long.MaxValue, TestContext.CancellationToken));
        Assert.IsNull(await files.ReadAsync("song.ogg", Data.Length - 1, TestContext.CancellationToken));
        Assert.IsNull(await files.ReadAsync("guitar.ogg", long.MaxValue, TestContext.CancellationToken));
        Assert.AreSequenceEqual(new byte[] { 7 }, ReadAll(files.OpenRead("notes.mid")!));
        Assert.AreSequenceEqual(Data, ReadAll(files.OpenRead("song.ogg")!));
        Assert.IsNull(files.OpenRead("guitar.ogg"));
    }

    [TestMethod]
    public void AMissingFolderThrows() =>
        Assert.Throws<DirectoryNotFoundException>(() => SongFileReader.Open(Folder(Path.Combine(_root.FullName, "Gone")), TestContext.CancellationToken));

    private static byte[] ReadAll(Stream stream)
    {
        using (stream)
        {
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }
    }

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

    public TestContext TestContext { get; set; } = null!;
}
