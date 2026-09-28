using ChartPacker.Core.Scanning;
using ChartPacker.Core.Sng;
using ChartPacker.Core.Tests.Sng;

namespace ChartPacker.Core.Tests;

[TestClass]
public sealed class AlbumArtTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4];
    private static readonly byte[] Jpg = [0xFF, 0xD8, 0xFF, 9, 8, 7];

    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-art-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public async Task FolderGivesTheImageBytes()
    {
        string folder = CreateFolder(("album.jpg", Jpg), ("notes.chart", [1]));

        CollectionAssert.AreEqual(Jpg, await AlbumArt.ReadAsync(Folder(folder)));
    }

    [TestMethod]
    public async Task FolderPrefersPng()
    {
        string folder = CreateFolder(("album.jpg", Jpg), ("album.png", Png));

        CollectionAssert.AreEqual(Png, await AlbumArt.ReadAsync(Folder(folder)));
    }

    [TestMethod]
    public async Task FolderMatchesIgnoringCase()
    {
        string folder = CreateFolder(("Album.PNG", Png));

        CollectionAssert.AreEqual(Png, await AlbumArt.ReadAsync(Folder(folder)));
    }

    [TestMethod]
    public async Task FolderIgnoresArtInSubfolders()
    {
        string folder = CreateFolder(("art/album.png", Png));

        Assert.IsNull(await AlbumArt.ReadAsync(Folder(folder)));
    }

    [TestMethod]
    public async Task FolderWithoutArtGivesNull()
    {
        string folder = CreateFolder(("background.png", Png));

        Assert.IsNull(await AlbumArt.ReadAsync(Folder(folder)));
    }

    [TestMethod]
    public async Task FolderArtOverTheCapGivesNull()
    {
        string folder = CreateFolder(("album.png", Png));

        Assert.IsNull(await AlbumArt.ReadAsync(Folder(folder), maxBytes: Png.Length - 1));
        CollectionAssert.AreEqual(Png, await AlbumArt.ReadAsync(Folder(folder), maxBytes: Png.Length));
    }

    [TestMethod]
    public async Task SngGivesTheUnmaskedImageBytes()
    {
        var builder = new SngBuilder();
        builder.AddFile("notes.chart", Masked(builder, [5, 5, 5])).AddFile("album.jpg", Masked(builder, Jpg));

        CollectionAssert.AreEqual(Jpg, await AlbumArt.ReadAsync(WriteSng(builder)));
    }

    [TestMethod]
    public async Task SngPrefersPng()
    {
        var builder = new SngBuilder();
        builder.AddFile("album.jpeg", Masked(builder, Jpg)).AddFile("ALBUM.png", Masked(builder, Png));

        CollectionAssert.AreEqual(Png, await AlbumArt.ReadAsync(WriteSng(builder)));
    }

    [TestMethod]
    public async Task SngWithoutArtGivesNull()
    {
        var builder = new SngBuilder();
        builder.AddFile("notes.chart", Masked(builder, [5]));

        Assert.IsNull(await AlbumArt.ReadAsync(WriteSng(builder)));
    }

    [TestMethod]
    public async Task SngArtOverTheCapGivesNull()
    {
        var builder = new SngBuilder();
        builder.AddFile("album.png", Masked(builder, Png));

        Assert.IsNull(await AlbumArt.ReadAsync(WriteSng(builder), maxBytes: Png.Length - 1));
    }

    [TestMethod]
    public async Task SngArtPastTheEndThrows()
    {
        var builder = new SngBuilder();
        builder.AddFile("album.png", Masked(builder, Png));
        builder.LengthOverrides[0] = (ulong)Png.Length + 100;

        await Assert.ThrowsAsync<IOException>(() => AlbumArt.ReadAsync(WriteSng(builder)));
    }

    [TestMethod]
    public async Task NotAnSngGivesNull()
    {
        string path = Path.Combine(_root.FullName, "junk.sng");
        File.WriteAllBytes(path, [1, 2, 3]);

        Assert.IsNull(await AlbumArt.ReadAsync(new SongEntry(SongEntryKind.SngFile, path, "junk.sng", "junk")));
    }

    [TestMethod]
    public async Task CancelledReadThrows()
    {
        string folder = CreateFolder(("album.png", Png));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => AlbumArt.ReadAsync(Folder(folder), cancellationToken: cts.Token));
    }

    /// <summary>The builder stores data as given, so it's masked here to be stored the way a real .sng would.</summary>
    private static byte[] Masked(SngBuilder builder, byte[] data)
    {
        byte[] copy = data.ToArray();
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
