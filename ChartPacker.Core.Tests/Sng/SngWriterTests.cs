using ChartPacker.Core.Sng;

namespace ChartPacker.Core.Tests.Sng;

[TestClass]
public sealed class SngWriterTests
{
    private static readonly byte[] ExampleMask = Enumerable.Range(0x10, 16).Select(b => (byte)b).ToArray();

    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-writer-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public async Task WorkedExampleIsByteIdentical()
    {
        string target = Path.Combine(_root.FullName, "out.sng");

        await SngWriter.WriteAsync(target, ExampleMask, [KeyValuePair.Create("name", "Test")], [Source("notes.chart", "ABC"u8.ToArray())], overwrite: false);

        CollectionAssert.AreEqual(SngReaderTests.WorkedExample, File.ReadAllBytes(target));
    }

    [TestMethod]
    public async Task LargeFilesAcrossChunksReadBackCleanly()
    {
        byte[] big = new byte[(1024 * 1024 * 2) + 333];
        new Random(5).NextBytes(big);
        string target = Path.Combine(_root.FullName, "big.sng");

        var findings = await SngWriter.WriteAsync(
            target,
            SngMask.Generate(),
            [KeyValuePair.Create("name", "Big")],
            [Source("song.ogg", big), Source("notes.chart", "x"u8.ToArray()), Source("empty.txt", [])],
            overwrite: false,
            verify: true);

        Assert.IsEmpty(findings);
        var read = SngReader.Read(target);
        Assert.IsEmpty(read.Findings);
        CollectionAssert.AreEqual(big, Extract(target, read, "song.ogg"));
        CollectionAssert.AreEqual("x"u8.ToArray(), Extract(target, read, "notes.chart"));
    }

    [TestMethod]
    public async Task ExistingTargetIsKeptWithoutOverwrite()
    {
        string target = Path.Combine(_root.FullName, "out.sng");
        File.WriteAllText(target, "keep");

        await Assert.ThrowsAsync<IOException>(() =>
            SngWriter.WriteAsync(target, ExampleMask, [], [Source("notes.chart", [1])], overwrite: false));

        Assert.AreEqual("keep", File.ReadAllText(target));
        Assert.IsFalse(File.Exists(target + SngWriter.TempSuffix));
    }

    [TestMethod]
    public async Task OverwriteReplacesTarget()
    {
        string target = Path.Combine(_root.FullName, "out.sng");
        File.WriteAllText(target, new string('x', 10_000));

        await SngWriter.WriteAsync(target, ExampleMask, [KeyValuePair.Create("name", "Test")], [Source("notes.chart", "ABC"u8.ToArray())], overwrite: true);

        CollectionAssert.AreEqual(SngReaderTests.WorkedExample, File.ReadAllBytes(target));
    }

    [TestMethod]
    public async Task CancelLeavesNoTempAndKeepsTarget()
    {
        string target = Path.Combine(_root.FullName, "out.sng");
        File.WriteAllText(target, "keep");
        using var cts = new CancellationTokenSource();
        var progress = new CancelOnReport(cts);

        await Assert.ThrowsAsync<OperationCanceledException>(() => SngWriter.WriteAsync(
            target, ExampleMask, [], [Source("a.bin", new byte[3 * 1024 * 1024])], overwrite: true, bytesWritten: progress, cancellationToken: cts.Token));

        Assert.AreEqual("keep", File.ReadAllText(target));
        Assert.IsFalse(File.Exists(target + SngWriter.TempSuffix));
    }

    [TestMethod]
    public async Task SourceThatChangedSizeFails()
    {
        string target = Path.Combine(_root.FullName, "out.sng");
        var source = Source("notes.chart", [1, 2, 3]) with { Length = 5 };

        await Assert.ThrowsAsync<IOException>(() => SngWriter.WriteAsync(target, ExampleMask, [], [source], overwrite: false));

        Assert.IsFalse(File.Exists(target));
        Assert.IsFalse(File.Exists(target + SngWriter.TempSuffix));
    }

    private SngSourceFile Source(string name, byte[] data)
    {
        string path = Path.Combine(_root.FullName, Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, data);
        return new SngSourceFile(name, path, data.Length);
    }

    private static byte[] Extract(string sng, SngReadResult read, string name)
    {
        using var handle = File.OpenHandle(sng);
        using var output = new MemoryStream();
        SngFileData.CopyUnmasked(handle, read.Files.Single(f => f.Name == name), read.Header!.Mask.Span, output);
        return output.ToArray();
    }

    /// <summary>Cancels as soon as the first chunk is reported, on the writing thread.</summary>
    private sealed class CancelOnReport(CancellationTokenSource cts) : IProgress<long>
    {
        public void Report(long value) => cts.Cancel();
    }
}
