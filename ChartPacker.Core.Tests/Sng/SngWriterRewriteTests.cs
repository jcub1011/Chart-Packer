using ChartPacker.Core.Sng;
using ChartPacker.Core.Validation;

namespace ChartPacker.Core.Tests.Sng;

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
        var before = SngReader.Read(sng);

        var findings = await SngWriter.RewriteMetadataAsync(
            sng, [KeyValuePair.Create("name", "New Title"), KeyValuePair.Create("artist", "Band")]);

        Assert.IsFalse(findings.Any(f => f.Severity == Severity.Error));
        var after = SngReader.Read(sng);
        Assert.IsEmpty(after.Findings);
        CollectionAssert.AreEqual(before.Header!.Mask.ToArray(), after.Header!.Mask.ToArray());
        CollectionAssert.AreEqual(
            new[] { ("name", "New Title"), ("artist", "Band") },
            after.Metadata.Select(p => (p.Key, p.Value)).ToArray());
        CollectionAssert.AreEqual(before.Files.Select(f => (f.Name, f.Length)).ToArray(), after.Files.Select(f => (f.Name, f.Length)).ToArray());
        CollectionAssert.AreEqual(chart, Extract(sng, after, "notes.chart"));
        CollectionAssert.AreEqual(audio, Extract(sng, after, "song.ogg"));
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

        var findings = await SngWriter.RewriteMetadataAsync(sng, [KeyValuePair.Create("name", "B")]);

        Assert.IsTrue(findings.Any(f => f.Severity == Severity.Error));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(sng));
        Assert.IsFalse(File.Exists(sng + SngWriter.TempSuffix));
    }

    [TestMethod]
    public async Task GapsBetweenFilesAreCompacted()
    {
        var builder = new SngBuilder().AddMetadata("name", "A").AddFile("notes.chart", [1, 2, 3]);
        builder.Trailing = [9, 9, 9];
        string sng = Path.Combine(_root.FullName, "trailing.sng");
        File.WriteAllBytes(sng, builder.Build());

        await SngWriter.RewriteMetadataAsync(sng, [KeyValuePair.Create("name", "B")]);

        var after = SngReader.Read(sng);
        Assert.IsEmpty(after.Findings);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, Raw(sng, after.Files.Single()));
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

        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(sng));
        Assert.IsFalse(File.Exists(sng + SngWriter.TempSuffix));
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
}
