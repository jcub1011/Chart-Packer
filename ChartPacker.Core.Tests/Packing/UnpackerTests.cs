using ChartPacker.Core.Ini;
using ChartPacker.Core.Packing;
using ChartPacker.Core.Sng;
using ChartPacker.Core.Tests.Sng;
using ChartPacker.Core.Validation;

namespace ChartPacker.Core.Tests.Packing;

[TestClass]
public sealed class UnpackerTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-unpack-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public async Task WorkedExampleUnpacks()
    {
        string sng = WriteBytes("example.sng", SngReaderTests.WorkedExample);
        string target = Path.Combine(_root.FullName, "example");

        var result = await Unpacker.UnpackAsync(sng, target, overwrite: false, TestContext.CancellationToken);

        Assert.IsTrue(result.Extracted);
        Assert.IsEmpty(result.Findings);
        Assert.AreEqual("ABC", File.ReadAllText(Path.Combine(target, "notes.chart")));
        StringAssert.Contains(File.ReadAllText(Path.Combine(target, "song.ini")), "name = Test");
        AssertNoTempFolders();
    }

    [TestMethod]
    [DataRow("../evil.txt")]
    [DataRow("/etc/x")]
    [DataRow("sub/../../evil.txt")]
    [DataRow("C:/evil.txt")]
    [DataRow("..\\evil.txt")]
    public async Task TraversalWritesNothingOutsideTheOutputFolder(string name)
    {
        string sng = await CraftAsync([(name, "evil"u8.ToArray()), ("notes.chart", "ok"u8.ToArray())]);
        string outer = Path.Combine(_root.FullName, "outer");
        string target = Path.Combine(outer, "song");
        var before = Directory.EnumerateFileSystemEntries(_root.FullName, "*", SearchOption.AllDirectories).ToHashSet();

        var result = await Unpacker.UnpackAsync(sng, target, overwrite: false, TestContext.CancellationToken);

        Assert.IsTrue(result.CompletedWithErrors);
        Assert.AreEqual(SngRules.Sng033, result.Findings.Single(f => f.Severity == Severity.Error).RuleId);
        var created = Directory.EnumerateFileSystemEntries(_root.FullName, "*", SearchOption.AllDirectories)
            .Where(p => !before.Contains(p))
            .ToList();
        Assert.IsTrue(
            created.All(p => p == outer || p.StartsWith(target, StringComparison.Ordinal)),
            string.Join(Environment.NewLine, created));
        Assert.AreSequenceEqual(["notes.chart", "song.ini"], Directory.GetFiles(target).Select(Path.GetFileName).ToArray(), Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    public async Task StoredSongIniIsSkippedWithWarning()
    {
        string sng = await CraftAsync([("song.ini", "[song]\nname = Stored\n"u8.ToArray())], [KeyValuePair.Create("name", "Generated")]);
        string target = Path.Combine(_root.FullName, "song");

        var result = await Unpacker.UnpackAsync(sng, target, overwrite: false, TestContext.CancellationToken);

        Assert.AreEqual(SngRules.Sng053, result.Findings.Single().RuleId);
        StringAssert.Contains(File.ReadAllText(Path.Combine(target, "song.ini")), "name = Generated");
    }

    [TestMethod]
    public async Task StructuralErrorStopsTheUnpack()
    {
        string sng = WriteBytes("bad.sng", "NOTSNG-and-more-bytes-than-a-header"u8.ToArray());
        string target = Path.Combine(_root.FullName, "bad");

        var result = await Unpacker.UnpackAsync(sng, target, overwrite: false, TestContext.CancellationToken);

        Assert.IsFalse(result.Extracted);
        Assert.AreEqual(SngRules.Sng001, result.Findings.Single().RuleId);
        Assert.IsFalse(Directory.Exists(target));
        AssertNoTempFolders();
    }

    [TestMethod]
    public async Task ExistingTargetThrowsWithoutOverwrite()
    {
        string sng = WriteBytes("example.sng", SngReaderTests.WorkedExample);
        string target = Directory.CreateDirectory(Path.Combine(_root.FullName, "example")).FullName;

        await Assert.ThrowsAsync<IOException>(() => Unpacker.UnpackAsync(sng, target, overwrite: false, TestContext.CancellationToken));

        Assert.IsEmpty(Directory.GetFileSystemEntries(target));
    }

    [TestMethod]
    public async Task OverwriteReplacesFilesAndKeepsOthers()
    {
        string sng = WriteBytes("example.sng", SngReaderTests.WorkedExample);
        string target = Directory.CreateDirectory(Path.Combine(_root.FullName, "example")).FullName;
        File.WriteAllText(Path.Combine(target, "notes.chart"), "old");
        File.WriteAllText(Path.Combine(target, "mine.txt"), "keep");

        var result = await Unpacker.UnpackAsync(sng, target, overwrite: true, TestContext.CancellationToken);

        Assert.IsTrue(result.Extracted);
        Assert.AreEqual("ABC", File.ReadAllText(Path.Combine(target, "notes.chart")));
        Assert.AreEqual("keep", File.ReadAllText(Path.Combine(target, "mine.txt")));
        AssertNoTempFolders();
    }

    [TestMethod]
    public async Task CancelLeavesNothingBehind()
    {
        string sng = WriteBytes("example.sng", SngReaderTests.WorkedExample);
        string target = Path.Combine(_root.FullName, "example");

        await Assert.ThrowsAsync<OperationCanceledException>(() => Unpacker.UnpackAsync(sng, target, overwrite: false, new CancellationToken(canceled: true)));

        Assert.IsFalse(Directory.Exists(target));
        AssertNoTempFolders();
    }

    [TestMethod]
    public async Task PackThenUnpackRoundTrips()
    {
        string source = Directory.CreateDirectory(Path.Combine(_root.FullName, "Artist - Title")).FullName;
        byte[] audio = new byte[(1024 * 1024) + 17];
        new Random(9).NextBytes(audio);
        File.WriteAllBytes(Path.Combine(source, "song.ogg"), audio);
        File.WriteAllText(Path.Combine(source, "notes.chart"), "[Song]\n{\n}\n");
        File.WriteAllText(Path.Combine(source, "readme.txt"), "hello");
        File.WriteAllText(Path.Combine(source, "song.ini"), "[song]\nname = Title\nartist = Band\nalbum =\ndelay = 0\n");

        var plan = await PackPlanner.PlanAsync(source, cancellationToken: TestContext.CancellationToken);
        string sng = Path.Combine(_root.FullName, "packed.sng");
        var verify = await Packer.PackAsync(plan, sng, overwrite: false, cancellationToken: TestContext.CancellationToken);
        string target = Path.Combine(_root.FullName, "unpacked");
        var result = await Unpacker.UnpackAsync(sng, target, overwrite: false, TestContext.CancellationToken);

        Assert.IsEmpty(verify);
        Assert.IsEmpty(result.Findings);
        foreach (string name in new[] { "song.ogg", "notes.chart", "readme.txt" })
            Assert.AreSequenceEqual(File.ReadAllBytes(Path.Combine(source, name)), File.ReadAllBytes(Path.Combine(target, name)), name);

        var pairs = (await SongIni.ReadAsync(Path.Combine(target, "song.ini"), TestContext.CancellationToken)).Pairs;
        Assert.AreSequenceEqual(
            [KeyValuePair.Create("name", "Title"), KeyValuePair.Create("artist", "Band"), KeyValuePair.Create("delay", "0")], [.. pairs]);
    }

    private void AssertNoTempFolders() =>
        Assert.IsEmpty(Directory.GetDirectories(_root.FullName, "*.chartpacker-tmp", SearchOption.AllDirectories));

    private string WriteBytes(string name, byte[] bytes)
    {
        string path = Path.Combine(_root.FullName, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Writes a <c>.sng</c> with arbitrary stored names, which the planner would refuse.</summary>
    private async Task<string> CraftAsync((string Name, byte[] Data)[] files, KeyValuePair<string, string>[]? metadata = null)
    {
        string sources = Directory.CreateDirectory(Path.Combine(_root.FullName, "sources-" + Guid.NewGuid().ToString("N"))).FullName;
        var list = new List<SngSourceFile>();
        foreach (var (name, data) in files)
        {
            string path = Path.Combine(sources, Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(path, data);
            list.Add(new SngSourceFile(name, path, data.Length));
        }

        string sng = Path.Combine(_root.FullName, Guid.NewGuid().ToString("N") + ".sng");
        await SngWriter.WriteAsync(sng, SngMask.Generate(), metadata ?? [], list, overwrite: false, cancellationToken: TestContext.CancellationToken);
        return sng;
    }

    public TestContext TestContext { get; set; }
}
