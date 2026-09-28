using ChartPacker.Core.Ini;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Sng;
using ChartPacker.Core.Validation;

namespace ChartPacker.Core.Tests.Metadata;

[TestClass]
public sealed class DraftWriterTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-draftwriter-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public async Task SngFilesAreAddedRemovedAndReplaced()
    {
        string sng = await CreateSngAsync([P("name", "A")], ("notes.chart", [1, 2, 3]), ("song.ogg", [4]), ("video.mp4", [5]));
        var draft = new MetadataDraft();
        draft.RemoveFile("VIDEO.mp4");
        draft.AddFile(new AddedFile("guitar.ogg", WriteSource([7, 7]), 2));
        draft.AddFile(new AddedFile("song.ogg", WriteSource([9, 9, 9]), 3));

        var findings = await DraftWriter.SaveSngAsync(sng, draft);

        Assert.IsFalse(findings.Any(f => f.Severity == Severity.Error));
        var after = SngReader.Read(sng);
        Assert.IsEmpty(after.Findings);
        CollectionAssert.AreEqual(new[] { "notes.chart", "guitar.ogg", "song.ogg" }, after.Files.Select(f => f.Name).ToArray());
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, Extract(sng, after, "notes.chart"));
        CollectionAssert.AreEqual(new byte[] { 7, 7 }, Extract(sng, after, "guitar.ogg"));
        CollectionAssert.AreEqual(new byte[] { 9, 9, 9 }, Extract(sng, after, "song.ogg"));
        CollectionAssert.AreEqual(new[] { ("name", "A") }, after.Metadata.Select(p => (p.Key, p.Value)).ToArray());
    }

    [TestMethod]
    public async Task SngArtAndFileChangesAreSavedTogether()
    {
        string sng = await CreateSngAsync([P("name", "A")], ("album.jpg", [1]), ("notes.chart", [2]));
        string art = Path.Combine(_root.FullName, "cover.png");
        File.WriteAllBytes(art, [8, 8]);
        var draft = new MetadataDraft();
        draft.Set("artist", "Band");
        draft.SetAlbumArt(art);
        draft.RemoveFile("notes.chart");
        draft.AddFile(new AddedFile("notes.mid", WriteSource([6]), 1));

        var findings = await DraftWriter.SaveSngAsync(sng, draft);

        Assert.IsFalse(findings.Any(f => f.Severity == Severity.Error));
        var after = SngReader.Read(sng);
        CollectionAssert.AreEquivalent(new[] { "album.png", "notes.mid" }, after.Files.Select(f => f.Name).ToArray());
        CollectionAssert.AreEqual(new byte[] { 8, 8 }, Extract(sng, after, "album.png"));
        CollectionAssert.AreEqual(new[] { ("name", "A"), ("artist", "Band") }, after.Metadata.Select(p => (p.Key, p.Value)).ToArray());
    }

    [TestMethod]
    public async Task SngMetadataOnlyDraftKeepsEveryFile()
    {
        string sng = await CreateSngAsync([P("name", "A")], ("album.jpg", [1]), ("notes.chart", [2]));
        var draft = new MetadataDraft();
        draft.Set("name", "B");

        await DraftWriter.SaveSngAsync(sng, draft);

        var after = SngReader.Read(sng);
        CollectionAssert.AreEqual(new[] { "album.jpg", "notes.chart" }, after.Files.Select(f => f.Name).ToArray());
        Assert.AreEqual("B", after.Metadata.Single().Value);
    }

    [TestMethod]
    public async Task FolderFileChangesAreApplied()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root.FullName, "Song")).FullName;
        File.WriteAllBytes(Path.Combine(folder, "notes.chart"), [1]);
        File.WriteAllBytes(Path.Combine(folder, "video.mp4"), [2]);
        string songIni = Path.Combine(folder, SongIni.FileName);
        File.WriteAllText(songIni, "[song]\nname = A\n");
        var draft = new MetadataDraft();
        draft.Set("name", "B");
        draft.RemoveFile("video.mp4");
        draft.AddFile(new AddedFile("guitar.ogg", WriteSource([7]), 1));

        await DraftWriter.SaveFolderAsync(folder, songIni, draft);

        CollectionAssert.AreEquivalent(
            new[] { "guitar.ogg", "notes.chart", "song.ini" },
            Directory.GetFiles(folder).Select(Path.GetFileName).ToArray());
        StringAssert.Contains(File.ReadAllText(songIni), "name = B");
    }

    [TestMethod]
    public async Task FolderFileOnlyDraftDoesNotCreateASongIni()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root.FullName, "Song")).FullName;
        File.WriteAllBytes(Path.Combine(folder, "notes.chart"), [1]);
        var draft = new MetadataDraft();
        draft.AddFile(new AddedFile("guitar.ogg", WriteSource([7]), 1));

        await DraftWriter.SaveFolderAsync(folder, Path.Combine(folder, SongIni.FileName), draft);

        CollectionAssert.AreEquivalent(new[] { "guitar.ogg", "notes.chart" }, Directory.GetFiles(folder).Select(Path.GetFileName).ToArray());
    }

    private static KeyValuePair<string, string> P(string key, string value) => KeyValuePair.Create(key, value);

    private string WriteSource(byte[] data)
    {
        string path = Path.Combine(_root.FullName, Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, data);
        return path;
    }

    private async Task<string> CreateSngAsync(IReadOnlyList<KeyValuePair<string, string>> metadata, params (string Name, byte[] Data)[] files)
    {
        var sources = files.Select(f => new SngSourceFile(f.Name, WriteSource(f.Data), f.Data.Length)).ToList();
        string sng = Path.Combine(_root.FullName, "song.sng");
        await SngWriter.WriteAsync(sng, SngMask.Generate(), metadata, sources, overwrite: false);
        return sng;
    }

    private static byte[] Extract(string sng, SngReadResult read, string name)
    {
        using var handle = File.OpenHandle(sng);
        using var output = new MemoryStream();
        SngFileData.CopyUnmasked(handle, read.Files.Single(f => f.Name == name), read.Header!.Mask.Span, output);
        return output.ToArray();
    }
}
