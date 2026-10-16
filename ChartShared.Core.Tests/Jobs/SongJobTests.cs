using ChartShared.Core.Jobs;
using ChartShared.Core.Scanning;
using ChartShared.Core.Sng;
using ChartShared.Core.Tests.Sng;
using ChartShared.Core.Validation;

namespace ChartShared.Core.Tests.Jobs;

[TestClass]
public sealed class SongJobTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-job-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public async Task PackSkipsSngFilesAndPacksFoldersBesideThemselves()
    {
        SongFolder("Artist/Song A");
        WriteBytes("Artist/Old.sng", SngReaderTests.WorkedExample);

        var results = await RunAsync(JobKind.Pack, OverwritePolicy.Skip);

        var skipped = results.Single(r => r.Entry.Kind == SongEntryKind.SngFile);
        Assert.AreEqual(JobItemStatus.Skipped, skipped.Status);
        Assert.AreEqual(SkipReason.AlreadyPacked, skipped.SkipReason);
        var packed = results.Single(r => r.Entry.Kind == SongEntryKind.SongFolder);
        Assert.AreEqual(JobItemStatus.Succeeded, packed.Status, Describe(packed));
        Assert.AreEqual(P("Artist/Song A.sng"), packed.OutputPath);
        Assert.IsTrue(File.Exists(packed.OutputPath));
    }

    [TestMethod]
    public async Task UnpackSkipsFoldersAndUnpacksSngBesideItself()
    {
        SongFolder("Artist/Song A");
        WriteBytes("Artist/Old.sng", SngReaderTests.WorkedExample);

        var results = await RunAsync(JobKind.Unpack, OverwritePolicy.Skip);

        Assert.AreEqual(SkipReason.AlreadyUnpacked, results.Single(r => r.Entry.Kind == SongEntryKind.SongFolder).SkipReason);
        var unpacked = results.Single(r => r.Entry.Kind == SongEntryKind.SngFile);
        Assert.AreEqual(JobItemStatus.Succeeded, unpacked.Status, Describe(unpacked));
        Assert.AreEqual(P("Artist/Old"), unpacked.OutputPath);
        Assert.AreEqual("ABC", File.ReadAllText(Path.Combine(unpacked.OutputPath!, "notes.chart")));
    }

    [TestMethod]
    public async Task OneCorruptItemDoesNotStopTheJob()
    {
        WriteBytes("a.sng", "garbage garbage garbage garbage"u8.ToArray());
        WriteBytes("b.sng", SngReaderTests.WorkedExample);

        var results = await RunAsync(JobKind.Unpack, OverwritePolicy.Skip);

        Assert.HasCount(2, results);
        Assert.AreEqual(JobItemStatus.Failed, results[0].Status);
        Assert.AreEqual(SngRules.Sng001, results[0].Findings.Single().RuleId);
        Assert.AreEqual(JobItemStatus.Succeeded, results[1].Status);
    }

    [TestMethod]
    public async Task PlanErrorsFailTheItem()
    {
        SongFolder("Bad", ini: "[song]\nname = a;b\n");
        SongFolder("Good");

        var results = await RunAsync(JobKind.Pack, OverwritePolicy.Skip);

        Assert.AreEqual(JobItemStatus.Failed, results.Single(r => r.Entry.DisplayName == "Bad").Status);
        Assert.IsFalse(File.Exists(P("Bad.sng")));
        Assert.AreEqual(JobItemStatus.Succeeded, results.Single(r => r.Entry.DisplayName == "Good").Status);
    }

    [TestMethod]
    public async Task SkipPolicyLeavesExistingOutput()
    {
        SongFolder("Song");
        File.WriteAllText(P("Song.sng"), "keep");

        var result = (await RunAsync(JobKind.Pack, OverwritePolicy.Skip)).Single(r => r.Entry.Kind == SongEntryKind.SongFolder);

        Assert.AreEqual(JobItemStatus.Skipped, result.Status);
        Assert.AreEqual(SkipReason.OutputExists, result.SkipReason);
        Assert.AreEqual("keep", File.ReadAllText(P("Song.sng")));
    }

    [TestMethod]
    public async Task OverwritePolicyReplacesExistingOutput()
    {
        SongFolder("Song");
        File.WriteAllText(P("Song.sng"), "old");

        var result = (await RunAsync(JobKind.Pack, OverwritePolicy.Overwrite)).Single(r => r.Entry.Kind == SongEntryKind.SongFolder);

        Assert.AreEqual(JobItemStatus.Succeeded, result.Status, Describe(result));
        Assert.AreEqual(P("Song.sng"), result.OutputPath);
        Assert.AreNotEqual("old", File.ReadAllText(P("Song.sng")));
    }

    [TestMethod]
    public async Task RenamePolicyPicksFreeName()
    {
        WriteBytes("Song.sng", SngReaderTests.WorkedExample);
        Directory.CreateDirectory(P("Song"));
        Directory.CreateDirectory(P("Song (2)"));

        var result = (await RunAsync(JobKind.Unpack, OverwritePolicy.Rename, maxDepth: 0)).Single();

        Assert.AreEqual(JobItemStatus.Succeeded, result.Status, Describe(result));
        Assert.AreEqual(P("Song (3)"), result.OutputPath);
    }

    [TestMethod]
    public async Task OutputsMirrorUnderCustomDestination()
    {
        SongFolder("Artist/Song");
        string destination = Directory.CreateDirectory(Path.Combine(_root.FullName, "..", _root.Name + "-out")).FullName;
        try
        {
            var result = (await RunAsync(JobKind.Pack, OverwritePolicy.Skip, destination: destination)).Single();

            Assert.AreEqual(Path.Combine(destination, "Artist", "Song.sng"), result.OutputPath);
            Assert.IsTrue(File.Exists(result.OutputPath));
        }
        finally
        {
            Directory.Delete(destination, recursive: true);
        }
    }

    [TestMethod]
    public async Task FlatLayoutPacksDirectlyIntoDestination()
    {
        SongFolder("Artist/Album/Song");
        string destination = Directory.CreateDirectory(P("out")).FullName;

        var result = (await RunAsync(JobKind.Pack, OverwritePolicy.Skip, maxDepth: 3, destination: destination, layout: OutputLayout.Flat)).Single();

        Assert.AreEqual(Path.Combine(destination, "Song.sng"), result.OutputPath);
        Assert.IsTrue(File.Exists(result.OutputPath));
    }

    [TestMethod]
    public async Task FlatLayoutWithDefaultDestinationPacksIntoScannedFolder()
    {
        SongFolder("Artist/Song");

        var result = (await RunAsync(JobKind.Pack, OverwritePolicy.Skip, layout: OutputLayout.Flat)).Single();

        Assert.AreEqual(P("Song.sng"), result.OutputPath);
    }

    [TestMethod]
    [DataRow(OverwritePolicy.Skip)]
    [DataRow(OverwritePolicy.Overwrite)]
    public async Task FlatDuplicatesAfterTheFirstAreSkipped(OverwritePolicy policy)
    {
        SongFolder("A/Song", ini: "[song]\nname = From A\n");
        SongFolder("B/Song", ini: "[song]\nname = From B\n");

        var results = await RunAsync(JobKind.Pack, policy, layout: OutputLayout.Flat);

        Assert.AreEqual(JobItemStatus.Succeeded, results[0].Status, Describe(results[0]));
        Assert.AreEqual(P("Song.sng"), results[0].OutputPath);
        Assert.AreEqual(JobItemStatus.Skipped, results[1].Status);
        Assert.AreEqual(SkipReason.DuplicateOutput, results[1].SkipReason);
        Assert.AreEqual(P("Song.sng"), results[1].OutputPath);
        Assert.AreEqual(results[0].Entry, results[1].ConflictsWith);
        // The first item's output wasn't overwritten by the second.
        Assert.AreEqual("From A", SngReader.Read(P("Song.sng"), TestContext.CancellationToken).Metadata.Single().Value);
    }

    [TestMethod]
    public async Task FlatDuplicatesAreRenamedUnderRename()
    {
        SongFolder("A/Song");
        SongFolder("B/Song");
        SongFolder("C/Song");

        var results = await RunAsync(JobKind.Pack, OverwritePolicy.Rename, layout: OutputLayout.Flat);

        Assert.AreSequenceEqual(
            [P("Song.sng"), P("Song (2).sng"), P("Song (3).sng")], results.Select(r => r.OutputPath).ToArray());
        Assert.IsTrue(results.All(r => r.Status == JobItemStatus.Succeeded));
    }

    [TestMethod]
    public async Task FlatDuplicatesIgnoreCaseWhereTheFileSystemDoes()
    {
        SongFolder("A/Song");
        SongFolder("B/SONG");

        var results = await RunAsync(JobKind.Pack, OverwritePolicy.Skip, layout: OutputLayout.Flat);

        var expected = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? SkipReason.DuplicateOutput : SkipReason.None;
        Assert.AreEqual(expected, results[1].SkipReason);
    }

    [TestMethod]
    public async Task FailedFirstDuplicateLeavesTheNameFree()
    {
        SongFolder("A/Song", ini: "[song]\nname = bad;value\n");
        SongFolder("B/Song");

        var results = await RunAsync(JobKind.Pack, OverwritePolicy.Skip, layout: OutputLayout.Flat);

        Assert.AreEqual(JobItemStatus.Failed, results[0].Status);
        Assert.AreEqual(JobItemStatus.Succeeded, results[1].Status, Describe(results[1]));
        Assert.AreEqual(P("Song.sng"), results[1].OutputPath);
    }

    [TestMethod]
    public async Task ExistingOutputFromBeforeTheJobIsStillOutputExists()
    {
        SongFolder("A/Song");
        File.WriteAllText(P("Song.sng"), "keep");

        var result = (await RunAsync(JobKind.Pack, OverwritePolicy.Skip, layout: OutputLayout.Flat)).Single(r => r.Entry.Kind == SongEntryKind.SongFolder);

        Assert.AreEqual(SkipReason.OutputExists, result.SkipReason);
        Assert.AreEqual("keep", File.ReadAllText(P("Song.sng")));
    }

    [TestMethod]
    public void RootSongFolderPacksBesideItself()
    {
        var entry = new SongEntry(SongEntryKind.SongFolder, _root.FullName, ".", _root.Name);

        string target = OutputPaths.PackTarget(entry, _root.FullName, _root.FullName);

        Assert.AreEqual(Path.Combine(_root.Parent!.FullName, _root.Name + ".sng"), target);
    }

    [TestMethod]
    public async Task CancelStopsTheJob()
    {
        SongFolder("A");
        SongFolder("B");
        using var cts = new CancellationTokenSource();
        var results = new List<JobItemResult>();
        var progress = new SyncProgress(r =>
        {
            results.Add(r);
            cts.Cancel();
        });
        var entries = SongLibraryScanner.Enumerate(_root.FullName, 2, TestContext.CancellationToken).ToList();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            SongJob.RunAsync(JobKind.Pack, entries, _root.FullName, _root.FullName, OverwritePolicy.Skip, progress, cancellationToken: cts.Token));

        Assert.HasCount(1, results);
        Assert.IsFalse(File.Exists(P("B.sng")));
        Assert.IsEmpty(Directory.GetFiles(_root.FullName, "*.tmp", SearchOption.AllDirectories));
    }

    private async Task<List<JobItemResult>> RunAsync(
        JobKind kind,
        OverwritePolicy policy,
        int maxDepth = 2,
        string? destination = null,
        OutputLayout layout = OutputLayout.Mirror)
    {
        var entries = SongLibraryScanner.Enumerate(_root.FullName, maxDepth, TestContext.CancellationToken).ToList();
        var results = new List<JobItemResult>();
        await SongJob.RunAsync(kind, entries, _root.FullName, destination ?? _root.FullName, policy, new SyncProgress(results.Add), layout, cancellationToken: TestContext.CancellationToken);
        return results;
    }

    private void SongFolder(string relative, string ini = "[song]\nname = Test\n")
    {
        string folder = Directory.CreateDirectory(P(relative)).FullName;
        File.WriteAllText(Path.Combine(folder, "song.ini"), ini);
        File.WriteAllText(Path.Combine(folder, "notes.chart"), "chart");
        File.WriteAllText(Path.Combine(folder, "song.ogg"), "audio");
    }

    private void WriteBytes(string relative, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(P(relative))!);
        File.WriteAllBytes(P(relative), bytes);
    }

    private string P(string relative) => Path.Combine(_root.FullName, relative.Replace('/', Path.DirectorySeparatorChar));

    private static string Describe(JobItemResult result) =>
        string.Join(Environment.NewLine, result.Findings.Select(f => $"{f.RuleId}: {f.Message}").Append(result.ErrorMessage ?? ""));

    /// <summary>Reports on the calling thread, so results are complete when the job returns.</summary>
    private sealed class SyncProgress(Action<JobItemResult> report) : IProgress<JobItemResult>
    {
        public void Report(JobItemResult value) => report(value);
    }

    public TestContext TestContext { get; set; }
}
