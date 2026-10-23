using ChartShared.Core.Sng;
using ChartShared.Core.Validation;

namespace ChartShared.Core.Tests.Sng;

[TestClass]
public sealed class SngWriterRewriteTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-rewrite-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public async Task EditedNameKeepsMaskAndFileContents()
    {
        byte[] audio = new byte[(1024 * 1024) + 777];
        new Random(7).NextBytes(audio);
        byte[] chart = "[Song]\n{\n}\n"u8.ToArray();
        string sng = await CreateSngAsync(
            [KeyValuePair.Create("name", "Old"), KeyValuePair.Create("artist", "Band")],
            ("notes.chart", chart), ("song.ogg", audio), ("empty.txt", []));
        var before = SngReader.Read(sng, TestContext.CancellationToken);

        var findings = await SngWriter.RewriteMetadataAsync(
            sng, [KeyValuePair.Create("name", "New Title"), KeyValuePair.Create("artist", "Band")], TestContext.CancellationToken);

        Assert.DoesNotContain(f => f.Severity == Severity.Error, findings);
        var after = SngReader.Read(sng, TestContext.CancellationToken);
        Assert.IsEmpty(after.Findings);
        Assert.AreSequenceEqual(before.Header!.Mask.ToArray(), after.Header!.Mask.ToArray());
        Assert.AreSequenceEqual(
            [("name", "New Title"), ("artist", "Band")], [.. after.Metadata.Select(p => (p.Key, p.Value))]);
        Assert.AreSequenceEqual([.. before.Files.Select(f => (f.Name, f.Length))], [.. after.Files.Select(f => (f.Name, f.Length))]);
        Assert.AreSequenceEqual(chart, Extract(sng, after, "notes.chart"));
        Assert.AreSequenceEqual(audio, Extract(sng, after, "song.ogg"));
        Assert.IsEmpty(Extract(sng, after, "empty.txt"));
        Assert.IsFalse(File.Exists(sng + SngWriter.TempSuffix));
    }

    [TestMethod]
    public async Task StructurallyBrokenFileIsRefusedAndUntouched()
    {
        var builder = new SngBuilder().AddMetadata("name", "A").AddFile("notes.chart", [1, 2, 3]);
        builder.LengthOverrides[0] = 1000;
        string sng = Path.Combine(_root.FullName, "broken.sng");
        byte[] bytes = builder.Build();
        File.WriteAllBytes(sng, bytes);

        var findings = await SngWriter.RewriteMetadataAsync(sng, [KeyValuePair.Create("name", "B")], TestContext.CancellationToken);

        Assert.Contains(f => f.Severity == Severity.Error, findings);
        Assert.AreSequenceEqual(bytes, File.ReadAllBytes(sng));
        Assert.IsFalse(File.Exists(sng + SngWriter.TempSuffix));
    }

    [TestMethod]
    public async Task GapsBetweenFilesAreCompacted()
    {
        var builder = new SngBuilder().AddMetadata("name", "A").AddFile("notes.chart", [1, 2, 3]);
        builder.Trailing = [9, 9, 9];
        string sng = Path.Combine(_root.FullName, "trailing.sng");
        File.WriteAllBytes(sng, builder.Build());

        await SngWriter.RewriteMetadataAsync(sng, [KeyValuePair.Create("name", "B")], TestContext.CancellationToken);

        var after = SngReader.Read(sng, TestContext.CancellationToken);
        Assert.IsEmpty(after.Findings);
        Assert.AreSequenceEqual(new byte[] { 1, 2, 3 }, Raw(sng, after.Files.Single()));
    }

    [TestMethod]
    public async Task CancelKeepsOriginalAndLeavesNoTemp()
    {
        string sng = await CreateSngAsync([KeyValuePair.Create("name", "Old")], ("notes.chart", [1, 2, 3]));
        byte[] bytes = File.ReadAllBytes(sng);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            SngWriter.RewriteMetadataAsync(sng, [KeyValuePair.Create("name", "New")], cts.Token));

        Assert.AreSequenceEqual(bytes, File.ReadAllBytes(sng));
        Assert.IsFalse(File.Exists(sng + SngWriter.TempSuffix));
    }

    [TestMethod]
    public async Task ReplacingAFileKeepsTheOthersAndMasksTheNewOne()
    {
        byte[] audio = new byte[(1024 * 1024) + 333];
        new Random(3).NextBytes(audio);
        byte[] oldArt = [0xFF, 0xD8, 0xFF, 1, 2, 3];
        byte[] newArt = new byte[(1024 * 1024) + 55];
        new Random(4).NextBytes(newArt);
        string sng = await CreateSngAsync(
            [KeyValuePair.Create("name", "A")],
            ("album.jpg", oldArt), ("song.ogg", audio), ("notes.chart", [1, 2, 3]));
        var before = SngReader.Read(sng, TestContext.CancellationToken);
        string source = WriteSource(newArt);

        var findings = await SngWriter.RewriteAsync(
            sng, [KeyValuePair.Create("name", "A")], ["ALBUM.JPG"], [new SngSourceFile("album.png", source, newArt.Length)], TestContext.CancellationToken);

        Assert.DoesNotContain(f => f.Severity == Severity.Error, findings);
        var after = SngReader.Read(sng, TestContext.CancellationToken);
        Assert.IsEmpty(after.Findings);
        Assert.AreSequenceEqual(before.Header!.Mask.ToArray(), after.Header!.Mask.ToArray());
        Assert.AreSequenceEqual(["song.ogg", "notes.chart", "album.png"], [.. after.Files.Select(f => f.Name)]);
        Assert.AreSequenceEqual(audio, Extract(sng, after, "song.ogg"));
        Assert.AreSequenceEqual(new byte[] { 1, 2, 3 }, Extract(sng, after, "notes.chart"));
        Assert.AreSequenceEqual(newArt, Extract(sng, after, "album.png"));
        Assert.IsFalse(File.Exists(sng + SngWriter.TempSuffix));
    }

    [TestMethod]
    public async Task ARewriteWaitsForAReaderToLetGo()
    {
        // Issue #89: another Chart Packer playing the song holds it open until it sees the song's lock.
        string sng = await CreateSngAsync([KeyValuePair.Create("name", "A")], ("notes.chart", [1, 2, 3]));
        var reader = File.OpenHandle(sng, FileMode.Open, FileAccess.Read, FileShare.Read);
        Task letGo = Task.Delay(400, TestContext.CancellationToken).ContinueWith(_ => reader.Dispose(), TaskScheduler.Default);

        var findings = await SngWriter.RewriteMetadataAsync(sng, [KeyValuePair.Create("name", "B")], TestContext.CancellationToken);

        await letGo;
        Assert.DoesNotContain(f => f.Severity == Severity.Error, findings);
        var after = SngReader.Read(sng, TestContext.CancellationToken);
        Assert.AreEqual("B", after.Metadata.Single(p => p.Key == "name").Value);
        Assert.AreSequenceEqual(new byte[] { 1, 2, 3 }, Extract(sng, after, "notes.chart"));
        Assert.IsFalse(File.Exists(sng + SngWriter.TempSuffix));
    }

    [TestMethod]
    public async Task RemovingAFileLeavesTheRest()
    {
        string sng = await CreateSngAsync([KeyValuePair.Create("name", "A")], ("album.png", [9, 9]), ("notes.chart", [1, 2, 3]));

        await SngWriter.RewriteAsync(sng, [KeyValuePair.Create("name", "A")], ["album.png", "not-there.txt"], [], TestContext.CancellationToken);

        var after = SngReader.Read(sng, TestContext.CancellationToken);
        Assert.IsEmpty(after.Findings);
        Assert.AreEqual("notes.chart", after.Files.Single().Name);
        Assert.AreSequenceEqual(new byte[] { 1, 2, 3 }, Extract(sng, after, "notes.chart"));
    }

    [TestMethod]
    public async Task AddingAFileAppendsIt()
    {
        string sng = await CreateSngAsync([KeyValuePair.Create("name", "A")], ("notes.chart", [1, 2, 3]));
        string source = WriteSource([7, 7, 7, 7]);

        await SngWriter.RewriteAsync(sng, [KeyValuePair.Create("name", "A")], [], [new SngSourceFile("album.jpg", source, 4)], TestContext.CancellationToken);

        var after = SngReader.Read(sng, TestContext.CancellationToken);
        Assert.IsEmpty(after.Findings);
        Assert.AreSequenceEqual(["notes.chart", "album.jpg"], [.. after.Files.Select(f => f.Name)]);
        Assert.AreSequenceEqual(new byte[] { 7, 7, 7, 7 }, Extract(sng, after, "album.jpg"));
    }

    [TestMethod]
    public async Task AddingANameThatIsKeptThrowsAndLeavesTheFile()
    {
        string sng = await CreateSngAsync([KeyValuePair.Create("name", "A")], ("album.png", [9, 9]));
        byte[] bytes = File.ReadAllBytes(sng);
        string source = WriteSource([1]);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            SngWriter.RewriteAsync(sng, [KeyValuePair.Create("name", "A")], [], [new SngSourceFile("ALBUM.png", source, 1)], TestContext.CancellationToken));

        Assert.AreSequenceEqual(bytes, File.ReadAllBytes(sng));
        Assert.IsFalse(File.Exists(sng + SngWriter.TempSuffix));
    }

    private string WriteSource(byte[] data)
    {
        string path = Path.Combine(_root.FullName, Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, data);
        return path;
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
        await SngWriter.WriteAsync(sng, SngMask.Generate(), metadata, sources, overwrite: false, cancellationToken: TestContext.CancellationToken);
        return sng;
    }

    private static byte[] Extract(string sng, SngReadResult read, string name)
    {
        using var handle = File.OpenHandle(sng);
        using var output = new MemoryStream();
        SngFileData.CopyUnmasked(handle, read.Files.Single(f => f.Name == name), read.Header!.Mask.Span, output);
        return output.ToArray();
    }

    private static byte[] Raw(string sng, SngFileEntry entry)
    {
        using var handle = File.OpenHandle(sng);
        using var output = new MemoryStream();
        SngFileData.CopyRaw(handle, entry, output);
        return output.ToArray();
    }

    public TestContext TestContext { get; set; }
}
